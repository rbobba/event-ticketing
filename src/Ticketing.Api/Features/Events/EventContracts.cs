using Ticketing.Domain.Events;

namespace Ticketing.Api.Features.Events;

public sealed record VenueRequest(string Name, string TimeZoneId);

public sealed record TierRequest(string Name, decimal Price, int Allocation);

public sealed record EventRequest(
    string Name,
    string? Description,
    VenueRequest Venue,
    DateOnly Date,
    TimeOnly Time,
    int TotalCapacity,
    IReadOnlyList<TierRequest> Tiers);

public sealed record VenueResponse(string Name, string TimeZoneId);

public sealed record TierResponse(Guid Id, string Name, decimal Price, int Allocation, int Remaining);

public sealed record EventResponse(
    Guid Id,
    string Name,
    string Description,
    VenueResponse Venue,
    DateOnly Date,
    TimeOnly Time,
    DateTimeOffset StartsAtUtc,
    int TotalCapacity,
    IReadOnlyList<TierResponse> Tiers)
{
    public static EventResponse From(Event source)
    {
        ArgumentNullException.ThrowIfNull(source);

        // Date and time are returned separately
        var local = source.StartsAtLocal;

        return new EventResponse(
            source.Id,
            source.Name,
            source.Description,
            new VenueResponse(source.Venue.Name, source.Venue.TimeZoneId),
            DateOnly.FromDateTime(local.DateTime),
            TimeOnly.FromDateTime(local.DateTime),
            source.StartsAtUtc,
            source.TotalCapacity,
            source.Tiers
                  .Select(t => new TierResponse(t.Id, t.Name, t.Price, t.Allocation, t.Remaining))
                  .ToList());
    }
}
