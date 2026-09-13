namespace Ticketing.Domain.Orders;

/// <summary>
/// One admission. Deliberately carries no tier of its own: an order is for a single
/// tier, so a tier on the ticket would be a second copy of the same fact and a second
/// thing that could disagree with the first.
/// </summary>
public sealed class Ticket
{
    private Ticket() { }

    private Ticket(Guid orderId, DateTimeOffset issuedAt)
    {
        Id = Guid.CreateVersion7();
        OrderId = orderId;
        IssuedAtUtc = issuedAt;
    }

    public Guid Id { get; private set; }

    public Guid OrderId { get; private set; }

    public DateTimeOffset IssuedAtUtc { get; private set; }

    internal static Ticket Issue(Guid orderId, DateTimeOffset issuedAt)
        => new(orderId, issuedAt);
}
