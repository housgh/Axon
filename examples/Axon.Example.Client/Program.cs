using Axon.Client.DependencyInjection;
using Axon.Client.Services;

var builder = WebApplication.CreateBuilder(args);

var axonServerUrl = builder.Configuration["Axon:ServerUrl"]
    ?? throw new InvalidOperationException("Axon:ServerUrl is not configured.");
builder.Services.AddAxonClient(axonServerUrl);

var app = builder.Build();

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
