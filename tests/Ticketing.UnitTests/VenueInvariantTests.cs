using Ticketing.Domain;
using Ticketing.Domain.Events;

namespace Ticketing.UnitTests;

/// <summary>
/// INV-6 — event times are stored as a UTC instant plus the venue's IANA time zone
/// id, never as a naive local time.
///
/// The brief supplies date and time as separate local fields, so something has to
/// combine them. The venue does, because the venue owns the time zone.
/// </summary>
public sealed class VenueInvariantTests
{
    [Fact]
    public void INV_6_LocalWallClockTimeRoundTripsThroughUtc()
    {
        var venue = Venue.Create("Riverside Hall", "America/Chicago");

        // 19:30 local on a date in US Central Daylight Time — UTC-5.
        var instant = venue.ToUtcInstant(new DateOnly(2026, 7, 4), new TimeOnly(19, 30));

        Assert.Equal(TimeSpan.Zero, instant.Offset);
        Assert.Equal(new DateTime(2026, 7, 5, 0, 30, 0, DateTimeKind.Utc), instant.UtcDateTime);
        Assert.Equal(new TimeOnly(19, 30), TimeOnly.FromDateTime(venue.ToLocal(instant).DateTime));
    }

    [Fact]
    public void INV_6_ATimeInTheSpringForwardGapIsRejected()
    {
        // Clocks in America/Chicago jump 02:00 → 03:00 on 8 March 2026, so 02:30
        // never happens. Storing a naive local time would accept it silently and
        // produce an event that starts at an instant that does not exist.
        var venue = Venue.Create("Riverside Hall", "America/Chicago");

        // Explicitly an Action: the method returns a struct, which does not convert
        // to the Func<object> overload the way a reference type would.
        Action impossible = () => venue.ToUtcInstant(new DateOnly(2026, 3, 8), new TimeOnly(2, 30));

        var error = Assert.Throws<DomainException>(impossible);
        Assert.Contains("America/Chicago", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void INV_6_AnAmbiguousTimeResolvesDeterministically()
    {
        // The reverse case: 01:30 on 1 November 2026 happens twice. There is no
        // correct answer, only a consistent one — and a stored instant must be
        // reproducible, so this asserts that it is.
        var venue = Venue.Create("Riverside Hall", "America/Chicago");

        var first = venue.ToUtcInstant(new DateOnly(2026, 11, 1), new TimeOnly(1, 30));
        var second = venue.ToUtcInstant(new DateOnly(2026, 11, 1), new TimeOnly(1, 30));

        Assert.Equal(first, second);
    }

    [Fact]
    public void AnUnknownTimeZoneIdIsARejectedRequestNotAServerError()
    {
        // Without this the provider's TimeZoneNotFoundException would escape the
        // domain and surface as a 500 for what is a client mistake.
        var wrong = () => Venue.Create("Riverside Hall", "America/Chicagoo");

        Assert.Throws<DomainException>(wrong);
    }

    [Fact]
    public void TheZoneIdIsStoredExactlyAsGiven()
    {
        // Stored, not converted. The id travels with the event so that a client can
        // render the local time itself without asking this API what zone it meant.
        var venue = Venue.Create("Riverside Hall", "America/Chicago");

        Assert.Equal("America/Chicago", venue.TimeZoneId);
        Assert.Equal("Riverside Hall", venue.Name);
    }

    [Fact]
    public void AVenueRequiresAName()
    {
        var nameless = () => Venue.Create("  ", "America/Chicago");

        Assert.Throws<DomainException>(nameless);
    }
}
