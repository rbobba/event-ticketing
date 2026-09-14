using System.Net;
using System.Net.Http.Json;

using Ticketing.Api.Features.Events;

namespace Ticketing.IntegrationTests;

[Collection(nameof(PostgresCollectionDefinition))]
public sealed class ReportingTests(PostgresFixture fixture)
{
    [Fact]
    public async Task AC_3_1_AvailabilityReturnsPerTierRemaining()
    {
        var show = await SeedAsync();
        await BuyAsync(show.Id, show.Tiers[0].Id, 4);

        var availability = await fixture.Client
            .GetFromJsonAsync<AvailabilityResponse>($"/v1/events/{show.Id}/availability");

        Assert.Equal(2, availability!.Tiers.Count);
        Assert.Equal(6, availability.Tiers.Single(t => t.PricingTierId == show.Tiers[0].Id).Remaining);
        Assert.Equal(5, availability.Tiers.Single(t => t.PricingTierId == show.Tiers[1].Id).Remaining);
    }

    [Fact]
    public async Task AC_3_2_SoldOutTierReportsZero()
    {
        var show = await SeedAsync();
        await BuyAsync(show.Id, show.Tiers[0].Id, 10);      // the whole tier

        var availability = await fixture.Client
            .GetFromJsonAsync<AvailabilityResponse>($"/v1/events/{show.Id}/availability");

        var soldOut = availability!.Tiers.SingleOrDefault(t => t.PricingTierId == show.Tiers[0].Id);

        Assert.NotNull(soldOut);        // present, not omitted
        Assert.Equal(0, soldOut!.Remaining);
    }

    [Fact]
    public async Task AC_4_1_SummaryAggregatesByTier()
    {
        var show = await SeedAsync();
        await BuyAsync(show.Id, show.Tiers[0].Id, 3);
        await BuyAsync(show.Id, show.Tiers[1].Id, 2);

        var summary = await fixture.Client
            .GetFromJsonAsync<SalesSummaryResponse>($"/v1/events/{show.Id}/sales-summary");

        var ga = summary!.Tiers.Single(t => t.PricingTierId == show.Tiers[0].Id);
        var vip = summary.Tiers.Single(t => t.PricingTierId == show.Tiers[1].Id);

        Assert.Equal(3, ga.Sold);
        Assert.Equal(7, ga.Remaining);
        Assert.Equal(2, vip.Sold);
        Assert.Equal(3, vip.Remaining);

        Assert.Equal(5, summary.TotalSold);
        Assert.Equal(10, summary.TotalRemaining);
    }

    [Fact]
    public async Task AC_4_2_SummaryWithNoSalesReturnsZeroes()
    {
        var show = await SeedAsync();

        var response = await fixture.Client.GetAsync($"/v1/events/{show.Id}/sales-summary");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);        // not 404

        var summary = await response.Content.ReadFromJsonAsync<SalesSummaryResponse>();

        Assert.Equal(2, summary!.Tiers.Count);
        Assert.Equal(0, summary.TotalSold);
        Assert.Equal(0m, summary.TotalRevenue);
        Assert.All(summary.Tiers, t => Assert.Equal(0, t.Sold));
    }

    [Fact]
    public async Task AC_4_3_RevenueIsExactDecimal()
    {
        // 0.10 x 3 is 0.30000000000000004 in binary floating point, and 33.33 x 3 is
        // 99.99000000000001. Both are exact in decimal.
        var show = await SeedAsync(gaPrice: 0.10m, vipPrice: 33.33m);

        await BuyAsync(show.Id, show.Tiers[0].Id, 3);
        await BuyAsync(show.Id, show.Tiers[1].Id, 3);

        var summary = await fixture.Client
            .GetFromJsonAsync<SalesSummaryResponse>($"/v1/events/{show.Id}/sales-summary");

        Assert.Equal(0.30m, summary!.Tiers.Single(t => t.PricingTierId == show.Tiers[0].Id).Revenue);
        Assert.Equal(99.99m, summary.Tiers.Single(t => t.PricingTierId == show.Tiers[1].Id).Revenue);
        Assert.Equal(100.29m, summary.TotalRevenue);
    }

    // ----- helpers -------------------------------------------------------------

    private async Task<EventResponse> SeedAsync(decimal gaPrice = 45.00m, decimal vipPrice = 120.00m)
    {
        await fixture.ResetAsync();

        var request = new EventRequest(
            "Reporting Show",
            "Seeded by the test.",
            new VenueRequest("The Vic", "America/Chicago"),
            new DateOnly(2026, 11, 14),
            new TimeOnly(19, 30),
            TotalCapacity: 500,
            Tiers:
            [
                new TierRequest("General Admission", gaPrice, 10),
                new TierRequest("VIP", vipPrice, 5),
            ]);

        var created = await fixture.Client.PostAsJsonAsync("/v1/events", request);
        return (await created.Content.ReadFromJsonAsync<EventResponse>())!;
    }

    private async Task BuyAsync(Guid eventId, Guid tierId, int quantity)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/events/{eventId}/orders")
        {
            Content = JsonContent.Create(new { pricingTierId = tierId, quantity }),
        };
        request.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString());

        var response = await fixture.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }
}
