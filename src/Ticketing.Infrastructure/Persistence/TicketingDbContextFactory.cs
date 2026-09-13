using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ticketing.Infrastructure.Persistence;

/// <summary>
/// Lets `dotnet ef migrations add` build a context without starting the API.
///
/// Generating a migration does not open a connection — the provider only needs to
/// know which SQL dialect to emit — so the fallback below deliberately carries no
/// credentials. Set TICKETING_DB if you want to point the tooling at a real database.
/// </summary>
internal sealed class TicketingDbContextFactory : IDesignTimeDbContextFactory<TicketingDbContext>
{
    public TicketingDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("TICKETING_DB")
            ?? "Host=localhost;Database=ticketing_design_time";

        var options = new DbContextOptionsBuilder<TicketingDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new TicketingDbContext(options);
    }
}
