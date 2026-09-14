using Ticketing.Domain;
using Ticketing.Domain.Events;
using Ticketing.Domain.Orders;

namespace Ticketing.UnitTests;

/// <summary>
/// INV-3 — order totals are computed from stored tier prices; a client-supplied
/// amount is never trusted. INV-5 — money is decimal, never floating point.
///
/// Note what is *not* here. INV-1 (no overselling) and INV-2 (idempotent replay) are
/// properties of concurrent database behaviour, not of this object, and a unit test
/// claiming to cover them would be worse than none. They are tested against real
/// PostgreSQL — see ADR-0004.
/// </summary>
public sealed class OrderInvariantTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static Event AnEventStartingLater() =>
        Event.Create(
            name: "Spring Showcase",
            description: null,
            venue: Venue.Create("Riverside Hall", "America/Chicago"),
            date: new DateOnly(2026, 11, 14),
            time: new TimeOnly(19, 30),
            totalCapacity: 100);

    [Fact]
    public void INV_3_TheTotalComesFromTheTierPriceAndNothingElse()
    {
        var showcase = AnEventStartingLater();
        var tier = showcase.AddTier("General Admission", 19.99m, 50);

        var order = Order.Place(showcase, tier, quantity: 3, idempotencyKey: "key-1", now: Now);

        // No parameter on Place accepts an amount. There is nowhere for a caller to
        // put a price, which is a stronger guarantee than validating one.
        Assert.Equal(19.99m, order.UnitPrice);
        Assert.Equal(59.97m, order.Total);
    }

    [Fact]
    public void INV_5_MoneyIsExactToTheCent()
    {
        var showcase = AnEventStartingLater();
        var tier = showcase.AddTier("General Admission", 0.10m, 50);

        var order = Order.Place(showcase, tier, quantity: 3, idempotencyKey: "key-1", now: Now);

        // 0.1 + 0.1 + 0.1 is 0.30000000000000004 in binary floating point. With
        // decimal it is 0.30, which is why prices are decimal here and numeric in
        // PostgreSQL. A cent lost per order is a reconciliation problem later.
        Assert.Equal(0.30m, order.Total);
    }

    [Fact]
    public void OneTicketIsIssuedPerUnitOfQuantity()
    {
        var showcase = AnEventStartingLater();
        var tier = showcase.AddTier("General Admission", 19.99m, 50);

        var order = Order.Place(showcase, tier, quantity: 4, idempotencyKey: "key-1", now: Now);

        Assert.Equal(4, order.Tickets.Count);
        Assert.All(order.Tickets, t => Assert.Equal(order.Id, t.OrderId));
    }

    [Fact]
    public void ATierBelongingToAnotherEventIsRejected()
    {
        // The composite foreign key makes this unrepresentable in the database. The
        // domain rejects it too, so the failure is a 422 with an explanation rather
        // than a constraint violation surfacing as a 500.
        var showcase = AnEventStartingLater();
        var otherEvent = AnEventStartingLater();
        var foreignTier = otherEvent.AddTier("General Admission", 19.99m, 50);

        var mismatched = () =>
            Order.Place(showcase, foreignTier, quantity: 1, idempotencyKey: "key-1", now: Now);

        Assert.Throws<DomainException>(mismatched);
    }

    [Fact]
    public void AnEventThatHasAlreadyStartedCannotBeSold()
    {
        var showcase = AnEventStartingLater();
        var tier = showcase.AddTier("General Admission", 19.99m, 50);

        var tooLate = () => Order.Place(
            showcase, tier, quantity: 1, idempotencyKey: "key-1",
            now: showcase.StartsAtUtc.AddSeconds(1));

        Assert.Throws<DomainException>(tooLate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void QuantityMustBePositive(int quantity)
    {
        var showcase = AnEventStartingLater();
        var tier = showcase.AddTier("General Admission", 19.99m, 50);

        var invalid = () =>
            Order.Place(showcase, tier, quantity, idempotencyKey: "key-1", now: Now);

        Assert.Throws<DomainException>(invalid);
    }

    [Fact]
    public void AnIdempotencyKeyIsRequired()
    {
        var showcase = AnEventStartingLater();
        var tier = showcase.AddTier("General Admission", 19.99m, 50);

        var unkeyed = () => Order.Place(showcase, tier, quantity: 1, idempotencyKey: "", now: Now);

        Assert.Throws<DomainException>(unkeyed);
    }
}
