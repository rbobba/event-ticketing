using Ticketing.Domain.Events;

namespace Ticketing.Domain.Orders;

public sealed class Order
{
    private readonly List<Ticket> _tickets = [];

    private Order() => IdempotencyKey = null!;

    private Order(Guid eventId, Guid pricingTierId, string idempotencyKey,
                  int quantity, decimal unitPrice, DateTimeOffset placedAt)
    {
        Id = Guid.CreateVersion7();
        EventId = eventId;
        PricingTierId = pricingTierId;
        IdempotencyKey = idempotencyKey;
        Quantity = quantity;
        UnitPrice = unitPrice;
        Total = unitPrice * quantity;
        PlacedAtUtc = placedAt.ToUniversalTime();

        for (var i = 0; i < quantity; i++)
            _tickets.Add(Ticket.Issue(Id, PlacedAtUtc));
    }

    public Guid Id { get; private set; }

    /// <summary>
    /// Denormalised: the tier already knows its event. Kept so that "orders for this
    /// event" needs no join, and held honest by a composite foreign key on
    /// (pricing_tier_id, event_id), which makes a mismatched pair unrepresentable.
    /// </summary>
    public Guid EventId { get; private set; }

    public Guid PricingTierId { get; private set; }

    /// <summary>INV-2: unique. A replay with this key returns the original order.</summary>
    public string IdempotencyKey { get; private set; }

    public int Quantity { get; private set; }

    /// <summary>
    /// The price actually charged, copied from the tier at purchase time. This is not
    /// redundant with the tier's price: that one is the price *now*, this one is the
    /// price *transacted*. They are different facts, and the order must keep its own.
    /// </summary>
    public decimal UnitPrice { get; private set; }

    /// <summary>INV-3: computed here from the stored tier price. No caller can set it.</summary>
    public decimal Total { get; private set; }

    public DateTimeOffset PlacedAtUtc { get; private set; }

    public IReadOnlyCollection<Ticket> Tickets => _tickets.AsReadOnly();

    public static Order Place(Event ticketedEvent, PricingTier tier, int quantity,
                              string idempotencyKey, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(ticketedEvent);
        ArgumentNullException.ThrowIfNull(tier);

        if (quantity <= 0)
            throw new DomainException("Quantity must be greater than zero.");

        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new DomainException("An idempotency key is required.");

        if (tier.EventId != ticketedEvent.Id)
            throw new DomainException("The pricing tier does not belong to this event.");

        if (ticketedEvent.HasStarted(now))
            throw new DomainException("The event has already started.");

        return new Order(ticketedEvent.Id, tier.Id, idempotencyKey, quantity, tier.Price, now);
    }
}
