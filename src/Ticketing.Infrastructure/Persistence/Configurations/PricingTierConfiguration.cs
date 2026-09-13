using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Ticketing.Domain.Events;

namespace Ticketing.Infrastructure.Persistence.Configurations;

internal sealed class PricingTierConfiguration : IEntityTypeConfiguration<PricingTier>
{
    public void Configure(EntityTypeBuilder<PricingTier> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("pricing_tiers", t =>
        {
            // INV-1, defence in depth. Application logic decrements conditionally;
            // this makes the oversold state unrepresentable whatever code runs.
            t.HasCheckConstraint(
                "ck_pricing_tiers_remaining_non_negative", "remaining >= 0");

            // Catches the opposite bug: something that increments past the allocation.
            t.HasCheckConstraint(
                "ck_pricing_tiers_remaining_within_allocation", "remaining <= allocation");

            t.HasCheckConstraint(
                "ck_pricing_tiers_price_non_negative", "price >= 0");

            t.HasCheckConstraint(
                "ck_pricing_tiers_allocation_positive", "allocation > 0");
        });

        builder.HasKey(t => t.Id);

        builder.Property(t => t.Name).IsRequired().HasMaxLength(100);
        builder.Property(t => t.Allocation).IsRequired();
        builder.Property(t => t.Remaining).IsRequired();

        // INV-5. numeric(12,2) — never Postgres' `money` type, which is locale
        // dependent, and never a floating point type.
        builder.Property(t => t.Price).IsRequired().HasPrecision(12, 2);

        builder.HasOne<Event>()
               .WithMany(e => e.Tiers)
               .HasForeignKey(t => t.EventId)
               .OnDelete(DeleteBehavior.Cascade);

        // Redundant as a key in its own right — Id is already unique — but it is what
        // lets `orders` declare a composite foreign key covering both columns, so an
        // order can never name one event and a tier belonging to another.
        builder.HasAlternateKey(t => new { t.Id, t.EventId });

        builder.HasIndex(t => t.EventId).HasDatabaseName("ix_pricing_tiers_event_id");
    }
}
