using Microsoft.Data.Sqlite;

namespace Axon.Tests.Integration;

/// <summary>
/// Creates a real SQLite database file once per test collection and applies Schema.sql against
/// it, so integration tests exercise the actual Dapper/SQL path rather than the in-memory
/// store's behavior. Unlike the other backends' fixtures, this needs no container - SQLite is
/// just a file - but a real file (not ":memory:") is used deliberately, since an in-memory
/// SQLite database is private to the single connection that created it and would defeat the
/// point of testing AxonSQLiteStore's own connection-per-call pattern (CreateConnection() opens
/// a fresh SqliteConnection for every call, the same way the other backends' stores do).
/// </summary>
public class SQLiteFixture : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"axon-test-{Guid.NewGuid():N}.db");

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        ConnectionString = new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString();

        var schemaPath = Path.Combine(AppContext.BaseDirectory, "SQLite.Schema.sql");
        var schemaSql = await File.ReadAllTextAsync(schemaPath);

        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = schemaSql;
        await command.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        return Task.CompletedTask;
    }
}

[CollectionDefinition(Name)]
public class SQLiteCollection : ICollectionFixture<SQLiteFixture>
{
    public const string Name = "SQLite";
}
