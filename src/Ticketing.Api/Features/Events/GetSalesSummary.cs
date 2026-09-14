using Microsoft.EntityFrameworkCore;

using Ticketing.Infrastructure.Persistence;

namespace Ticketing.Api.Features.Events;

public sealed record TierSales(
    Guid PricingTierId, string Name, int Allocation, int Sold, int Remaining, decimal Revenue);

public sealed record SalesSummaryResponse(
    Guid EventId,
    string EventName,
    int TotalSold,
    int TotalRemaining,
    decimal TotalRevenue,
    IReadOnlyList<TierSales> Tiers);

public static class GetSalesSummary
{
    public static RouteGroupBuilder MapSalesSummary(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/events/{eventId:guid}/sales-summary", HandleAsync)
             .WithName("GetSalesSummary")
             .WithSummary("Tickets sold, remaining, and revenue per tier and in total.");

        return group;
    }

    private static async Task<IResult> HandleAsync(
        Guid eventId, TicketingDbContext db, CancellationToken ct)
    {
        var name = await db.Events.AsNoTracking()
            .Where(e => e.Id == eventId)
            .Select(e => e.Name)
            .FirstOrDefaultAsync(ct);

        if (name is null)
        {
            return TypedResults.Problem(
                title: "Event not found.", statusCode: StatusCodes.Status404NotFound);
        }

        // Driven from pricing_tiers, not from orders, so a tier with no sales still
        // produces a row (AC-4.2). The nullable casts make the sums return null rather
        // than throwing on an empty set, and ?? turns that into a zero.
        var tiers = await db.PricingTiers.AsNoTracking()
            .Where(t => t.EventId == eventId)
            .OrderBy(t => t.Price)
            .Select(t => new TierSales(
                t.Id,
                t.Name,
                t.Allocation,
                db.Orders.Where(o => o.PricingTierId == t.Id).Sum(o => (int?)o.Quantity) ?? 0,
                t.Remaining,
                db.Orders.Where(o => o.PricingTierId == t.Id).Sum(o => (decimal?)o.Total) ?? 0m))
            .ToListAsync(ct);

        return TypedResults.Ok(new SalesSummaryResponse(
            eventId,
            name,
            tiers.Sum(t => t.Sold),
            tiers.Sum(t => t.Remaining),
            tiers.Sum(t => t.Revenue),      // decimal throughout — INV-5
            tiers));
    }
}
