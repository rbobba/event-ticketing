namespace Ticketing.Domain.Events;

public sealed class PricingTier
{
    private PricingTier() => Name = null!;

    private PricingTier(Guid eventId, string name, decimal price, int allocation)
    {
        Id = Guid.CreateVersion7();
        EventId = eventId;
        Name = name;
        Price = price;
        Allocation = allocation;
        Remaining = allocation;
    }

    public Guid Id { get; private set; }

    public Guid EventId { get; private set; }

    public string Name { get; private set; }

    /// <summary>INV-5: money is decimal, never floating point.</summary>
    public decimal Price { get; private set; }

    public int Allocation { get; private set; }

    /// <summary>
    /// Tickets still available in this tier.
    ///
    /// There is deliberately no domain method that decrements this. The only
    /// safe decrement is the conditional UPDATE in the purchase slice, which the
    /// database evaluates atomically; a load-check-subtract-save cycle here would
    /// race and oversell (INV-1). A CHECK constraint backs it up in the schema.
    /// </summary>
    public int Remaining { get; private set; }

    internal static PricingTier Create(Guid eventId, string name, decimal price, int allocation)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("Tier name is required.");

        if (price < 0m)
            throw new DomainException("Tier price cannot be negative.");

        return new PricingTier(eventId, name, price, allocation);
    }
}
