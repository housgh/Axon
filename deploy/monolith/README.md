# Monolith demo

A docker compose stack for Axon's [monolith (in-process) mode](../../README.md#monolith-in-process-mode):
**3 instances of one app** (`Axon.Example.Monolith`, which enqueues *and* runs its own jobs via
`AddInProcessClient()`) behind nginx, sharing **SQL Server** as the job store.

Compared to the [multi-instance microservice demo](../multi-instance/README.md), there's no separate client
service, no Redis backplane, and no `/hubs/axon` WebSocket. Jobs never cross the network.

```
                    ┌──────────┐
   browser/curl ──▶ │  nginx   │ app endpoints: round-robin
                    │          │ /axon (dashboard/API): sticky per client IP
                    └────┬─────┘
              ┌──────────┼──────────┐
              ▼          ▼          ▼
            app1       app2       app3     each: web app + scheduler + job runner
              │          │          │
              └──────────┼──────────┘
                         ▼
                     SQL Server
                    (job store)
```

## Run it

```bash
cd deploy/monolith
cp .env.example .env   # first time only
docker compose up -d --build
```

The first boot takes a minute or two while SQL Server starts and each instance bootstraps the
database and schema. Then:

- **App** (through nginx, round-robin): http://localhost:8180/ shows which instance answered.
  - `/enqueue`: one job.
  - `/enqueue-many/30`: a burst of instant jobs.
  - `/enqueue-slow/24`: 5-second jobs that spread across instances.
  - `/continuation`, `/schedule`, `/recurring`, `/dependency`: the other `IAxonClient` features.
- **Dashboard**: http://localhost:8180/axon. Log in as `admin` with `DASHBOARD_ADMIN_PASSWORD` from `.env`. Each instance is also reachable directly on 8181/8182/8183.
- **SQL Server**: `localhost:1434`, user `sa`, password `SQL_SA_PASSWORD` from `.env`.

Ports are offset from the microservice demo (8180 vs 8080, 1434 vs 1433), so both stacks can run
at the same time.

Tear down with `docker compose down`. Add `-v` to also drop the SQL Server and Data Protection
key volumes.

## What this proves

- **Any instance runs any job, exactly once.** `/enqueue-slow/24` lands on one instance (round-robin
  picks one per request), but the jobs run on all three. Each instance runs at most
  `Axon__MaxConcurrentJobs` (4 here) at a time, so the rest stay `Enqueued` until another
  instance's poll claims them. Check with:
  ```bash
  for s in app1 app2 app3; do echo "$s: $(docker compose logs $s | grep -c 'Slow job #')"; done
  ```
  Every job runs exactly once regardless of which instance claimed it, because the atomic SQL
  claim (see [docs/architecture.md#multi-instance-dispatch-safety](../../docs/architecture.md#multi-instance-dispatch-safety))
  does the coordination. No backplane is involved.
- **Instant jobs don't spread, and that's fine.** `/enqueue-many/30` usually all runs on one
  instance. Whichever instance polls first sees every due job and has free slots for all of them.
  Lower `MaxConcurrentJobs` if you want work spread more evenly.
- **Graceful shutdown.** Enqueue `/enqueue-slow/12`, wait a few seconds, then
  `docker compose stop app2`. app2 stops claiming new jobs, finishes the ones it's running, and
  only then exits, so every job still ends `Succeeded`. A job still running when the host's
  shutdown timeout expires (or when the container is killed) stays `Processing`. It's retried
  after its processing deadline via [orphan reclaim](../../docs/architecture.md#orphan-reclaim-crash--disconnect-recovery).

## Things to know

- **Dashboard live updates are per instance.** The dashboard's push connection (`/axon/hub`, the
  only WebSocket in this stack) only receives change notifications from the instance it's connected
  to. With no Redis backplane, a job that runs on another instance won't refresh your open dashboard
  until something on *your* instance changes or you reload. nginx pins `/axon` per client IP to
  keep that view consistent. If you want instant fleet-wide updates, add `Axon.Server.Redis`'s
  `.AddRedisBackplane(...)`, which in monolith mode only affects the dashboard.
- **Shared Data Protection keys.** All instances mount the same `/keys` volume, so a dashboard
  login cookie issued by one instance is accepted by the others. This is the same lesson as the
  [microservice demo](../multi-instance/README.md#a-real-deployment-lesson-this-setup-surfaces).
- **Not for production**, for the same reasons as the [microservice demo](../multi-instance/README.md#not-for-production):
  plaintext secrets in `.env` and no TLS.

## Files

- `docker-compose.yml`: the stack.
- `.env.example`: copy to `.env` to supply `SQL_SA_PASSWORD` and `DASHBOARD_ADMIN_PASSWORD` (`.env` is gitignored).
- `nginx.conf`: round-robin for app traffic, sticky per IP for `/axon`, and WebSocket upgrade for the dashboard hub only.
- `../../examples/Axon.Example.Monolith/`: the app. SQL Server, dashboard auth, shared keys and `MaxConcurrentJobs` are all opt-in through config, so a plain `dotnet run` still gives an in-memory single instance.
