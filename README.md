# Axon

<img src="axon.png" alt="Axon logo" width="160" />

A lightweight, Hangfire-style background job scheduler for .NET microservices, with one key difference: **the job scheduler and the job's implementation don't have to live in the same codebase.**

Axon.Server acts purely as a scheduler/dispatcher — it never executes your job code. Each microservice runs an Axon.Client that both enqueues jobs (to run on itself, later) and executes them when the server dispatches them back over a persistent connection. This makes Axon a good fit for RPC-style architectures where Hangfire's "storage + workers share one deployable" model doesn't apply.

**Transport:** dispatch uses [SignalR](https://learn.microsoft.com/aspnet/core/signalr/introduction) over WebSockets — each `Axon.Client` opens one long-lived WebSocket connection to `Axon.Server` (with automatic reconnect) and the server pushes jobs down that connection as they become due, rather than clients polling for work. This means:
- Both ends need a network path that allows WebSocket upgrades (most reverse proxies/load balancers need this enabled explicitly).
- A client only receives jobs while its connection is open; if it disconnects, dispatched-but-unacknowledged jobs are reclaimed and retried (see [docs/architecture.md](docs/architecture.md)) rather than lost.
- Running `Axon.Server` behind multiple instances requires a backplane (`Axon.Server.Redis`, see below) or sticky sessions, since a client's WebSocket is pinned to whichever server instance accepted it. Dispatch itself is safe across instances sharing `Axon.Store.SqlServer` — each instance atomically claims a job before dispatching it (see [docs/architecture.md](docs/architecture.md#multi-instance-dispatch-safety)), so at most one instance ever dispatches a given job even if several see it as due in the same poll cycle. The backplane requirement is specifically about routing a client's *inbound* WebSocket traffic to the right instance, not about dispatch correctness.

## Packages

| Package | Description |
|---|---|
| `Axon.Core` | Shared models and enums used by both client and server. |
| `Axon.Client` | Enqueue jobs, schedule recurring jobs, and execute dispatched jobs inside your service. |
| `Axon.Server` | The scheduler/dispatcher: SignalR hub, job store, background processors, and an admin dashboard. |
| `Axon.Store.SqlServer` | SQL Server-backed persistence for `Axon.Server` (in-memory storage is used by default). |
| `Axon.Server.Redis` | Redis SignalR backplane for `Axon.Server`, so job dispatch and dashboard push reach clients connected to any instance behind a load balancer. Optional, separate package so `Axon.Server` itself doesn't carry a Redis dependency; other backplane options may be added as their own packages later. |
| `Axon.Server.OpenTelemetry` | Wires an OpenTelemetry SDK to `Axon.Server`'s built-in metrics and traces (queue depth, dispatch latency, job outcome counters, dispatch/enqueue/ack spans). Optional, separate package for the same reason as `Axon.Server.Redis`. |

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

Add `Axon.Server.Redis` if you're running more than one `Axon.Server` instance behind a load balancer:

```bash
dotnet add package Axon.Server.Redis
```

Add `Axon.Server.OpenTelemetry` if you want metrics and traces for job dispatch, queue depth, and outcomes:

```bash
dotnet add package Axon.Server.OpenTelemetry
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

Passwords are configured in plain text (matching most secrets-manager/env-var workflows) but are never stored or compared in plain text at runtime: each is hashed once at startup with PBKDF2-HMAC-SHA256 and a random per-user salt, and only that hash is held afterward. Login attempts are rate-limited two ways: by client IP (5 attempts per 5 minutes) and by username (5 failures locks that username out for 5 minutes, independent of which IP the attempts came from) — either can trip first depending on the attack shape. Every login attempt and every write action (job retry/delete, recurring-job trigger/delete) is logged under the `Axon.Server.Services.AxonAuditLog` category with the acting username, so audit events can be routed/retained separately from regular application logs without touching your logging configuration.

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

### 3. (Optional) Register the Redis backplane

If you're running multiple `Axon.Server` instances behind a load balancer, chain `.AddRedisBackplane(...)` off `AddAxonServer()` so job dispatch and dashboard updates reach a client no matter which instance's WebSocket it's connected to:

```csharp
builder.Services.AddAxonServer()
    .AddRedisBackplane(builder.Configuration["Axon:RedisConnectionString"]!)
    .AddAxonDashboard();
```

Without this, a client's connection is pinned to whichever instance accepted it, so a job dispatched by instance A never reaches a client connected to instance B. (The dashboard's Servers/Clients tabs are a separate concern, fixed by `Axon.Store.SqlServer` rather than the backplane — see below.)

### 4. (Optional) Register observability

Chain `.AddOpenTelemetryObservability(...)` off `AddAxonServer()` to get metrics (queue depth, dispatch latency, job outcome counters) and traces (dispatch/enqueue/ack spans) for an OpenTelemetry SDK to collect:

```csharp
builder.Services.AddAxonServer()
    .AddAxonDashboard()
    .AddOpenTelemetryObservability(
        configureMetrics: metrics => metrics.AddOtlpExporter(),
        configureTracing: tracing => tracing.AddOtlpExporter());
```

This only registers Axon's meter/activity source with the SDK - configure whatever exporter you want (OTLP, console, Prometheus, etc.) via `configureMetrics`/`configureTracing`, the same way you would for any other OpenTelemetry SDK setup.

### 5. (Optional) Register job data cleanup

By default, completed jobs (`Succeeded`/`Failed`/`Skipped`) and their history are kept forever. Chain `.AddJobCleanup(...)` to purge them after a retention window, so the Jobs/JobHistory tables don't grow unbounded in a long-running deployment:

```csharp
builder.Services.AddAxonServer()
    .AddAxonDashboard()
    .AddJobCleanup(retention: TimeSpan.FromDays(30));
```

A background sweep (once an hour by default; override with the `pollInterval` parameter) deletes completed jobs whose most recent history entry is older than `retention`. `Enqueued`/`Scheduled`/`Processing`/`AwaitingParent` jobs are never touched, regardless of age.

### 6. Register the client

Point the client at wherever `Axon.Server` is hosted (its own process, or a different microservice's address). This opens the SignalR/WebSocket connection (`/hubs/axon`) that the server dispatches jobs over:

```csharp
builder.Services.AddAxonClient(axonBaseUrl); // e.g. "https://localhost:7221"
```

### 7. Map the server middleware and dashboard

```csharp
app.UseAxonServer();
```

This maps the SignalR hub (`/hubs/axon`) and, if opted into in step 1, the `/axon` API/dashboard. It also always maps two unauthenticated health check endpoints for orchestrators (Kubernetes, ECS, etc.), regardless of whether the API/dashboard is opted into or dashboard auth is configured:
- `GET /axon/health/live` — always `200 Healthy` once the process is up; use as a liveness probe.
- `GET /axon/health/ready` — `200 Healthy` only if the configured job store (in-memory or `Axon.Store.SqlServer`) can actually be reached; use as a readiness probe so an orchestrator stops routing traffic to an instance whose database connection is down.

### 8. Enqueue, schedule, and run recurring jobs

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

By default, a failed job retries up to 3 times with 10s/30s/2min backoff. Override this per job with an `AxonRetryPolicy` on `EnqueueAsync`/`ScheduleAsync`:

```csharp
var jobId = await axonClient.EnqueueAsync<MyClass>(x => x.WriteHelloWorld("Hello World"),
    retryPolicy: new AxonRetryPolicy { MaxAttempts = 5, RetryDelaysSeconds = [5, 30, 120] });
```

`MaxAttempts` is the total attempts including the first (so `5` means up to 4 retries). `RetryDelaysSeconds` is indexed by retry attempt (0-based); once exhausted, the last entry is reused for every further retry — so `[5, 30, 120]` with `MaxAttempts = 10` retries at 5s, 30s, then 120s, 120s, 120s, ... for the remaining attempts.

Cap how many jobs of a given type may be `Processing` at once across the whole fleet with a concurrency limit — useful for throttling against a rate-limited downstream dependency:

```csharp
var jobId = await axonClient.EnqueueAsync<MyClass>(x => x.SendEmail(...),
    concurrencyKey: "email-sender", maxConcurrent: 5);
```

At most 5 jobs sharing the `"email-sender"` key will be `Processing` at once; a 6th stays `Enqueued` until one of the 5 finishes (succeeds, fails terminally, or is reclaimed as orphaned). The limit is enforced atomically inside the same claim operation that makes multi-instance dispatch safe (see [docs/architecture.md#multi-instance-dispatch-safety](docs/architecture.md#multi-instance-dispatch-safety)), so it holds even with multiple `Axon.Server` instances racing to claim jobs sharing a key against `Axon.Store.SqlServer`.

Instead of passing `concurrencyKey`/`maxConcurrent` at every call site, declare a default on the method itself:

```csharp
public class EmailJobs
{
    [AxonConcurrencyLimit("email-sender", 5)]
    public void SendEmail(string to) { /* ... */ }
}
```

An explicit `concurrencyKey` argument on `EnqueueAsync`/`ScheduleAsync` always overrides the attribute when both are present.

Chain a job to run only after another one finishes with `ContinueWithAsync`:

```csharp
var parentJobId = await axonClient.EnqueueAsync<MyClass>(x => x.DownloadReport());
var childJobId = await axonClient.ContinueWithAsync<MyClass>(parentJobId, x => x.EmailReport());
```

The continuation sits in an `AwaitingParent` state — not dispatched, not counted against any concurrency limit — until the parent reaches a terminal state. If the parent succeeds, the continuation is promoted to `Enqueued`. If the parent fails (retries exhausted), the continuation is left permanently `Skipped` by default; pass `continueOnParentFailure: true` to run it anyway:

```csharp
await axonClient.ContinueWithAsync<MyClass>(parentJobId, x => x.CleanUpTempFiles(), continueOnParentFailure: true);
```

Continuations can be chained (A → B → C): each one only resolves once its own immediate parent finishes. If the parent has already finished by the time `ContinueWithAsync` is called, the continuation resolves immediately rather than waiting for a transition that already happened.

Recurring jobs use standard cron expressions and are idempotent by `recurringJobId` — calling `AddOrUpdateRecurringAsync` again with the same id updates the existing schedule instead of creating a duplicate:

```csharp
await axonClient.AddOrUpdateRecurringAsync<MyClass>(
    "hourly-hello",
    "0 * * * *",
    x => x.WriteHelloWorld("Recurring tick"));

await axonClient.RemoveRecurringAsync("hourly-hello");
```

### 9. Open the dashboard

Navigate to `/axon` on whichever host runs `Axon.Server` and sign in with a configured username/password. The dashboard is organized into four tabs:
- **Jobs** — job history, state, retry/delete.
- **Recurring jobs** — schedules, last/next run, trigger/remove.
- **Servers** — active `Axon.Server` instances, each heartbeating every 15s and shown offline once its heartbeat is more than 45s old. With the default in-memory store an instance only ever sees itself; use `Axon.Store.SqlServer` to see every instance behind a multi-instance deployment.
- **Clients** — connected devices, shown Idle or Processing depending on whether a job is currently dispatched to them. A SignalR connection is pinned to whichever instance accepted it, so with the default in-memory registry this list is instance-local (`Axon.Server.Redis` makes dispatch/push still reach the right client, but each instance only lists the clients connected to itself); use `Axon.Store.SqlServer` to see every connected client across the whole fleet, regardless of which instance it's connected to.

`ReadOnly` users see the same data but without retry/delete/trigger controls.

The dashboard updates in real time over a dedicated SignalR connection (`/axon/hub`, gated by the same dashboard auth) rather than polling: every job, recurring-job, server, or client change is pushed to open dashboard tabs the moment it happens. If that connection is ever unavailable (network blip, outbound access to the SignalR JS CDN blocked, etc.) the dashboard automatically falls back to polling every 5s and keeps retrying the push connection in the background.

See [Axon.Example](Axon.Example) for a complete, runnable ASP.NET Core project wired up end-to-end (server + client in the same process).

For a real horizontally-scaled deployment - 3 `Axon.Server` instances behind a load balancer, sharing SQL Server and a Redis backplane, with a separate client - see [deploy/](deploy) for a docker compose stack that runs the whole thing with one command.

## Architecture

See [docs/architecture.md](docs/architecture.md) for diagrams of the job dispatch flow, state machine, and crash/restart recovery.

## Observability

With `Axon.Server.OpenTelemetry` (see Usage above), `Axon.Server` emits:

**Metrics** (meter `Axon.Server`):
- `axon.jobs.enqueued`, `axon.jobs.dispatched`, `axon.jobs.claim_failed`, `axon.jobs.succeeded`, `axon.jobs.failed`, `axon.jobs.retried`, `axon.jobs.orphaned_reclaimed` — counters for each job-state transition.
- `axon.jobs.queue_depth` — jobs currently `Enqueued`/`Scheduled`, as of the most recent poll cycle.
- `axon.jobs.dispatch_latency` — time from a job being enqueued to being claimed for dispatch.
- `axon.jobs.execution_duration` — time from dispatch (claim) to the client acknowledging success, failure, or being reclaimed as orphaned.
- `axon.recurring_jobs.triggered` — recurring-job occurrences enqueued as a new job instance.

**Traces** (activity source `Axon.Server`): spans around enqueue, claim/dispatch, success/failure acknowledgement, and orphan reclaim, tagged with `axon.job_id`/`axon.device_name`.

These are plain `System.Diagnostics.Metrics`/`System.Diagnostics.ActivitySource` primitives — `Axon.Server` itself has no OpenTelemetry dependency, so they're emitted (at effectively zero cost) whether or not anything is listening. `Axon.Server.OpenTelemetry` just wires an OTel SDK to collect them; any other listener (including a host app's own OTel setup, via `AddMeter("Axon.Server")`/`AddSource("Axon.Server")`) can attach to them directly instead.

## Operational maturity

- **Health checks**: `/axon/health/live` and `/axon/health/ready` (see Usage step 6 above) for orchestrator liveness/readiness probes.
- **Graceful shutdown**: abrupt termination is already safe by design — see [docs/architecture.md#graceful-shutdown](docs/architecture.md#graceful-shutdown) for why no explicit drain logic is needed beyond ASP.NET Core's standard `HostOptions.ShutdownTimeout`.
- **Config validation**: `AddAxonSqlServerStore`, `AddRedisBackplane`, `AddAxonClient`, and `.AddAuthentication(...)` all validate their required configuration (connection strings, URLs, dashboard users) at startup and throw immediately with a clear message, rather than failing confusingly on first use deep in a background service.

## Testing

- `tests/Axon.Tests.Unit` — unit tests for the job/recurring-job services, `AxonJobProcessor`'s dispatch logic, `AxonHub`, and the in-memory store, using [NSubstitute](https://nsubstitute.github.io/) for mocking.
- `tests/Axon.Tests.Integration` — integration tests for `Axon.Store.SqlServer` against a real SQL Server instance spun up via [Testcontainers](https://dotnet.testcontainers.org/) (requires Docker). These cover the same transactional-write and atomic-claim guarantees described above, including a 20-way concurrent `TryClaimJob` race to verify exactly one caller ever wins.

```bash
dotnet test
```

runs both projects; Docker must be running locally for the integration tests to start their SQL Server container.

## Status

This is a proof of concept. See the [repository](https://github.com/housgh/Axon) for source, issues, and usage examples.
