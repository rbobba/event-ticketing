using Microsoft.EntityFrameworkCore;

using Ticketing.Domain.Events;
using Ticketing.Infrastructure.Persistence;

namespace Ticketing.Api.Features.Events;

public static class EventEndpoints
{
    public static RouteGroupBuilder MapEventEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        var events = group.MapGroup("/events");

        events.MapPost("/", CreateAsync).WithName("CreateEvent");
        events.MapGet("/", ListAsync).WithName("ListEvents");
        events.MapGet("/{id:guid}", GetAsync).WithName("GetEvent");
        events.MapPut("/{id:guid}", UpdateAsync).WithName("UpdateEvent");
        events.MapDelete("/{id:guid}", DeleteAsync).WithName("DeleteEvent");

        return group;
    }

    private static async Task<IResult> CreateAsync(
        EventRequest request, TicketingDbContext db, CancellationToken ct)
    {
        // Every guard below throws DomainException, which the handler maps to 422.
        var venue = Venue.Create(request.Venue.Name, request.Venue.TimeZoneId);

        var show = Event.Create(
            request.Name, request.Description, venue,
            request.Date, request.Time, request.TotalCapacity);

        foreach (var tier in request.Tiers)
        {
            show.AddTier(tier.Name, tier.Price, tier.Allocation);   // INV-4
        }

        db.Events.Add(show);
        await db.SaveChangesAsync(ct);

        return TypedResults.Created($"/v1/events/{show.Id}", EventResponse.From(show));
    }

    private static async Task<IResult> ListAsync(TicketingDbContext db, CancellationToken ct)
    {
        var all = await db.Events.AsNoTracking()
            .Include(e => e.Tiers)
            .OrderBy(e => e.StartsAtUtc)
            .ToListAsync(ct);

        return TypedResults.Ok(all.Select(EventResponse.From).ToList());
    }

    private static async Task<IResult> GetAsync(
        Guid id, HttpResponse response, TicketingDbContext db, CancellationToken ct)
    {
        // xmin is a shadow property, so it is projected explicitly rather than read off
        // the entity. Projecting it also keeps the query AsNoTracking.
        var row = await db.Events.AsNoTracking()
            .Include(e => e.Tiers)
            .Where(e => e.Id == id)
            .Select(e => new { Event = e, Version = EF.Property<uint>(e, "xmin") })
            .FirstOrDefaultAsync(ct);

        if (row is null)
        {
            return TypedResults.Problem(
                title: "Event not found.", statusCode: StatusCodes.Status404NotFound);
        }

        response.Headers.ETag = $"\"{row.Version}\"";
        return TypedResults.Ok(EventResponse.From(row.Event));
    }

    private static async Task<IResult> UpdateAsync(
        Guid id, EventRequest request, HttpRequest httpRequest,
        TicketingDbContext db, CancellationToken ct)
    {
        var show = await db.Events
            .Include(e => e.Tiers)          // tracked: UpdateDetails needs AllocatedSoFar
            .FirstOrDefaultAsync(e => e.Id == id, ct);

        if (show is null)
        {
            return TypedResults.Problem(
                title: "Event not found.", statusCode: StatusCodes.Status404NotFound);
        }

        var venue = Venue.Create(request.Venue.Name, request.Venue.TimeZoneId);
        show.UpdateDetails(request.Name, request.Description, venue,
                           request.Date, request.Time, request.TotalCapacity);

        // If-Match is honoured when sent, not demanded. Overriding OriginalValue is what
        // puts `AND xmin = <client value>` into the UPDATE; if the row moved on since the
        // client read it, nothing matches and EF raises a concurrency exception.
        if (TryParseIfMatch(httpRequest, out var expected))
        {
            db.Entry(show).Property<uint>("xmin").OriginalValue = expected;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return TypedResults.Problem(
                title: "The event has changed since you read it.",
                detail: "Re-read the event and retry with the current ETag.",
                statusCode: StatusCodes.Status412PreconditionFailed);
        }

        return TypedResults.Ok(EventResponse.From(show));
    }

    private static async Task<IResult> DeleteAsync(
        Guid id, TicketingDbContext db, CancellationToken ct)
    {
        var show = await db.Events.FirstOrDefaultAsync(e => e.Id == id, ct);

        if (show is null)
        {
            return TypedResults.Problem(
                title: "Event not found.", statusCode: StatusCodes.Status404NotFound);
        }

        // The RESTRICT foreign key would also stop this, but as a 500. Sales are a
        // business reason to refuse, so it gets a business answer.
        if (await db.Orders.AnyAsync(o => o.EventId == id, ct))
        {
            return TypedResults.Problem(
                title: "This event has ticket sales and cannot be deleted.",
                statusCode: StatusCodes.Status409Conflict);
        }

        db.Events.Remove(show);
        await db.SaveChangesAsync(ct);

        return TypedResults.NoContent();
    }

    private static bool TryParseIfMatch(HttpRequest request, out uint version)
    {
        version = 0;

        var header = request.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(header) || header == "*")
            return false;

        return uint.TryParse(header.Trim().TrimStart('W', '/').Trim('"'), out version);
    }
}
