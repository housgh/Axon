# Axon

<img src="axon.png" alt="Axon logo" width="160" />

A lightweight background job scheduler for .NET microservices, built around one key idea: **the job scheduler and the job's implementation don't have to live in the same codebase.**

Axon.Server acts purely as a scheduler/dispatcher — it never executes your job code. Each microservice runs an Axon.Client that both enqueues jobs (to run on itself, later) and executes them when the server dispatches them back over a persistent connection. This makes Axon a good fit for RPC-style architectures where the scheduler and the workers that run jobs aren't part of the same deployable.

**Transport:** dispatch uses [SignalR](https://learn.microsoft.com/aspnet/core/signalr/introduction) over WebSockets — each `Axon.Client` opens one long-lived WebSocket connection to `Axon.Server` (with automatic reconnect) and the server pushes jobs down that connection as they become due, rather than clients polling for work. This means:
- Both ends need a network path that allows WebSocket upgrades (most reverse proxies/load balancers need this enabled explicitly).
- A client only receives jobs while its connection is open; if it disconnects, dispatched-but-unacknowledged jobs are reclaimed and retried (see [docs/architecture.md](docs/architecture.md)) rather than lost.
- Running `Axon.Server` behind multiple instances requires a backplane (`Axon.Server.Redis`, see below) or sticky sessions, since a client's WebSocket is pinned to whichever server instance accepted it. Dispatch itself is safe across instances sharing `Axon.Store.SqlServer`, `Axon.Store.Postgres`, `Axon.Store.MySql`, or `Axon.Store.MongoDb` — each instance atomically claims a job before dispatching it (see [docs/architecture.md](docs/architecture.md#multi-instance-dispatch-safety)), so at most one instance ever dispatches a given job even if several see it as due in the same poll cycle. The backplane requirement is specifically about routing a client's *inbound* WebSocket traffic to the right instance, not about dispatch correctness.

## Packages

NuGet package IDs are prefixed `GoAxon.*` (the `Axon.*` prefix is reserved by another publisher);
namespaces, project names, and everything else in this repo are still `Axon.*`.

| Package | NuGet ID | Description |
|---|---|---|
| `Axon.Core` | `GoAxon.Core` | Shared models and enums used by both client and server. |
| `Axon.Client` | `GoAxon.Client` | Enqueue jobs, schedule recurring jobs, and execute dispatched jobs inside your service. |
| `Axon.Server` | `GoAxon.Server` | The scheduler/dispatcher: SignalR hub, job store, background processors, and an admin dashboard. |
| `Axon.Store.SqlServer` | `GoAxon.Store.SqlServer` | SQL Server-backed persistence for `Axon.Server` (in-memory storage is used by default). |
| `Axon.Store.Postgres` | `GoAxon.Store.Postgres` | PostgreSQL-backed persistence for `Axon.Server`, same role as `Axon.Store.SqlServer`. |
| `Axon.Store.MySql` | `GoAxon.Store.MySql` | MySQL-backed persistence for `Axon.Server`, same role as `Axon.Store.SqlServer`. |
| `Axon.Store.SQLite` | `GoAxon.Store.SQLite` | SQLite-backed persistence for `Axon.Server`. Single-instance/local-dev only — see the note below. |
| `Axon.Store.MongoDb` | `GoAxon.Store.MongoDb` | MongoDB-backed persistence for `Axon.Server`, same role as `Axon.Store.SqlServer`. Requires a replica set — see the note below. |
| `Axon.Server.Redis` | `GoAxon.Server.Redis` | Redis SignalR backplane for `Axon.Server`, so job dispatch and dashboard push reach clients connected to any instance behind a load balancer. Optional, separate package so `Axon.Server` itself doesn't carry a Redis dependency; other backplane options may be added as their own packages later. |
| `Axon.Server.OpenTelemetry` | `GoAxon.Server.OpenTelemetry` | Wires an OpenTelemetry SDK to `Axon.Server`'s built-in metrics and traces (queue depth, dispatch latency, job outcome counters, dispatch/enqueue/ack spans). Optional, separate package for the same reason as `Axon.Server.Redis`. |

## Installation

Each microservice that hosts the scheduler/dashboard needs `Axon.Server`; each microservice that enqueues or executes jobs (including the one hosting the server, if it does both) needs `Axon.Client`:

```bash
dotnet add package GoAxon.Server
dotnet add package GoAxon.Client
```

Add `Axon.Store.SqlServer`, `Axon.Store.Postgres`, `Axon.Store.MySql`, `Axon.Store.SQLite`, or `Axon.Store.MongoDb` if you want job/recurring-job state to survive a restart instead of living in memory:

```bash
dotnet add package GoAxon.Store.SqlServer
# or
dotnet add package GoAxon.Store.Postgres
# or
dotnet add package GoAxon.Store.MySql
# or
dotnet add package GoAxon.Store.SQLite
# or
dotnet add package GoAxon.Store.MongoDb
```

`Axon.Store.SQLite` is the odd one out: SQLite's single-writer model means it does **not**
support the multi-instance dispatch scenario the other backends target (see
[docs/architecture.md#multi-instance-dispatch-safety](docs/architecture.md#multi-instance-dispatch-safety))
— use it for a single-instance deployment or local development, not a load-balanced fleet.

`Axon.Store.MongoDb` requires the target MongoDB deployment to be a **replica set** (even a
single-node one) rather than a standalone server — `TryClaimJob`'s `ConcurrencyKey`/`MaxConcurrent`
check runs inside a multi-document transaction, and standalone MongoDB servers cannot open one at
all (`MongoClient` connects fine either way; only that one operation fails). Any managed MongoDB
offering (Atlas, DocumentDB-compatible services, etc.) is already a replica set by default; for a
self-hosted single-node setup, start `mongod --replSet rs0` and run `rs.initiate()` once. There is
also no `Schema.sql` to run first — collections are created implicitly on first write — but call
`Axon.MongoDb.Indexes.EnsureIndexesAsync(connectionString, databaseName)` once if you want the
indexes the store's queries rely on for performance at scale (optional; queries still work
without them, just slower on a large collection).

Add `Axon.Server.Redis` if you're running more than one `Axon.Server` instance behind a load balancer:

```bash
dotnet add package GoAxon.Server.Redis
```

Add `Axon.Server.OpenTelemetry` if you want metrics and traces for job dispatch, queue depth, and outcomes:

```bash
dotnet add package GoAxon.Server.OpenTelemetry
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

### 2. (Optional) Register durable storage

By default job and recurring-job state live in memory and are lost on restart. To persist them, run the chosen backend's `Schema.sql` against your database, then register it:

```csharp
// SQL Server: run Axon.Store.SqlServer/Schema.sql first
var sqlConnectionString = builder.Configuration["Axon:SqlConnectionString"];
if (!string.IsNullOrEmpty(sqlConnectionString))
{
    builder.Services.AddAxonSqlServerStore(sqlConnectionString);
}

// PostgreSQL: run Axon.Store.Postgres/Schema.sql first
var postgresConnectionString = builder.Configuration["Axon:PostgresConnectionString"];
if (!string.IsNullOrEmpty(postgresConnectionString))
{
    builder.Services.AddAxonPostgresStore(postgresConnectionString);
}

// MySQL: run Axon.Store.MySql/Schema.sql first
var mySqlConnectionString = builder.Configuration["Axon:MySqlConnectionString"];
if (!string.IsNullOrEmpty(mySqlConnectionString))
{
    builder.Services.AddAxonMySqlStore(mySqlConnectionString);
}

// SQLite: run Axon.Store.SQLite/Schema.sql first. Single-instance/local-dev only.
var sqliteConnectionString = builder.Configuration["Axon:SQLiteConnectionString"];
if (!string.IsNullOrEmpty(sqliteConnectionString))
{
    builder.Services.AddAxonSQLiteStore(sqliteConnectionString);
}

// MongoDB: must point at a replica set. No schema to run first (optionally call
// Axon.MongoDb.Indexes.EnsureIndexesAsync once).
var mongoConnectionString = builder.Configuration["Axon:MongoConnectionString"];
if (!string.IsNullOrEmpty(mongoConnectionString))
{
    builder.Services.AddAxonMongoDbStore(mongoConnectionString, databaseName: "axon");
}
```

Only register one backend — whichever call runs last wins, since they all replace the same underlying services.

### 3. (Optional) Register the Redis backplane

If you're running multiple `Axon.Server` instances behind a load balancer, chain `.AddRedisBackplane(...)` off `AddAxonServer()` so job dispatch and dashboard updates reach a client no matter which instance's WebSocket it's connected to:

```csharp
builder.Services.AddAxonServer()
    .AddRedisBackplane(builder.Configuration["Axon:RedisConnectionString"]!)
    .AddAxonDashboard();
```

Without this, a client's connection is pinned to whichever instance accepted it, so a job dispatched by instance A never reaches a client connected to instance B. (The dashboard's Servers/Clients tabs are a separate concern, fixed by `Axon.Store.SqlServer`/`Axon.Store.Postgres`/`Axon.Store.MySql`/`Axon.Store.MongoDb` rather than the backplane — see below.)

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

### 5. (Optional) Override job data cleanup

`Succeeded` jobs and their history are cleaned up automatically - a background sweep (once an hour by default) purges `Succeeded` jobs whose most recent history entry is older than 1 day, so the Jobs/JobHistory tables don't grow unbounded in a long-running deployment. `Failed` and `Skipped` jobs are kept forever regardless of age, and dashboard/API job counts (`GET /axon/stats`) never drop because of this cleanup - a job that succeeded and was later purged still counts toward the lifetime total. Chain `.AddJobCleanup(...)` to override the retention/poll interval:

```csharp
builder.Services.AddAxonServer()
    .AddAxonDashboard()
    .AddJobCleanup(retention: TimeSpan.FromDays(30));
```

`Enqueued`/`Scheduled`/`Processing`/`AwaitingParent` jobs are never touched, regardless of age.

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
- `GET /axon/health/ready` — `200 Healthy` only if the configured job store (in-memory, `Axon.Store.SqlServer`, `Axon.Store.Postgres`, `Axon.Store.MySql`, `Axon.Store.SQLite`, or `Axon.Store.MongoDb`) can actually be reached; use as a readiness probe so an orchestrator stops routing traffic to an instance whose database connection is down.

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

By default, a failed job retries up to 3 times with 10s/30s/2min backoff. Override this per job with an `AxonRetryPolicy` on the `AxonEnqueueOptions` accepted by `EnqueueAsync`/`ScheduleAsync`/`ContinueWithAsync`:

```csharp
var jobId = await axonClient.EnqueueAsync<MyClass>(x => x.WriteHelloWorld("Hello World"),
    new AxonEnqueueOptions { RetryPolicy = new AxonRetryPolicy { MaxAttempts = 5, RetryDelaysSeconds = [5, 30, 120] } });
```

`MaxAttempts` is the total attempts including the first (so `5` means up to 4 retries). `RetryDelaysSeconds` is indexed by retry attempt (0-based); once exhausted, the last entry is reused for every further retry — so `[5, 30, 120]` with `MaxAttempts = 10` retries at 5s, 30s, then 120s, 120s, 120s, ... for the remaining attempts.

Cap how many jobs of a given type may be `Processing` at once across the whole fleet with a concurrency limit — useful for throttling against a rate-limited downstream dependency:

```csharp
var jobId = await axonClient.EnqueueAsync<MyClass>(x => x.SendEmail(...),
    new AxonEnqueueOptions { ConcurrencyKey = "email-sender", MaxConcurrent = 5 });
```

At most 5 jobs sharing the `"email-sender"` key will be `Processing` at once; a 6th stays `Enqueued` until one of the 5 finishes (succeeds, fails terminally, or is reclaimed as orphaned). The limit is enforced atomically inside the same claim operation that makes multi-instance dispatch safe (see [docs/architecture.md#multi-instance-dispatch-safety](docs/architecture.md#multi-instance-dispatch-safety)), so it holds even with multiple `Axon.Server` instances racing to claim jobs sharing a key against `Axon.Store.SqlServer`, `Axon.Store.Postgres`, `Axon.Store.MySql`, or `Axon.Store.MongoDb`.

Instead of passing `concurrencyKey`/`maxConcurrent` at every call site, declare a default on the method itself:

```csharp
public class EmailJobs
{
    [AxonConcurrencyLimit("email-sender", 5)]
    public void SendEmail(string to) { /* ... */ }
}
```

An explicit `AxonEnqueueOptions.ConcurrencyKey` always overrides the attribute when both are present.

Shift a job's dispatch order with `Priority` — `Low`, `Medium` (the default), `High`, or `Critical`:

```csharp
var jobId = await axonClient.EnqueueAsync<MyClass>(x => x.RotateSecurityKeys(),
    new AxonEnqueueOptions { Priority = JobPriority.Critical });
```

This isn't a hard tier — a flood of `High` jobs can't starve a `Low` job forever. Each priority
level gets a fixed "boost", a free head start (in minutes of virtual age) applied when jobs are
sorted for dispatch: `Low` gets none, `Medium` gets 5 min, `High` gets 15 min, `Critical` gets 60
min. A job's real age plus its boost decides dispatch order, so a `High` job only cuts ahead of
an already-waiting `Low` job by up to 15 minutes — a `Low` job that's been waiting longer than
that still goes first. If nothing else is competing for a dispatch slot, priority makes no
difference at all — the boost only matters when jobs are actually contending; a lone `Low` job
still dispatches on the very next poll.

Worked example: a `High` job created 1 minute ago dispatches before a `Low` job created 1 minute
ago (High's 15-minute boost outweighs the tiny age difference), but a `Low` job created 20
minutes ago still dispatches before that same 1-minute-old `High` job — 20 real minutes beats a
15-minute boost. See [docs/architecture.md#job-priority](docs/architecture.md#job-priority) for
the full scoring formula, the max-headstart table, and per-backend implementation notes.

### Queues

Route a job through a named queue instead of the implicit `"default"` one:

```csharp
var jobId = await axonClient.EnqueueAsync<MyClass>(x => x.ChargeInvoice(...),
    new AxonEnqueueOptions { QueueName = "billing" });
```

An `Axon.Server` instance only claims/dispatches jobs on queues it was registered for, via
`.AddQueues(...)` chained off `AddAxonServer()`:

```csharp
builder.Services.AddAxonServer()
    .AddAxonDashboard()
    .AddQueues("billing"); // this instance serves "billing" AND "default"
```

`.AddQueues(...)` always adds `"default"` alongside whatever you pass — it's never an exclusive
allowlist, so calling `.AddQueues("billing")` doesn't stop an instance from also running ordinary
unqueued jobs. An instance that never calls `.AddQueues(...)` at all still serves `"default"`
(today's behavior, unchanged). A job on a queue no live instance serves simply sits waiting —
same as a job whose target device is offline — until an instance that serves it comes online; the
dashboard's Jobs table shows each job's queue, and the Servers tab shows which queues each
instance serves. See [docs/architecture.md#job-queues](docs/architecture.md#job-queues) for
details.

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

A recurring job can be paused, resumed, or skip its single next occurrence — from the dashboard's Recurring jobs tab, or directly via `POST /axon/recurring-jobs/{recurringJobId}/pause`, `.../resume`, `.../skip-next`. Pausing leaves the existing `NextRunAt` untouched (the poll loop just skips it entirely while paused), so resuming picks the schedule back up rather than recomputing it; skipping advances `NextRunAt` to the occurrence after the one currently scheduled and leaves everything else alone. These are dashboard/API-only (operator actions) — there's no `IAxonClient` equivalent, since it's normally an operator, not the job's own device, deciding to pause a schedule.

### 9. Open the dashboard

Navigate to `/axon` on whichever host runs `Axon.Server` and sign in with a configured username/password.

<img src="docs/images/dashboard-login.png" alt="Axon dashboard sign-in screen" width="480" />

The dashboard is organized into four tabs:
- **Jobs** — job history, state, retry/delete.

  <img src="docs/images/dashboard-jobs.png" alt="Axon dashboard Jobs tab" width="720" />

- **Recurring jobs** — schedules, last/next run, trigger/remove.

  <img src="docs/images/dashboard-recurring-jobs.png" alt="Axon dashboard Recurring jobs tab" width="720" />

- **Servers** — active `Axon.Server` instances, each heartbeating every 15s and shown offline once its heartbeat is more than 45s old. With the default in-memory store an instance only ever sees itself; use `Axon.Store.SqlServer`, `Axon.Store.Postgres`, `Axon.Store.MySql`, or `Axon.Store.MongoDb` to see every instance behind a multi-instance deployment.

  <img src="docs/images/dashboard-servers.png" alt="Axon dashboard Servers tab" width="720" />

- **Clients** — connected devices, shown Idle or Processing depending on whether a job is currently dispatched to them. A SignalR connection is pinned to whichever instance accepted it, so with the default in-memory registry this list is instance-local (`Axon.Server.Redis` makes dispatch/push still reach the right client, but each instance only lists the clients connected to itself); use `Axon.Store.SqlServer`, `Axon.Store.Postgres`, `Axon.Store.MySql`, or `Axon.Store.MongoDb` to see every connected client across the whole fleet, regardless of which instance it's connected to.

  <img src="docs/images/dashboard-clients.png" alt="Axon dashboard Clients tab" width="720" />

`ReadOnly` users see the same data but without retry/delete/trigger controls.

The dashboard updates in real time over a dedicated SignalR connection (`/axon/hub`, gated by the same dashboard auth) rather than polling: every job, recurring-job, server, or client change is pushed to open dashboard tabs the moment it happens. If that connection is ever unavailable (network blip, outbound access to the SignalR JS CDN blocked, etc.) the dashboard automatically falls back to polling every 5s and keeps retrying the push connection in the background.

See [examples/](examples) for complete, runnable ASP.NET Core projects ([Axon.Example.Server](examples/Axon.Example.Server), [Axon.Example.Client](examples/Axon.Example.Client)) wired up end-to-end, and [deploy/](deploy) for a docker compose stack that runs 3 `Axon.Server` instances behind a load balancer, sharing SQL Server and a Redis backplane, with a separate client - a real horizontally-scaled deployment running with one command.

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
- `tests/Axon.Tests.Integration` — integration tests for `Axon.Store.SqlServer`, `Axon.Store.Postgres`, `Axon.Store.MySql`, and `Axon.Store.MongoDb` (real instances spun up via [Testcontainers](https://dotnet.testcontainers.org/), requires Docker) and `Axon.Store.SQLite` (a real database file, no container needed). These cover the same transactional-write and atomic-claim guarantees described above, including a 20-way concurrent `TryClaimJob` race to verify exactly one caller ever wins.

```bash
dotnet test
```

runs both projects; Docker must be running locally for the integration tests to start their SQL Server container.

## Versioning

Packages are released from git tags (`vX.Y.Z`) via [.github/workflows/release.yml](.github/workflows/release.yml), which builds, tests, and pushes every package in this repo to NuGet.org with that same version — all packages here are versioned and released together, not independently.

Once tagged `1.0.0`, the intent is standard [SemVer](https://semver.org/): a **patch** release is a bug fix with no API change; a **minor** release adds functionality (new methods, new optional parameters, new packages) without breaking existing callers; a **major** release is reserved for a breaking change to public API surface — a removed/renamed public type or member, a changed method signature, or a behavioral change callers could reasonably have depended on. `IDeviceConnectionRegistry` going from a synchronous to an async interface (see git history) is the kind of change that would require a major bump once this commitment is in effect.

**This project is pre-1.0** (see Status below) and has not yet made that commitment — the `0.0.x`/`0.x.y` versions released so far may include breaking changes in a minor or patch bump. Treat every pre-1.0 upgrade as a potential breaking change until the 1.0.0 release, after which the policy above applies.

See [CHANGELOG.md](CHANGELOG.md) for what's changed.

## Status

This is a proof of concept. See the [repository](https://github.com/housgh/Axon) for source, issues, and usage examples.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md).
