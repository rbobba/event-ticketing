using DotNet.Testcontainers.Builders;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Testcontainers.PostgreSql;

using Ticketing.Infrastructure.Persistence;

namespace Ticketing.IntegrationTests;

public sealed class PostgresFixture : IAsyncLifetime
{
    // Built in InitializeAsync rather than here on purpose. PostgreSqlBuilder.Build()
    // validates that a Docker daemon is reachable, so constructing this in a field
    // initialiser would throw inside the fixture's constructor — where the failure
    // surfaces as an unhelpful stack trace that no catch block of ours can reach.
    private PostgreSqlContainer? _container;

    private WebApplicationFactory<Program>? _factory;

    public string ConnectionString =>
        _container?.GetConnectionString()
        ?? throw new InvalidOperationException("The container has not been started yet.");

    /// <summary>One client, reused. HttpClient is safe for concurrent requests, which
    /// AC-2.3 depends on.</summary>
    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
            await _container.StartAsync();
        }
        catch (DockerUnavailableException ex)
        {
            // The default failure is ten seconds of stack trace that never says what to
            // do about it. This is the first thing anyone cloning the repo hits if their
            // daemon is not up, so it gets a sentence instead.
            throw new InvalidOperationException(
                "\n\nThese tests need a running Docker daemon.\n\n" +
                "Testcontainers starts its own PostgreSQL container on a random port, " +
                "migrates it, and destroys it afterwards. It does NOT use docker-compose.yml, " +
                "and nothing else has to be running — but the Docker daemon does.\n\n" +
                "Start Docker Desktop, wait for it to finish starting, and run the tests " +
                "again. Closing the Docker Desktop window only hides it; quitting from the " +
                "tray icon stops the engine, which is what produces this error.\n\n" +
                "Why this suite will not fall back to an in-memory provider: " +
                "docs/adr/0004-real-postgresql-in-tests.md\n",
                ex);
        }

        await using (var db = CreateContext())
        {
            await db.Database.MigrateAsync();
        }

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Ticketing", ConnectionString));

        Client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();

        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    public TicketingDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TicketingDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;

        return new TicketingDbContext(options);
    }

    public async Task ResetAsync()
    {
        await using var db = CreateContext();
        await db.Database.ExecuteSqlRawAsync("TRUNCATE tickets, orders, pricing_tiers, events CASCADE;");
    }
}
