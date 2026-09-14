using Microsoft.EntityFrameworkCore;

using Ticketing.Infrastructure.Persistence;

namespace Ticketing.Api.Features.Events;

public sealed record TierAvailability(Guid PricingTierId, string Name, int Remaining);

public sealed record AvailabilityResponse(Guid EventId, IReadOnlyList<TierAvailability> Tiers);

public static class GetAvailability
{
    public static RouteGroupBuilder MapGetAvailability(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/events/{eventId:guid}/availability", HandleAsync)
             .WithName("GetAvailability")
             .WithSummary("Remaining tickets per tier. Advisory — not a reservation.");

        return group;
    }

    private static async Task<IResult> HandleAsync(
        Guid eventId, TicketingDbContext db, CancellationToken ct)
    {
        if (!await db.Events.AnyAsync(e => e.Id == eventId, ct))
        {
            return TypedResults.Problem(
                title: "Event not found.", statusCode: StatusCodes.Status404NotFound);
        }

        // A read model: projected straight to the DTO, no domain entities materialised.
        // AC-3.2 falls out of this — a sold-out tier is a row with Remaining = 0, not an
        // absent row, because nothing here filters on Remaining.
        var tiers = await db.PricingTiers.AsNoTracking()
            .Where(t => t.EventId == eventId)
            .OrderBy(t => t.Price)
            .Select(t => new TierAvailability(t.Id, t.Name, t.Remaining))
            .ToListAsync(ct);

        return TypedResults.Ok(new AvailabilityResponse(eventId, tiers));
    }
}
