using Axon.Server.DependencyInjection;
using Axon.Server.Redis;
using Axon.SqlServer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;

var builder = WebApplication.CreateBuilder(args);

var sqlConnectionString = builder.Configuration["Axon:SqlConnectionString"]
    ?? throw new InvalidOperationException("Axon:SqlConnectionString is not configured.");
var redisConnectionString = builder.Configuration["Axon:RedisConnectionString"]
    ?? throw new InvalidOperationException("Axon:RedisConnectionString is not configured.");
var instanceName = builder.Configuration["Axon:InstanceName"] ?? Environment.MachineName;

builder.Services.AddAxonServer()
    .AddAxonDashboard()
    .AddAuthentication(auth =>
    {
        builder.Configuration.GetSection("Axon:DashboardUsers").Bind(auth.Users);
        if (auth.Users.Count == 0)
            throw new InvalidOperationException("Axon:DashboardUsers is not configured.");
    })
    .AddRedisBackplane(redisConnectionString)
    .AddJobCleanup(retention: TimeSpan.FromDays(30));

builder.Services.AddAxonSqlServerStore(sqlConnectionString);

// The dashboard auth cookie is protected (signed/encrypted) with ASP.NET Core's Data Protection
// keys, which by default are per-instance and ephemeral - so a cookie issued by one instance
// fails to validate on another, even though DashboardUsers/the cookie scheme are identical
// across all 3. Persisting the key ring to a volume shared by every instance is the standard
// fix for cookie auth behind a load balancer with no sticky sessions.
var dataProtectionKeysPath = builder.Configuration["Axon:DataProtectionKeysPath"];
if (!string.IsNullOrEmpty(dataProtectionKeysPath))
{
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath))
        .SetApplicationName("Axon.Example.Server");
}

var app = builder.Build();

// Every instance creates the database (if missing) and applies the (idempotent,
// IF NOT EXISTS-guarded) schema at startup, so this compose stack works from a clean SQL Server
// volume with no manual setup step. Safe under multiple instances starting concurrently: SQL
// Server serializes DDL against the same objects.
await EnsureDatabaseAndSchemaAsync(sqlConnectionString, app.Logger);

app.MapGet("/", () => Results.Ok(new { instance = instanceName, machine = Environment.MachineName }));

app.UseAxonServer();

app.Run();

static async Task EnsureDatabaseAndSchemaAsync(string connectionString, ILogger logger)
{
    var builder = new SqlConnectionStringBuilder(connectionString);
    var databaseName = builder.InitialCatalog;
    builder.InitialCatalog = "master";
    var masterConnectionString = builder.ConnectionString;

    var schemaPath = Path.Combine(AppContext.BaseDirectory, "Schema.sql");
    var schemaSql = await File.ReadAllTextAsync(schemaPath);

    const int maxAttempts = 20;
    for (var attempt = 1; attempt <= maxAttempts; attempt++)
    {
        try
        {
            await using (var masterConnection = new SqlConnection(masterConnectionString))
            {
                await masterConnection.OpenAsync();
                await using var createDbCommand = masterConnection.CreateCommand();
                createDbCommand.CommandText =
                    $"IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE name = '{databaseName}') CREATE DATABASE [{databaseName}]";
                await createDbCommand.ExecuteNonQueryAsync();
            }

            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = schemaSql;
            await command.ExecuteNonQueryAsync();
            logger.LogInformation("Database and schema ready.");
            return;
        }
        catch (Exception e) when (attempt < maxAttempts)
        {
            logger.LogWarning(e, "Waiting for SQL Server (attempt {Attempt}/{MaxAttempts})...", attempt, maxAttempts);
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
    }
}
