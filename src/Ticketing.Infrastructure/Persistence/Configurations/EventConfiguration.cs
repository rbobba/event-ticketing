using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Ticketing.Domain.Events;

namespace Ticketing.Infrastructure.Persistence.Configurations;

internal sealed class EventConfiguration : IEntityTypeConfiguration<Event>
{
    public void Configure(EntityTypeBuilder<Event> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("events", t =>
            t.HasCheckConstraint("ck_events_capacity_positive", "total_capacity > 0"));

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Name).IsRequired().HasMaxLength(200);
        builder.Property(e => e.Description).IsRequired().HasMaxLength(2000);
        builder.Property(e => e.StartsAtUtc).IsRequired();
        builder.Property(e => e.TotalCapacity).IsRequired();

        // A venue has no identity of its own here, so it lives on the event's row.
        builder.OwnsOne(e => e.Venue, venue =>
        {
            venue.Property(v => v.Name)
                 .HasColumnName("venue_name").IsRequired().HasMaxLength(200);

            venue.Property(v => v.TimeZoneId)
                 .HasColumnName("venue_time_zone_id").IsRequired().HasMaxLength(100);
        });

        builder.Navigation(e => e.Venue).IsRequired();

        // Derived, not stored. Without these EF looks for a backing field and fails.
        builder.Ignore(e => e.StartsAtLocal);
        builder.Ignore(e => e.AllocatedSoFar);

        // The collection is exposed read-only, so EF must go through the field.
        builder.Metadata
               .FindNavigation(nameof(Event.Tiers))!
               .SetPropertyAccessMode(PropertyAccessMode.Field);

        // `xmin` is the Postgres system column holding the id of the transaction that
        // last wrote the row, so it changes on every update for free. Used as the ETag
        // for AC-1.4: no extra column to maintain, and unlike a hand-rolled version
        // number it cannot be forgotten by a write path that skips the domain.
        // (The `UseXminAsConcurrencyToken` shortcut was removed in the v10 provider;
        // this shadow property is the supported form.)
        builder.Property<uint>("xmin")
               .HasColumnName("xmin")
               .HasColumnType("xid")
               .ValueGeneratedOnAddOrUpdate()
               .IsConcurrencyToken();
    }
}
