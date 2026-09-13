using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Ticketing.Domain.Orders;

namespace Ticketing.Infrastructure.Persistence.Configurations;

internal sealed class TicketConfiguration : IEntityTypeConfiguration<Ticket>
{
    public void Configure(EntityTypeBuilder<Ticket> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("tickets");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.IssuedAtUtc).IsRequired();

        // Cascade, unlike orders: a ticket has no meaning without its order, whereas
        // an order is a financial record and is never deleted by a parent going away.
        builder.HasOne<Order>()
               .WithMany(o => o.Tickets)
               .HasForeignKey(t => t.OrderId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(t => t.OrderId).HasDatabaseName("ix_tickets_order_id");
    }
}
