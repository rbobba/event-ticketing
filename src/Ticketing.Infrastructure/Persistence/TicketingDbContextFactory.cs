using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Diagnostics;

using Microsoft.Extensions.Logging;

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

            // Against an empty database, EF's first act is to read __EFMigrationsHistory,
            // which does not exist yet. EF expects that and creates the table — but the
            // command interceptor logs the failure at Error level first, so `dotnet ef`
            // opens with a red "Failed executing DbCommand" on every clean setup. Demoted
            // to Debug so the first thing a reader sees is not a non-problem. A migration
            // that genuinely fails still throws, and `dotnet ef` still reports it.
            .ConfigureWarnings(w => w.Log((RelationalEventId.CommandError, LogLevel.Debug)))
            .Options;

        return new TicketingDbContext(options);
    }
}
