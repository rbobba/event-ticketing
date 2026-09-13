using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Npgsql;

using Ticketing.Domain.Orders;
using Ticketing.Infrastructure.Persistence;

namespace Ticketing.Api.Features.Orders;

public sealed record PurchaseRequest(Guid PricingTierId, int Quantity);

public sealed record OrderResponse(
    Guid Id,
    Guid EventId,
    Guid PricingTierId,
    int Quantity,
    decimal UnitPrice,
    decimal Total,
    DateTimeOffset PlacedAtUtc,
    IReadOnlyList<Guid> TicketIds);

public static class PurchaseTickets
{
    public static RouteGroupBuilder MapPurchaseTickets(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapPost("/events/{eventId:guid}/orders", HandleAsync)
             .WithName("PurchaseTickets")
             .WithSummary("Purchase tickets for an event. Requires an Idempotency-Key header.");

        return group;
    }

    private static async Task<IResult> HandleAsync(
        Guid eventId,
        PurchaseRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        TicketingDbContext db,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return TypedResults.Problem(
                title: "An Idempotency-Key header is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // INV-2 fast path. A replay should do no work.
        var seen = await db.Orders.AsNoTracking()
            .Include(o => o.Tickets)
            .FirstOrDefaultAsync(o => o.IdempotencyKey == idempotencyKey, ct);

        if (seen is not null)
        {
            return Replay(seen, request);
        }

        // AsNoTracking deliberately: ExecuteUpdateAsync below changes `remaining` in the
        // database without touching the change tracker, so a tracked tier would hold a
        // stale value that a later SaveChanges could write back over the decrement.
        var ticketedEvent = await db.Events.AsNoTracking()
            .Include(e => e.Tiers)
            .FirstOrDefaultAsync(e => e.Id == eventId, ct);

        if (ticketedEvent is null)
        {
            return TypedResults.Problem(
                title: "Event not found.", statusCode: StatusCodes.Status404NotFound);
        }

        var tier = ticketedEvent.Tiers.FirstOrDefault(t => t.Id == request.PricingTierId);

        if (tier is null)
        {
            return TypedResults.Problem(
                title: "Pricing tier not found for this event.",
                statusCode: StatusCodes.Status404NotFound);
        }

        // Throws DomainException for a past event or a non-positive quantity, which the
        // exception handler turns into 422. Nothing is persisted yet, and the total is
        // computed from the stored tier price (INV-3).
        var order = Order.Place(ticketedEvent, tier, request.Quantity, idempotencyKey, DateTimeOffset.UtcNow);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // INV-1. One statement: the database compares and decrements atomically, so
        // there is no window between the check and the write to race in. Zero rows
        // affected IS the sold-out answer — there is no separate check to disagree with.
        var rowsDecremented = await db.PricingTiers
            .Where(t => t.Id == tier.Id && t.Remaining >= request.Quantity)
            .ExecuteUpdateAsync(
                s => s.SetProperty(t => t.Remaining, t => t.Remaining - request.Quantity), ct);

        if (rowsDecremented == 0)
        {
            await transaction.RollbackAsync(ct);

            return TypedResults.Problem(
                title: "Insufficient tickets remaining in this tier.",
                detail: "The purchase is all or nothing; no tickets were issued.",
                statusCode: StatusCodes.Status409Conflict);
        }

        db.Orders.Add(order);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A concurrent request with the same key committed first. Rolling back undoes
            // our decrement, so the loser leaks no inventory. An application-level
            // "check then insert" would have raced here exactly as an oversell does.
            await transaction.RollbackAsync(ct);

            var winner = await db.Orders.AsNoTracking()
                .Include(o => o.Tickets)
                .FirstAsync(o => o.IdempotencyKey == idempotencyKey, ct);

            return Replay(winner, request);
        }

        await transaction.CommitAsync(ct);

        return TypedResults.Created($"/v1/orders/{order.Id}", ToResponse(order));
    }

    private static IResult Replay(Order existing, PurchaseRequest request)
    {
        // AC-2.5: the same key with a different payload is a conflict, not a replay.
        if (existing.PricingTierId != request.PricingTierId || existing.Quantity != request.Quantity)
        {
            return TypedResults.Problem(
                title: "This Idempotency-Key was already used with a different request.",
                statusCode: StatusCodes.Status409Conflict);
        }

        // AC-2.4: the original order, no new tickets. 200 rather than 201 on purpose —
        // 201 asserts something was created, and a replay creates nothing.
        return TypedResults.Ok(ToResponse(existing));
    }

    private static OrderResponse ToResponse(Order order) => new(
        order.Id,
        order.EventId,
        order.PricingTierId,
        order.Quantity,
        order.UnitPrice,
        order.Total,
        order.PlacedAtUtc,
        order.Tickets.Select(t => t.Id).ToList());
}
