using Microsoft.EntityFrameworkCore;

using Ticketing.Domain.Events;

namespace Ticketing.IntegrationTests;

[Collection(nameof(PostgresCollectionDefinition))]
public sealed class SchemaSmokeTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Schema_applies_and_an_event_round_trips()
    {
        await fixture.ResetAsync();

        var venue = Venue.Create("The Vic", "America/Chicago");
        var show = Event.Create("Smoke Test Show", "Proves the schema applies.", venue,
                                new DateOnly(2026, 11, 14), new TimeOnly(19, 30), 500);
        show.AddTier("General Admission", 45.00m, 300);

        await using (var db = fixture.CreateContext())
        {
            db.Events.Add(show);
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext())
        {
            var loaded = await db.Events
                                 .Include(e => e.Tiers)
                                 .SingleAsync(e => e.Id == show.Id);

            Assert.Equal("The Vic", loaded.Venue.Name);
            Assert.Equal("America/Chicago", loaded.Venue.TimeZoneId);
            Assert.Single(loaded.Tiers);
            Assert.Equal(300, loaded.Tiers.First().Remaining);
        }
    }
}
