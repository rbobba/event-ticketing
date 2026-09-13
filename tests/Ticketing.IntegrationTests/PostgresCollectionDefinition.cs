namespace Ticketing.IntegrationTests;

/// <summary>
/// Marker only. Tests decorated [Collection(nameof(PostgresCollectionDefinition))]
/// share one container and, importantly, do not run in parallel with each other —
/// which is what makes TRUNCATE between tests safe.
/// </summary>
[CollectionDefinition(nameof(PostgresCollectionDefinition))]
public sealed class PostgresCollectionDefinition : ICollectionFixture<PostgresFixture>
{
}

