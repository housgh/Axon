# Multi-instance demo

A docker compose stack demonstrating Axon running as a real horizontally-scaled deployment:
**3 `Axon.Server` instances** behind an nginx load balancer, sharing **SQL Server** (job store)
and **Redis** (SignalR backplane), with **1 `Axon.Client`** connecting through the load balancer.

```
                    ┌──────────┐
   client ────────▶ │  nginx   │ /hubs/axon: round-robin (job dispatch)
   browser ───────▶ │          │ everything else: sticky per client IP (dashboard/API)
                    └────┬─────┘
              ┌──────────┼──────────┐
              ▼          ▼          ▼
          server1     server2     server3
              │          │          │
              └────┬─────┴─────┬────┘
                    ▼           ▼
              SQL Server      Redis
             (job store)   (backplane)
```

## Run it

```bash
cd deploy
docker compose up -d --build
```

First boot takes a minute or two (SQL Server startup + each server's schema/database bootstrap).
Then:

- **Dashboard**: http://localhost:8080/axon (through the load balancer) — login `admin` / `P@ssw0rd`. Also reachable directly per-instance on 8081/8082/8083.
- **Client**: http://localhost:8090/enqueue — enqueues one job; http://localhost:8090/enqueue-many/10 for a batch; http://localhost:8090/recurring to register a once-a-minute recurring job.
- **SQL Server**: `localhost:1433`, sa / `Ax0n!DemoPassw0rd`.
- **Redis**: `localhost:6379`.

Tear down with `docker compose down` (add `-v` to also drop the SQL Server and Data Protection key volumes for a clean-slate rerun).

## What this proves

- **Dispatch correctness across instances**: enqueue a batch (`/enqueue-many/50`) and watch the client's logs — every job runs exactly once, regardless of which of the 3 servers' `AxonJobProcessor` won the atomic claim race against the shared SQL Server job store (see [docs/architecture.md#multi-instance-dispatch-safety](../docs/architecture.md#multi-instance-dispatch-safety)).
- **Backplane routing**: the client's single WebSocket connection (`/hubs/axon`, round-robin in `nginx.conf` - deliberately *not* sticky) lands on exactly one server instance's process. A job claimed and dispatched by a *different* instance still reaches the client, because `Axon.Server.Redis`'s backplane fans the SignalR call out across all 3 processes. This is the thing this demo exists to prove, so that path is left un-sticky on purpose. (`/axon/clients` will list the connected device on all 3 instances regardless - see below - so it doesn't reveal which one owns the actual WebSocket connection; the backplane fan-out is what you're really watching.)

## Why the Clients tab is fleet-wide (and dispatch isn't sticky)

A SignalR connection is pinned to whichever server instance accepted it, so a naive in-memory
connection registry would only ever know about the devices connected *directly* to that one
process - browsing the dashboard through the load balancer would then make the Clients list
(and the Servers tab's "freshest" heartbeat) appear to randomly flicker between instances, even
though nothing is actually wrong. `Axon.Store.SqlServer` fixes this at the source rather than
papering over it with routing tricks: `AddAxonSqlServerStore` swaps in
`AxonSqlServerDeviceConnectionStore`, which publishes every `Register`/`Unregister` to a shared
`DeviceConnections` table (mirroring how `AxonSqlServerInstanceStore` already made the Servers
tab fleet-wide). Querying `/axon/clients` on any of the 3 instances - directly on 8081/8082/8083,
or through nginx - now returns the same result, with no dependency on sticky sessions. A stale
row left behind by an instance that died without a clean SignalR disconnect is filtered out by
joining against that instance's `ServerInstances` heartbeat, the same staleness check the
Servers tab already used for `IsOnline`.

`nginx.conf` still splits traffic into two upstreams - `/hubs/axon` (job dispatch) stays plain
round-robin so the demo continues to prove the Redis backplane, while everything else is pinned
per client IP via `ip_hash` - but that stickiness is no longer what makes the Clients tab work.
It remains in place mainly because sticky dashboard routing is cheap and harmless, and it still
smooths over one unrelated wrinkle: see the Data Protection cookie note below.

## A real deployment lesson this setup surfaces

Getting this working end-to-end surfaced something not obvious from a single-instance dev setup:
**the dashboard's login cookie doesn't validate across instances by default.** ASP.NET Core signs
and encrypts auth cookies with its Data Protection key ring, which is per-process and ephemeral
unless configured otherwise - so a cookie issued by `server1` fails with `401` against `server2`
or `server3`, even though `DashboardUsers` and the cookie scheme are identical everywhere. This
has nothing to do with Axon's own auth logic; it's a general ASP.NET Core behind-a-load-balancer
concern that any cookie-authenticated app hits.

The fix here (`Program.cs` + the `axon-dataprotection-keys` volume in `docker-compose.yml`):
point every instance's Data Protection key ring at the same shared location via
`PersistKeysToFileSystem`. In this demo that's a shared Docker volume; in a real deployment it'd
more likely be a shared network file share or a dedicated key-ring store (Azure Blob, Redis, etc.
via the corresponding Data Protection extension packages). Verified by logging in against one
instance directly and confirming the same cookie is then accepted by the other two.

## Files

- `docker-compose.yml` — the stack.
- `nginx.conf` — split-upstream proxy (round-robin for job dispatch, sticky-per-IP for the dashboard/API) with WebSocket upgrade support (required for SignalR).
- `../examples/Axon.Example.Server/` — minimal server-only host (SQL storage + Redis backplane + dashboard auth + job cleanup, all opted into).
- `../examples/Axon.Example.Client/` — minimal client-only host with a few HTTP endpoints to trigger jobs.
