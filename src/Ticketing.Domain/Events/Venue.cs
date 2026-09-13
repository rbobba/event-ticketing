namespace Ticketing.Domain.Events;

/// <summary>
/// Where an event happens. A value object, not an entity: nothing in this domain
/// references a venue independently of the event it belongs to, so it has no identity
/// of its own and is stored on the event's row.
/// </summary>
public sealed class Venue
{
    private Venue()
    {
        // EF materialisation only.
        Name = null!;
        TimeZoneId = null!;
    }

    private Venue(string name, string timeZoneId)
    {
        Name = name;
        TimeZoneId = timeZoneId;
    }

    public string Name { get; private set; }

    /// <summary>IANA zone id, for example "America/Chicago" (INV-6).</summary>
    public string TimeZoneId { get; private set; }

    public static Venue Create(string name, string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("Venue name is required.");

        // Throws on an unknown zone. Deliberate: this is also the check that fails
        // loudly if the container ships without a tz database, rather than silently
        // storing a zone that cannot be resolved later.
        _ = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);

        return new Venue(name, timeZoneId);
    }

    /// <summary>Combines a local date and time at this venue into a UTC instant.</summary>
    public DateTimeOffset ToUtcInstant(DateOnly date, TimeOnly time)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
        var local = date.ToDateTime(time);   // DateTimeKind.Unspecified

        if (zone.IsInvalidTime(local))
        {
            throw new DomainException(
                $"{local:yyyy-MM-dd HH:mm} does not occur at {Name}: the clocks move " +
                $"forward over it in {TimeZoneId}.");
        }

        // For an ambiguous local time — the hour repeated when clocks go back —
        // GetUtcOffset returns the standard-time offset, i.e. the second occurrence.
        // Deterministic either way, which is what matters for a stored instant.
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    /// <summary>Reads a UTC instant as a wall clock time at this venue.</summary>
    public DateTimeOffset ToLocal(DateTimeOffset instant)
        => TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId));
}
