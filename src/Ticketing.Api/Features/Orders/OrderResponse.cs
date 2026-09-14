using Ticketing.Domain.Orders;

namespace Ticketing.Api.Features.Orders;

public sealed record OrderResponse(
    Guid Id,
    Guid EventId,
    Guid PricingTierId,
    int Quantity,
    decimal UnitPrice,
    decimal Total,
    DateTimeOffset PlacedAtUtc,
    IReadOnlyList<Guid> TicketIds)
{
    public static OrderResponse From(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);

        return new OrderResponse(
            order.Id,
            order.EventId,
            order.PricingTierId,
            order.Quantity,
            order.UnitPrice,
            order.Total,
            order.PlacedAtUtc,
            order.Tickets.Select(t => t.Id).ToList());
    }
}
