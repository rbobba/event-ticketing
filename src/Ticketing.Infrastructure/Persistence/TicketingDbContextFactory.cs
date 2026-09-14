using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ticketing.Infrastructure.Persistence;

/// <summary>
/// Lets the EF tooling build a context without starting the API.
///
/// The fallback matches the container in docker-compose.yml, so `dotnet ef database
/// update` works straight after `docker compose up -d` with nothing to configure. Set
/// TICKETING_DB to point the tooling elsewhere — a different port, or another
/// environment. Nothing here is a credential for anything: the compose file declares
/// the same password and says why.
/// </summary>
internal sealed class TicketingDbContextFactory : IDesignTimeDbContextFactory<TicketingDbContext>
{
    public TicketingDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("TICKETING_DB")
            ?? "Host=localhost;Port=5432;Database=ticketing;Username=ticketing;Password=localdev";

        var options = new DbContextOptionsBuilder<TicketingDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new TicketingDbContext(options);
    }
}
