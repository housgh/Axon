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
