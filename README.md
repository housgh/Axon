# Axon

A lightweight, Hangfire-style background job scheduler for .NET microservices, with one key difference: **the job scheduler and the job's implementation don't have to live in the same codebase.**

Axon.Server acts purely as a scheduler/dispatcher — it never executes your job code. Each microservice runs an Axon.Client that both enqueues jobs (to run on itself, later) and executes them when the server dispatches them back over a persistent connection. This makes Axon a good fit for RPC-style architectures where Hangfire's "storage + workers share one deployable" model doesn't apply.

**Transport:** dispatch uses [SignalR](https://learn.microsoft.com/aspnet/core/signalr/introduction) over WebSockets — each `Axon.Client` opens one long-lived WebSocket connection to `Axon.Server` (with automatic reconnect) and the server pushes jobs down that connection as they become due, rather than clients polling for work. This means:
- Both ends need a network path that allows WebSocket upgrades (most reverse proxies/load balancers need this enabled explicitly).
- A client only receives jobs while its connection is open; if it disconnects, dispatched-but-unacknowledged jobs are reclaimed and retried (see [docs/architecture.md](docs/architecture.md)) rather than lost.
- Running `Axon.Server` behind multiple instances requires a backplane (e.g. `AddSignalR().AddStackExchangeRedis(...)`) or sticky sessions, since a client's WebSocket is pinned to whichever server instance accepted it.

## Packages

| Package | Description |
|---|---|
| `Axon.Core` | Shared models and enums used by both client and server. |
| `Axon.Client` | Enqueue jobs, schedule recurring jobs, and execute dispatched jobs inside your service. |
| `Axon.Server` | The scheduler/dispatcher: SignalR hub, job store, background processors, and an admin dashboard. |
| `Axon.Store.SqlServer` | SQL Server-backed persistence for `Axon.Server` (in-memory storage is used by default). |

## Installation

Each microservice that hosts the scheduler/dashboard needs `Axon.Server`; each microservice that enqueues or executes jobs (including the one hosting the server, if it does both) needs `Axon.Client`:

```bash
dotnet add package Axon.Server
dotnet add package Axon.Client
```

Add `Axon.Store.SqlServer` if you want job/recurring-job state to survive a restart instead of living in memory:

```bash
dotnet add package Axon.Store.SqlServer
```

## Usage

### 1. Register the server

`AddAxonServer()` alone only registers the core scheduler (SignalR hub, job store, background processors) — no HTTP surface is mapped. Opt into the pieces you want:

```csharp
builder.Services.AddAxonServer()
    .AddAxonDashboard(); // JSON API (/axon/jobs, /axon/recurring-jobs, ...) + HTML dashboard
```

Use `.AddAxonApiEndpoints()` instead of `.AddAxonDashboard()` if you want the JSON API without the HTML pages (e.g. a service that's queried by another internal tool rather than browsed directly). Calling neither leaves `/axon` entirely unmapped — only the SignalR hub used for job dispatch stays active.

By default, whichever surface you enable is **open to anyone who can reach it** — no login required. Chain `.AddAuthentication(...)` to require username/password sign-in:

```csharp
builder.Services.AddAxonServer()
    .AddAxonDashboard()
    .AddAuthentication(auth =>
    {
        builder.Configuration.GetSection("Axon:DashboardUsers").Bind(auth.Users);
    });
```

If you skip `.AddAuthentication(...)`, Axon logs a startup warning so an open dashboard doesn't go unnoticed.

Axon's dashboard auth uses its own cookie scheme (`AxonDashboard`) and never sets itself as the app's default authentication scheme, so it coexists safely with a host app's own `AddAuthentication(...)` (JWT bearer, its own cookie, etc.) — enabling one doesn't override or get overridden by the other, regardless of registration order.

Dashboard access is username/password with per-user roles: `Admin` users can view and take actions (retry, delete, trigger); `ReadOnly` users can only view. Configure one or more users (`appsettings.json`, environment variable, etc.):

```json
{
  "Axon": {
    "DashboardUsers": [
      { "Username": "admin", "Password": "P@ssw0rd", "Role": "Admin" },
      { "Username": "viewer", "Password": "P@ssw0rd", "Role": "ReadOnly" }
    ]
  }
}
```

### 2. (Optional) Register SQL Server-backed storage

By default job and recurring-job state live in memory and are lost on restart. To persist them, run `Axon.Store.SqlServer/Schema.sql` against your database, then:

```csharp
var sqlConnectionString = builder.Configuration["Axon:SqlConnectionString"];
if (!string.IsNullOrEmpty(sqlConnectionString))
{
    builder.Services.AddAxonSqlServerStore(sqlConnectionString);
}
```

### 3. Register the client

Point the client at wherever `Axon.Server` is hosted (its own process, or a different microservice's address). This opens the SignalR/WebSocket connection (`/hubs/axon`) that the server dispatches jobs over:

```csharp
builder.Services.AddAxonClient(axonBaseUrl); // e.g. "https://localhost:7221"
```

### 4. Map the server middleware and dashboard

```csharp
app.UseAxonServer();
```

This maps the SignalR hub (`/hubs/axon`) and, if opted into in step 1, the `/axon` API/dashboard.

### 5. Enqueue, schedule, and run recurring jobs

Inject `IAxonClient` and call methods on any plain class — Axon serializes the method call as an expression tree, sends it to the server, and the server dispatches it back to a connected client for execution:

```csharp
public class WeatherForecastController(IAxonClient axonClient) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        // Run as soon as a worker is available
        var jobId = await axonClient.EnqueueAsync<MyClass>(x => x.WriteHelloWorld("Hello World"));
        return Ok(new { JobId = jobId });
    }

    [HttpGet("schedule")]
    public async Task<IActionResult> Schedule()
    {
        // Run once, after a delay
        var jobId = await axonClient.ScheduleAsync<MyClass>(TimeSpan.FromMinutes(1), x => x.WriteHelloWorld("Delayed Hello"));
        return Ok(new { JobId = jobId });
    }
}

public class MyClass
{
    public void WriteHelloWorld(string message) => Console.WriteLine(message);
}
```

Recurring jobs use standard cron expressions and are idempotent by `recurringJobId` — calling `AddOrUpdateRecurringAsync` again with the same id updates the existing schedule instead of creating a duplicate:

```csharp
await axonClient.AddOrUpdateRecurringAsync<MyClass>(
    "hourly-hello",
    "0 * * * *",
    x => x.WriteHelloWorld("Recurring tick"));

await axonClient.RemoveRecurringAsync("hourly-hello");
```

### 6. Open the dashboard

Navigate to `/axon` on whichever host runs `Axon.Server` and sign in with a configured username/password to view jobs, job history, and recurring job definitions. `ReadOnly` users see the same data but without retry/delete/trigger controls.

See [Axon.Example](Axon.Example) for a complete, runnable ASP.NET Core project wired up end-to-end (server + client in the same process).

## Architecture

See [docs/architecture.md](docs/architecture.md) for diagrams of the job dispatch flow, state machine, and crash/restart recovery.

## Status

This is a proof of concept. See the [repository](https://github.com/housgh/Axon) for source, issues, and usage examples.
