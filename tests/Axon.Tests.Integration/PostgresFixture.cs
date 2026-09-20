using Npgsql;
using Testcontainers.PostgreSql;

namespace Axon.Tests.Integration;

/// <summary>
/// Spins up a real PostgreSQL container once per test collection and applies Schema.sql against
/// it, so integration tests exercise the actual Dapper/SQL path (locking semantics, type
/// handling) rather than the in-memory store's behavior.
/// </summary>
public class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();

        var schemaPath = Path.Combine(AppContext.BaseDirectory, "Postgres.Schema.sql");
        var schemaSql = await File.ReadAllTextAsync(schemaPath);

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = schemaSql;
        await command.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}
