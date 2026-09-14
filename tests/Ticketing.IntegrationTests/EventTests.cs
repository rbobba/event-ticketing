using System.Net;
using System.Net.Http.Json;

using Ticketing.Api.Features.Events;
using Ticketing.Api.Features.Orders;

namespace Ticketing.IntegrationTests;

[Collection(nameof(PostgresCollectionDefinition))]
public sealed class EventTests(PostgresFixture fixture)
{
    [Fact]
    public async Task AC_1_1_CreateEventReturns201WithLocation()
    {
        await fixture.ResetAsync();

        var response = await fixture.Client.PostAsJsonAsync("/v1/events", ValidRequest());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);

        // The Location header must point somewhere real, not just be well formed.
        var fetched = await fixture.Client.GetAsync(response.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
    }

    [Fact]
    public async Task AC_1_2_TierAllocationsCannotExceedCapacity()
    {
        await fixture.ResetAsync();

        var request = ValidRequest(totalCapacity: 500, tiers:
        [
            new TierRequest("General Admission", 45.00m, 400),
            new TierRequest("VIP", 120.00m, 300),          // 700 > 500
        ]);

        var response = await fixture.Client.PostAsJsonAsync("/v1/events", request);

        Assert.Equal(HttpStatusCode.UnprocessableContent, response.StatusCode);

        // "and nothing is created" — the whole aggregate rolls back, not just the tier.
        var all = await fixture.Client.GetFromJsonAsync<List<EventResponse>>("/v1/events");
        Assert.Empty(all!);
    }

    [Fact]
    public async Task AC_1_3_UnknownEventReturns404()
    {
        var response = await fixture.Client.GetAsync($"/v1/events/{Guid.CreateVersion7()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task AC_1_4_StaleIfMatchReturns412()
    {
        await fixture.ResetAsync();

        var created = await fixture.Client.PostAsJsonAsync("/v1/events", ValidRequest());
        var url = created.Headers.Location!;

        var read = await fixture.Client.GetAsync(url);
        var staleTag = read.Headers.ETag!.Tag;          // captured before the row moves on

        // Someone else updates it, so the tag we are holding goes stale.
        var winner = await Put(url, ValidRequest(name: "Renamed By Someone Else"), etag: null);
        Assert.Equal(HttpStatusCode.OK, winner.StatusCode);

        var loser = await Put(url, ValidRequest(name: "My Rename"), etag: staleTag);

        Assert.Equal(HttpStatusCode.PreconditionFailed, loser.StatusCode);

        var after = await fixture.Client.GetFromJsonAsync<EventResponse>(url);
        Assert.Equal("Renamed By Someone Else", after!.Name);   // unchanged by the loser
    }

    [Fact]
    public async Task AC_1_5_CannotDeleteEventWithSales()
    {
        await fixture.ResetAsync();

        var created = await fixture.Client.PostAsJsonAsync("/v1/events", ValidRequest());
        var show = await created.Content.ReadFromJsonAsync<EventResponse>();

        using var purchase = new HttpRequestMessage(
            HttpMethod.Post, $"/v1/events/{show!.Id}/orders")
        {
            Content = JsonContent.Create(new { pricingTierId = show.Tiers[0].Id, quantity = 1 }),
        };
        purchase.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString());
        await fixture.Client.SendAsync(purchase);

        var deleted = await fixture.Client.DeleteAsync($"/v1/events/{show.Id}");

        Assert.Equal(HttpStatusCode.Conflict, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await fixture.Client.GetAsync($"/v1/events/{show.Id}")).StatusCode);
    }

    [Fact]
    public async Task AC_1_6_EventTimeIsUtcPlusZone()
    {
        await fixture.ResetAsync();

        var created = await fixture.Client.PostAsJsonAsync("/v1/events", ValidRequest());
        var show = await created.Content.ReadFromJsonAsync<EventResponse>();

        Assert.Equal("America/Chicago", show!.Venue.TimeZoneId);
        Assert.Equal(TimeSpan.Zero, show.StartsAtUtc.Offset);   // a UTC instant, not local
    }

    [Fact]
    public async Task AC_1_7_LocalDateAndTimeRoundTrip()
    {
        await fixture.ResetAsync();

        var date = new DateOnly(2026, 11, 14);
        var time = new TimeOnly(19, 30);

        var created = await fixture.Client.PostAsJsonAsync("/v1/events", ValidRequest(date: date, time: time));
        var show = await created.Content.ReadFromJsonAsync<EventResponse>();

        Assert.Equal(date, show!.Date);
        Assert.Equal(time, show.Time);

        // 19:30 in Chicago in November is CST (UTC-6), so the instant is 01:30 the next day.
        Assert.Equal(new DateTimeOffset(2026, 11, 15, 1, 30, 0, TimeSpan.Zero), show.StartsAtUtc);
    }

    // ----- helpers -------------------------------------------------------------

    private static EventRequest ValidRequest(
        string name = "Test Show",
        int totalCapacity = 500,
        DateOnly? date = null,
        TimeOnly? time = null,
        IReadOnlyList<TierRequest>? tiers = null) =>
        new(name,
            "Seeded by the test.",
            new VenueRequest("The Vic", "America/Chicago"),
            date ?? new DateOnly(2026, 11, 14),
            time ?? new TimeOnly(19, 30),
            totalCapacity,
            tiers ?? [new TierRequest("General Admission", 45.00m, 300)]);

    private Task<HttpResponseMessage> Put(Uri url, EventRequest body, string? etag)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(body) };

        if (etag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", etag);
        }

        return fixture.Client.SendAsync(request);
    }
}
