using MySqlConnector;
using Testcontainers.MySql;

namespace Axon.Tests.Integration;

/// <summary>
/// Spins up a real MySQL container once per test collection and applies Schema.sql against it,
/// so integration tests exercise the actual Dapper/SQL path (locking semantics, type handling)
/// rather than the in-memory store's behavior.
/// </summary>
public class MySqlFixture : IAsyncLifetime
{
    private readonly MySqlContainer _container = new MySqlBuilder("mysql:8.4").Build();

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();

        var schemaPath = Path.Combine(AppContext.BaseDirectory, "MySql.Schema.sql");
        var schemaSql = await File.ReadAllTextAsync(schemaPath);

        await using var connection = new MySqlConnection(ConnectionString);
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
public class MySqlCollection : ICollectionFixture<MySqlFixture>
{
    public const string Name = "MySql";
}
