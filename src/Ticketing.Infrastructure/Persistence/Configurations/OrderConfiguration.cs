using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Ticketing.Domain.Events;
using Ticketing.Domain.Orders;

namespace Ticketing.Infrastructure.Persistence.Configurations;

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("orders", t =>
        {
            t.HasCheckConstraint("ck_orders_quantity_positive", "quantity > 0");

            // `total` is derived from unit_price × quantity. It is stored because it
            // is the transacted figure and must not be recomputed later under
            // different rounding rules; this stops the two drifting apart.
            t.HasCheckConstraint(
                "ck_orders_total_matches_unit_price", "total = unit_price * quantity");
        });

        builder.HasKey(o => o.Id);

        builder.Property(o => o.Quantity).IsRequired();
        builder.Property(o => o.PlacedAtUtc).IsRequired();
        builder.Property(o => o.IdempotencyKey).IsRequired().HasMaxLength(255);

        builder.Property(o => o.UnitPrice).IsRequired().HasPrecision(12, 2);
        builder.Property(o => o.Total).IsRequired().HasPrecision(12, 2);

        // INV-2, and load bearing under concurrency: two simultaneous replays of the
        // same key race, one inserts and the other takes a 23505 unique violation,
        // which the purchase slice catches and resolves by returning the original.
        // An application-level "check then insert" would race exactly as an oversell does.
        builder.HasIndex(o => o.IdempotencyKey)
               .IsUnique()
               .HasDatabaseName("ux_orders_idempotency_key");

        // The composite foreign key. `event_id` is denormalised — the tier already
        // knows its event — so this constrains the pair rather than each column
        // separately, making a mismatched (tier, event) combination impossible to write.
        builder.HasOne<PricingTier>()
               .WithMany()
               .HasForeignKey(o => new { o.PricingTierId, o.EventId })
               .HasPrincipalKey(t => new { t.Id, t.EventId })
               .OnDelete(DeleteBehavior.Restrict);

        builder.Metadata
               .FindNavigation(nameof(Order.Tickets))!
               .SetPropertyAccessMode(PropertyAccessMode.Field);

        // No separate index on PricingTierId: the composite foreign key above already
        // creates (pricing_tier_id, event_id), and a lookup on the leading column of a
        // composite index uses it. A second index would only add write cost on the
        // insert path that AC-2.3 hammers.
        //
        // EventId does need its own: it is the *trailing* column of that composite, so
        // the sales summary's filter on event_id cannot use it.
        builder.HasIndex(o => o.EventId).HasDatabaseName("ix_orders_event_id");
    }
}
