using Ticketing.Domain;
using Ticketing.Domain.Events;

namespace Ticketing.UnitTests;

/// <summary>
/// INV-4 — the sum of an event's tier allocations never exceeds its total capacity.
///
/// This is a cross-row rule, so unlike INV-1 it cannot be a CHECK constraint: a
/// constraint sees one row and this needs the sum of the siblings. The aggregate root
/// is therefore its only defence, which is exactly why it is worth testing here.
/// </summary>
public sealed class EventInvariantTests
{
    private static Event AnEvent(int totalCapacity = 100) =>
        Event.Create(
            name: "Spring Showcase",
            description: "An evening of short sets.",
            venue: Venue.Create("Riverside Hall", "America/Chicago"),
            date: new DateOnly(2026, 11, 14),
            time: new TimeOnly(19, 30),
            totalCapacity: totalCapacity);

    [Fact]
    public void INV_4_TierAllocationsCannotExceedCapacity()
    {
        var showcase = AnEvent(totalCapacity: 100);
        showcase.AddTier("General Admission", 25.00m, 80);

        var tooMany = () => showcase.AddTier("Balcony", 15.00m, 21);

        var error = Assert.Throws<DomainException>(tooMany);
        Assert.Contains("101", error.Message, StringComparison.Ordinal);
        Assert.Single(showcase.Tiers);
    }

    [Fact]
    public void INV_4_AllocatingExactlyTheCapacityIsAllowed()
    {
        var showcase = AnEvent(totalCapacity: 100);

        showcase.AddTier("General Admission", 25.00m, 60);
        showcase.AddTier("Balcony", 15.00m, 40);

        Assert.Equal(100, showcase.AllocatedSoFar);
    }

    [Fact]
    public void INV_4_CapacityCannotBeReducedBelowWhatTiersAlreadyHold()
    {
        // The same invariant approached from the other side. Guarding only AddTier
        // would leave it trivially breakable by an edit.
        var showcase = AnEvent(totalCapacity: 100);
        showcase.AddTier("General Admission", 25.00m, 80);

        var shrink = () => showcase.UpdateDetails(
            name: "Spring Showcase",
            description: null,
            venue: Venue.Create("Riverside Hall", "America/Chicago"),
            date: new DateOnly(2026, 11, 14),
            time: new TimeOnly(19, 30),
            totalCapacity: 50);

        Assert.Throws<DomainException>(shrink);
        Assert.Equal(100, showcase.TotalCapacity);
    }

    [Fact]
    public void ANewTierStartsFullyAvailable()
    {
        var showcase = AnEvent();

        var tier = showcase.AddTier("General Admission", 25.00m, 40);

        Assert.Equal(40, tier.Allocation);
        Assert.Equal(40, tier.Remaining);
    }

    [Fact]
    public void AnEventCannotBeCreatedWithoutCapacity()
    {
        var noCapacity = () => AnEvent(totalCapacity: 0);

        Assert.Throws<DomainException>(noCapacity);
    }
}
