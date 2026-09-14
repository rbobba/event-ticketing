using Microsoft.EntityFrameworkCore;

using Ticketing.Infrastructure.Persistence;

namespace Ticketing.Api.Features.Orders;

public static class GetOrder
{
    public static RouteGroupBuilder MapGetOrder(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/orders/{orderId:guid}", HandleAsync)
             .WithName("GetOrder")
             .WithSummary("Retrieve an order. Possession of the id is the authorization.");

        return group;
    }

    private static async Task<IResult> HandleAsync(
        Guid orderId, TicketingDbContext db, CancellationToken ct)
    {
        // Flat, not nested under the event: the order id is globally unique, and
        // requiring the event id too would mean holding two identifiers for no gain.
        var order = await db.Orders.AsNoTracking()
            .Include(o => o.Tickets)
            .FirstOrDefaultAsync(o => o.Id == orderId, ct);

        return order is null
            ? TypedResults.Problem(
                title: "Order not found.", statusCode: StatusCodes.Status404NotFound)
            : TypedResults.Ok(OrderResponse.From(order));
    }
}
