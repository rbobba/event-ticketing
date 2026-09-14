using System.Diagnostics.CodeAnalysis;

namespace Ticketing.Domain.Events;

[SuppressMessage("Naming", "CA1716:Identifiers should not match keywords",
    Justification = "'Event' is the ubiquitous language of this domain — it is the word " +
                    "used by the brief, by the API route (/v1/events), and by the table. " +
                    "CA1716 guards cross-language consumers of a published library; this " +
                    "is an application assembly consumed only from C#. Every alternative " +
                    "name considered was less accurate.")]
public sealed class Event
{
    private readonly List<PricingTier> _tiers = [];

    private Event()
    {
        // EF materialisation only. The properties are set by the provider.
        Name = null!;
        Description = null!;
        Venue = null!;
    }

    private Event(string name, string description, Venue venue,
                  DateTimeOffset startsAtUtc, int totalCapacity)
    {
        Id = Guid.CreateVersion7();
        Name = name;
        Description = description;
        Venue = venue;
        StartsAtUtc = startsAtUtc;
        TotalCapacity = totalCapacity;
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; }

    /// <summary>May be empty. An absent description is not an error.</summary>
    public string Description { get; private set; }

    public Venue Venue { get; private set; }

    /// <summary>
    /// The instant the event starts, always UTC (INV-6). The local date and time are
    /// derived from this and <see cref="Events.Venue.TimeZoneId"/> rather than stored,
    /// so the two can never disagree.
    /// </summary>
    public DateTimeOffset StartsAtUtc { get; private set; }

    public int TotalCapacity { get; private set; }

    public IReadOnlyCollection<PricingTier> Tiers => _tiers.AsReadOnly();

    public int AllocatedSoFar => _tiers.Sum(t => t.Allocation);

    /// <summary>The start time as a wall clock reading at the venue.</summary>
    public DateTimeOffset StartsAtLocal => Venue.ToLocal(StartsAtUtc);

    public static Event Create(string name, string? description, Venue venue,
                               DateOnly date, TimeOnly time, int totalCapacity)
    {
        ArgumentNullException.ThrowIfNull(venue);

        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("Event name is required.");

        if (totalCapacity <= 0)
            throw new DomainException("Total capacity must be greater than zero.");

        // The venue owns the conversion, because the venue owns the time zone.
        var startsAtUtc = venue.ToUtcInstant(date, time);

        return new Event(name, description ?? string.Empty, venue, startsAtUtc, totalCapacity);
    }

    /// <summary>INV-4: tier allocations may never exceed the event's capacity.</summary>
    public PricingTier AddTier(string tierName, decimal price, int allocation)
    {
        if (allocation <= 0)
            throw new DomainException("Tier allocation must be greater than zero.");

        if (AllocatedSoFar + allocation > TotalCapacity)
        {
            throw new DomainException(
                $"Allocating {allocation} to '{tierName}' would bring total allocation to " +
                $"{AllocatedSoFar + allocation}, exceeding the event capacity of {TotalCapacity}.");
        }

        var tier = PricingTier.Create(Id, tierName, price, allocation);
        _tiers.Add(tier);
        return tier;
    }

    /// <summary>
    /// Details only. Tiers are set at creation: changing an allocation after tickets are
    /// sold means reconciling against what has already been issued, which is a feature
    /// rather than an edit.
    /// </summary>
    public void UpdateDetails(string name, string? description, Venue venue,
                              DateOnly date, TimeOnly time, int totalCapacity)
    {
        ArgumentNullException.ThrowIfNull(venue);

        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("Event name is required.");

        if (totalCapacity <= 0)
            throw new DomainException("Total capacity must be greater than zero.");

        // INV-4 from the other direction: capacity can't drop below what tiers hold.
        if (totalCapacity < AllocatedSoFar)
        {
            throw new DomainException(
                $"Capacity cannot be reduced to {totalCapacity}; {AllocatedSoFar} is " +
                $"already allocated across this event's tiers.");
        }

        Name = name;
        Description = description ?? string.Empty;
        Venue = venue;
        StartsAtUtc = venue.ToUtcInstant(date, time);
        TotalCapacity = totalCapacity;
    }


    public bool HasStarted(DateTimeOffset now) => now >= StartsAtUtc;
}
