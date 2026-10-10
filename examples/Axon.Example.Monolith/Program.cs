using Axon.Client.Services;
using Axon.Server.DependencyInjection;
using Axon.SqlServer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;

// A single deployable that both schedules and runs its own jobs: no Axon.Client package, no
// server URL, no WebSocket. Everything below beyond AddInProcessClient() is opt-in via config, so
// a bare `dotnet run` gets an in-memory, unauthenticated single instance, while
// deploy/monolith/docker-compose.yml runs 3 instances sharing SQL Server.
var builder = WebApplication.CreateBuilder(args);

var instanceName = builder.Configuration["Axon:InstanceName"] ?? Environment.MachineName;
var sqlConnectionString = builder.Configuration["Axon:SqlConnectionString"];

var axon = builder.Services.AddAxonServer()
    .AddAxonDashboard()
    .AddInProcessClient(o =>
    {
        o.DeviceName = instanceName;
        if (builder.Configuration.GetValue<int?>("Axon:MaxConcurrentJobs") is { } maxConcurrentJobs)
            o.MaxConcurrentJobs = maxConcurrentJobs;
    });

if (builder.Configuration.GetSection("Axon:DashboardUsers").Exists())
{
    axon.AddAuthentication(auth => builder.Configuration.GetSection("Axon:DashboardUsers").Bind(auth.Users));
}

if (!string.IsNullOrEmpty(sqlConnectionString))
{
    builder.Services.AddAxonSqlServerStore(sqlConnectionString);
}

// Behind a load balancer the dashboard login cookie must validate on every instance, so they all
// need the same Data Protection key ring - see deploy/multi-instance/README.md's "deployment lesson" section.
var dataProtectionKeysPath = builder.Configuration["Axon:DataProtectionKeysPath"];
if (!string.IsNullOrEmpty(dataProtectionKeysPath))
{
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath))
        .SetApplicationName("Axon.Example.Monolith");
}

builder.Services.AddScoped<ISampleService, SampleService>();

var app = builder.Build();

if (!string.IsNullOrEmpty(sqlConnectionString))
{
    await EnsureDatabaseAndSchemaAsync(sqlConnectionString, app.Logger);
}

app.UseAxonServer();

app.MapGet("/", () => Results.Ok(new { instance = instanceName, dashboard = "/axon/dashboard" }));

app.MapGet("/enqueue", async (IAxonClient axonClient) =>
{
    var jobId = await axonClient.EnqueueAsync<DemoJobs>(x => x.SayHello("Hello from a monolith!"));
    return Results.Ok(new { jobId });
});

app.MapGet("/enqueue-many/{count:int}", async (IAxonClient axonClient, int count) =>
{
    var jobIds = new List<string>();
    for (var i = 0; i < count; i++)
    {
        var n = i;
        jobIds.Add(await axonClient.EnqueueAsync<DemoJobs>(x => x.SayHello($"Job #{n} (enqueued on {instanceName})")));
    }
    return Results.Ok(new { enqueuedOn = instanceName, count = jobIds.Count, jobIds });
});

// Jobs that take a few seconds each: once an instance's MaxConcurrentJobs slots are full, the
// rest stay Enqueued for the other instances' polls - watch them spread across the fleet.
app.MapGet("/enqueue-slow/{count:int}", async (IAxonClient axonClient, int count) =>
{
    var jobIds = new List<string>();
    for (var i = 0; i < count; i++)
    {
        var n = i;
        jobIds.Add(await axonClient.EnqueueAsync<DemoJobs>(x => x.SlowHello($"Slow job #{n}", 5)));
    }
    return Results.Ok(new { enqueuedOn = instanceName, count = jobIds.Count, jobIds });
});

app.MapGet("/schedule", async (IAxonClient axonClient) =>
{
    var jobId = await axonClient.ScheduleAsync<DemoJobs>(TimeSpan.FromSeconds(30), x => x.SayHello("Hello, 30 seconds later"));
    return Results.Ok(new { jobId });
});

app.MapGet("/continuation", async (IAxonClient axonClient) =>
{
    var parentId = await axonClient.EnqueueAsync<DemoJobs>(x => x.SayHello("Parent job"));
    var childId = await axonClient.ContinueWithAsync<DemoJobs>(parentId, x => x.SayHello("Runs after the parent succeeds"));
    return Results.Ok(new { parentId, childId });
});

app.MapGet("/recurring", async (IAxonClient axonClient) =>
{
    await axonClient.AddOrUpdateRecurringAsync<DemoJobs>("monolith-heartbeat", "* * * * *", x => x.SayHello("Recurring tick"));
    return Results.Ok(new { recurringJobId = "monolith-heartbeat" });
});

app.MapGet("/dependency", async (IAxonClient axonClient) =>
{
    var jobId = await axonClient.EnqueueAsync<ISampleService>(x => x.Run());
    return Results.Ok(new { jobId });
});

app.Run();

// Creates the database if missing and applies the idempotent schema, retrying while SQL Server is
// still starting - so the compose stack works from a clean volume with no manual setup step. Same
// approach as Axon.Example.Server; safe with several instances starting at once.
static async Task EnsureDatabaseAndSchemaAsync(string connectionString, ILogger logger)
{
    var builder = new SqlConnectionStringBuilder(connectionString);
    var databaseName = builder.InitialCatalog;
    builder.InitialCatalog = "master";
    var masterConnectionString = builder.ConnectionString;

    var schemaSql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Schema.sql"));

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

public class DemoJobs
{
    public void SayHello(string message) => Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] [{Environment.MachineName}] {message}");

    public void SlowHello(string message, long seconds)
    {
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        SayHello(message);
    }
}

public interface ISampleService
{
    void Run();
}

public class SampleService(ILogger<SampleService> logger) : ISampleService
{
    public void Run() => logger.LogInformation("Running SampleService with constructor-injected dependencies");
}
