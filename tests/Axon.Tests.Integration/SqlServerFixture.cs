using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace Axon.Tests.Integration;

/// <summary>
/// Spins up a real SQL Server container once per test collection and applies Schema.sql against
/// it, so integration tests exercise the actual Dapper/T-SQL path (locking semantics, type
/// handling) rather than the in-memory store's behavior.
/// </summary>
public class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();

        var schemaPath = Path.Combine(AppContext.BaseDirectory, "Schema.sql");
        var schemaSql = await File.ReadAllTextAsync(schemaPath);

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        foreach (var batch in schemaSql.Split("\nGO\n", StringSplitOptions.RemoveEmptyEntries))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = batch;
            await command.ExecuteNonQueryAsync();
        }
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "SqlServer";
}
