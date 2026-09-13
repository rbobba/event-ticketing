using Microsoft.EntityFrameworkCore;

using Testcontainers.PostgreSql;

using Ticketing.Infrastructure.Persistence;

namespace Ticketing.IntegrationTests;

/// <summary>
/// One Postgres container for the whole test run. Starting it costs a couple of
/// seconds, so it is shared via a collection fixture rather than created per test.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container =
    new PostgreSqlBuilder("postgres:17-alpine")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // The same migration that would run against production. Never EnsureCreated:
        // that builds the schema from the model and would skip the migration entirely,
        // so a broken migration would still pass every test.
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public TicketingDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TicketingDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;

        return new TicketingDbContext(options);
    }

    /// <summary>Empties every table. Order does not matter — CASCADE handles it.</summary>
    public async Task ResetAsync()
    {
        await using var db = CreateContext();
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE tickets, orders, pricing_tiers, events CASCADE;");
    }
}
