using Microsoft.EntityFrameworkCore;

using Ticketing.Domain.Events;
using Ticketing.Domain.Orders;

namespace Ticketing.Infrastructure.Persistence;

public sealed class TicketingDbContext(DbContextOptions<TicketingDbContext> options)
    : DbContext(options)
{
    public DbSet<Event> Events => Set<Event>();

    public DbSet<PricingTier> PricingTiers => Set<PricingTier>();

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<Ticket> Tickets => Set<Ticket>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TicketingDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        // Every decimal in this model is money (INV-5). Setting it once by convention
        // means a property added later cannot quietly land as an unconstrained numeric.
        configurationBuilder.Properties<decimal>().HavePrecision(12, 2);
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        // Applied here rather than at each registration site so that it cannot be
        // forgotten by one of them. Postgres folds unquoted identifiers to lower
        // case; a PascalCase table would be created as "PricingTier" and would then
        // need quoting in every hand-written query, forever.
        optionsBuilder.UseSnakeCaseNamingConvention();
    }
}
