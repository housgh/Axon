using Axon.Client.DependencyInjection;
using Axon.Client.Services;

var builder = WebApplication.CreateBuilder(args);

var axonServerUrl = builder.Configuration["Axon:ServerUrl"]
    ?? throw new InvalidOperationException("Axon:ServerUrl is not configured.");
builder.Services.AddAxonClient(axonServerUrl);

// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Always on, not just in Development: this is a demo project run via docker-compose (see
// deploy/docker-compose.yml), where ASPNETCORE_ENVIRONMENT is Production by default - gating
// Swagger to Development would silently hide it in that setup, the main way this project runs.
app.UseSwagger();
app.UseSwaggerUI();

app.MapGet("/", () => Results.Ok(new { machine = Environment.MachineName, connectedTo = axonServerUrl }));

app.MapGet("/enqueue", async (IAxonClient axonClient) =>
{
    var jobId = await axonClient.EnqueueAsync<DemoJobs>(x => x.SayHello("Hello from a 3-server, Redis-backplaned, SQL Server-backed Axon cluster!"));
    return Results.Ok(new { jobId });
});

app.MapGet("/enqueue-many/{count:int}", async (IAxonClient axonClient, int count) =>
{
    var jobIds = new List<string>();
    for (var i = 0; i < count; i++)
    {
        jobIds.Add(await axonClient.EnqueueAsync<DemoJobs>(x => x.SayHello($"Job #{i}")));
    }
    return Results.Ok(new { count = jobIds.Count, jobIds });
});

app.MapGet("/recurring", async (IAxonClient axonClient) =>
{
    await axonClient.AddOrUpdateRecurringAsync<DemoJobs>(
        "cluster-heartbeat",
        "* * * * *",
        x => x.SayHello("Recurring tick from the multi-instance cluster"));
    return Results.Ok(new { recurringJobId = "cluster-heartbeat" });
});

app.Run();

public class DemoJobs
{
    public void SayHello(string message) => Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] [{Environment.MachineName}] {message}");
}
