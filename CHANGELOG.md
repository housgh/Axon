# Changelog

All notable changes to the Axon packages are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versioning follows the policy in
[README.md#versioning](README.md#versioning) once this project reaches `1.0.0` — every package
in this repo is released together under one version, from a `vX.Y.Z` git tag.

## [Unreleased]

### Added
- `Axon.Store.Postgres`: PostgreSQL-backed job storage for `Axon.Server`, mirroring
  `Axon.Store.SqlServer`'s feature set (same four store interfaces, same `Schema.sql`-first
  onboarding, same multi-instance dispatch safety guarantee - see
  docs/architecture.md#multi-instance-dispatch-safety for how its `ConcurrencyKey` locking
  differs from SQL Server's).
- `Axon.Store.MySql`: MySQL-backed job storage for `Axon.Server`, same feature set as
  `Axon.Store.SqlServer`/`Axon.Store.Postgres` (see
  docs/architecture.md#multi-instance-dispatch-safety for its InnoDB locking approach).
- `Axon.Store.SQLite`: SQLite-backed job storage for `Axon.Server`, for single-instance
  deployments and local development. Unlike the other three store packages, it does not support
  multi-instance dispatch (see docs/architecture.md#multi-instance-dispatch-safety) - SQLite's
  single-writer model serializes all writes process-wide rather than per job/key.
- `Axon.Store.MongoDb`: MongoDB-backed job storage for `Axon.Server`, same four store interfaces
  and multi-instance dispatch safety guarantee as the SQL backends, but schemaless (no
  `Schema.sql` - see `Axon.MongoDb.Indexes.EnsureIndexesAsync` for the optional index-creation
  step) and requiring a replica set for `TryClaimJob`'s transaction-scoped
  `ConcurrencyKey`/`MaxConcurrent` check to work at all (see
  docs/architecture.md#multi-instance-dispatch-safety for why, and for how its `ConcurrencyLocks`
  collection makes MongoDB's per-document write-conflict detection actually catch a race that
  would otherwise go undetected).
- `Priority` on jobs (`JobPriority`: `Low`, `Medium` - the default, `High`, `Critical`), settable
  via `AxonEnqueueOptions.Priority`. Every backend's `GetJobs` now orders by an effective
  dispatch score (`COALESCE(ScheduledFor, EnqueuedAt) - Boost[Priority]`, see
  `Axon.Core.Enums.JobPriorityBoost`) instead of raw `ScheduledFor` - a bounded virtual-age bonus
  per priority level, not a hard tier: a `High` job only jumps ahead of an already-waiting `Low`
  job by as much as the boost gap between them (15 min), so a sufficiently old `Low` job still
  wins. See docs/architecture.md#job-priority for the full formula and the worked example.

### Changed
- Every job store now orders due jobs (and the dashboard's `/axon/jobs` listing) by
  `COALESCE(ScheduledFor, EnqueuedAt)` instead of `ScheduledFor` alone. Previously, an immediate
  ("run now") job's `ScheduledFor = NULL` sorted first in every backend's ascending order, so an
  immediate job always dispatched before any scheduled-for-later job regardless of age - this was
  an incidental side effect of the old sort, never a documented guarantee. Immediate jobs now
  compete with scheduled jobs on the same timeline (adjusted by `Priority`) instead of
  automatically jumping the whole queue. See docs/architecture.md#job-priority.

## [0.1.2] - 2026-09-20

The first version actually published to NuGet.org. `v0.1.0` and `v0.1.1` were tagged but never
published — the `Axon.*` NuGet ID prefix turned out to be reserved by another publisher, which is
why every package ID below is `GoAxon.*` rather than `Axon.*` (see the Changed entry).

### Added
- `AxonSqlServerDeviceConnectionStore`, making the dashboard's Clients tab fleet-wide (previously
  instance-local) when `Axon.Store.SqlServer` is configured, matching how the Servers tab already
  worked.
- `AxonStoreException` / `AxonSchemaNotProvisionedException`: SQL Server connectivity and
  missing-schema failures now surface as actionable exceptions instead of raw ADO.NET exceptions.
- A readiness health check for the Redis backplane (`AddRedisBackplane`), alongside the existing
  job-store health check.
- `CancellationToken` support on every `IAxonClient` public async method.
- `JobActivator` is now DI-aware: `AddAxonClient` registers a `ServiceProviderJobActivator` by
  default, so dispatched job classes can take constructor-injected dependencies.
- Pagination (`skip`/`take`) and `GetById` on `IAxonRecurringJobStore`, matching `IAxonJobStore`.
- Pause, resume, and skip-next-occurrence for recurring jobs — dashboard UI and API
  (`POST /axon/recurring-jobs/{id}/pause|resume|skip-next`).
- `AxonEnqueueOptions`, replacing the growing positional-parameter list on
  `EnqueueAsync`/`ScheduleAsync`/`ContinueWithAsync`.
- A `deploy/.env.example` template so the multi-instance demo's SQL password isn't hardcoded into
  `docker-compose.yml`.

### Changed
- `IAxonClient.EnqueueAsync`, `ScheduleAsync`, and `ContinueWithAsync` now take a single optional
  `AxonEnqueueOptions` parameter instead of separate `retryPolicy`/`concurrencyKey`/
  `maxConcurrent`/`cancellationToken` parameters. **Breaking** once this ships under the SemVer
  policy — pre-1.0, it lands as part of the initial `1.0.0` API shape instead.
- Several implementation types (`InMemoryAxonJobStore`, `PasswordHasher`, and others) that were
  public only by omission are now `internal`.
- NuGet package IDs are now prefixed `GoAxon.*` instead of `Axon.*` (e.g. `GoAxon.Core` instead of
  `Axon.Core`) — the `Axon.*` ID prefix turned out to be reserved by another publisher on
  NuGet.org, discovered when the first publish attempt failed. Only the NuGet-facing package ID
  changed; namespaces, project names, and everything else in this repo are still `Axon.*`.

### Fixed
- `Axon.Client`'s SignalR connection and server-side device registration now start eagerly at
  host startup instead of lazily on first `EnqueueAsync` call, so a connected device shows up in
  the dashboard's Clients tab immediately rather than only after its first job.
- The dashboard's `/axon/jobs` and `/axon/recurring-jobs` endpoints required `skip`/`take` query
  parameters with no defaults, returning `400` for any caller that didn't pass them explicitly.
- A SQL Server connectivity failure could surface under a different `SqlException.Number` on
  Linux than on Windows/macOS for the same underlying error, causing `SqlExceptionTranslator` to
  misclassify it on Linux CI runners.
