using System.Net;
using System.Net.Http.Json;

using Microsoft.EntityFrameworkCore;

using Ticketing.Api.Features.Orders;
using Ticketing.Domain.Events;

namespace Ticketing.IntegrationTests;

[Collection(nameof(PostgresCollectionDefinition))]
public sealed class PurchaseTests(PostgresFixture fixture)
{
    [Fact]
    public async Task AC_2_1_PurchaseReducesRemaining()
    {
        var (eventId, tierId) = await SeedAsync(allocation: 10);

        var response = await PurchaseAsync(eventId, tierId, quantity: 3);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var order = await response.Content.ReadFromJsonAsync<OrderResponse>();
        Assert.Equal(3, order!.TicketIds.Count);
        Assert.Equal(7, await RemainingAsync(tierId));
    }

    [Fact]
    public async Task AC_2_2_PartialFulfilmentIsRejected()
    {
        var (eventId, tierId) = await SeedAsync(allocation: 2);

        var response = await PurchaseAsync(eventId, tierId, quantity: 5);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(2, await RemainingAsync(tierId));   // nothing partially filled
    }

    [Fact]
    public async Task AC_2_3_ConcurrentPurchasesNeverOversell()
    {
        var (eventId, tierId) = await SeedAsync(allocation: 50);

        const int attempts = 200;

        // A distinct key per request: these are 200 different buyers, not one retrying.
        var responses = await Task.WhenAll(
            Enumerable.Range(0, attempts)
                      .Select(_ => PurchaseAsync(eventId, tierId, quantity: 1)));

        var created = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        var conflicted = responses.Count(r => r.StatusCode == HttpStatusCode.Conflict);

        Assert.Equal(50, created);
        Assert.Equal(attempts - 50, conflicted);
        Assert.Equal(0, await RemainingAsync(tierId));

        // The assertion that actually proves INV-1: a tier reading zero while 51 tickets
        // exist would still be an oversell.
        await using var db = fixture.CreateContext();
        Assert.Equal(50, await db.Tickets.CountAsync());
    }

    [Fact]
    public async Task AC_2_4_ReplayReturnsOriginalOrder()
    {
        var (eventId, tierId) = await SeedAsync(allocation: 10);
        var key = Guid.CreateVersion7().ToString();

        var first = await PurchaseAsync(eventId, tierId, quantity: 2, key);
        var replay = await PurchaseAsync(eventId, tierId, quantity: 2, key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

        var original = await first.Content.ReadFromJsonAsync<OrderResponse>();
        var returned = await replay.Content.ReadFromJsonAsync<OrderResponse>();

        Assert.Equal(original!.Id, returned!.Id);
        Assert.Equal(8, await RemainingAsync(tierId));   // decremented once, not twice
    }

    [Fact]
    public async Task AC_2_5_KeyReuseWithDifferentPayloadConflicts()
    {
        var (eventId, tierId) = await SeedAsync(allocation: 10);
        var key = Guid.CreateVersion7().ToString();

        await PurchaseAsync(eventId, tierId, quantity: 2, key);
        var reused = await PurchaseAsync(eventId, tierId, quantity: 3, key);

        Assert.Equal(HttpStatusCode.Conflict, reused.StatusCode);
        Assert.Equal(8, await RemainingAsync(tierId));
    }

    [Fact]
    public async Task AC_2_6_ClientSuppliedPriceIsIgnored()
    {
        var (eventId, tierId) = await SeedAsync(allocation: 10, price: 45.00m);

        // A hostile client trying to set its own price. The property does not exist on
        // the request type, so it is discarded by the binder.
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/events/{eventId}/orders")
        {
            Content = JsonContent.Create(new { pricingTierId = tierId, quantity = 2, unitPrice = 0.01m, total = 0.02m }),
        };
        request.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString());

        var response = await fixture.Client.SendAsync(request);
        var order = await response.Content.ReadFromJsonAsync<OrderResponse>();

        Assert.Equal(45.00m, order!.UnitPrice);
        Assert.Equal(90.00m, order.Total);
    }

    [Fact]
    public async Task AC_2_7_CannotPurchasePastEvent()
    {
        var (eventId, tierId) = await SeedAsync(allocation: 10, startsOn: new DateOnly(2020, 1, 1));

        var response = await PurchaseAsync(eventId, tierId, quantity: 1);

        Assert.Equal(HttpStatusCode.UnprocessableContent, response.StatusCode);
        Assert.Equal(10, await RemainingAsync(tierId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task AC_2_8_InvalidQuantityRejected(int quantity)
    {
        var (eventId, tierId) = await SeedAsync(allocation: 10);

        var response = await PurchaseAsync(eventId, tierId, quantity);

        Assert.Equal(HttpStatusCode.UnprocessableContent, response.StatusCode);
        Assert.Equal(10, await RemainingAsync(tierId));
    }

    [Fact]
    public async Task AC_2_10_OrderCanBeRetrievedById()
    {
        var (eventId, tierId) = await SeedAsync(allocation: 10);

        var created = await PurchaseAsync(eventId, tierId, quantity: 2);
        var placed = await created.Content.ReadFromJsonAsync<OrderResponse>();

        // Follow the Location header rather than rebuilding the URL — that proves the
        // header points somewhere real.
        var fetched = await fixture.Client.GetAsync(created.Headers.Location);

        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);

        var order = await fetched.Content.ReadFromJsonAsync<OrderResponse>();
        Assert.Equal(placed!.Id, order!.Id);
        Assert.Equal(2, order.Quantity);
        Assert.Equal(2, order.TicketIds.Count);
    }

    [Fact]
    public async Task AC_2_11_UnknownOrderReturns404()
    {
        var response = await fixture.Client.GetAsync($"/v1/orders/{Guid.CreateVersion7()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ----- helpers -------------------------------------------------------------

    private async Task<(Guid EventId, Guid TierId)> SeedAsync(
        int allocation, decimal price = 45.00m, DateOnly? startsOn = null)
    {
        await fixture.ResetAsync();

        var venue = Venue.Create("The Vic", "America/Chicago");
        var show = Event.Create(
            "Test Show", "Seeded by the test.", venue,
            startsOn ?? DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)),
            new TimeOnly(19, 30),
            totalCapacity: allocation);

        var tier = show.AddTier("General Admission", price, allocation);

        await using var db = fixture.CreateContext();
        db.Events.Add(show);
        await db.SaveChangesAsync();

        return (show.Id, tier.Id);
    }

    private Task<HttpResponseMessage> PurchaseAsync(
        Guid eventId, Guid tierId, int quantity, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/events/{eventId}/orders")
        {
            Content = JsonContent.Create(new { pricingTierId = tierId, quantity }),
        };

        request.Headers.Add("Idempotency-Key", idempotencyKey ?? Guid.CreateVersion7().ToString());

        return fixture.Client.SendAsync(request);
    }

    private async Task<int> RemainingAsync(Guid tierId)
    {
        await using var db = fixture.CreateContext();
        return await db.PricingTiers.Where(t => t.Id == tierId)
                                    .Select(t => t.Remaining)
                                    .SingleAsync();
    }
}
