# Axon

A lightweight, Hangfire-style background job scheduler for .NET microservices, with one key difference: **the job scheduler and the job's implementation don't have to live in the same codebase.**

Axon.Server acts purely as a scheduler/dispatcher — it never executes your job code. Each microservice runs an Axon.Client that both enqueues jobs (to run on itself, later) and executes them when the server dispatches them back over a persistent SignalR connection. This makes Axon a good fit for RPC-style architectures where Hangfire's "storage + workers share one deployable" model doesn't apply.

## Packages

| Package | Description |
|---|---|
| `Axon.Core` | Shared models and enums used by both client and server. |
| `Axon.Client` | Enqueue jobs, schedule recurring jobs, and execute dispatched jobs inside your service. |
| `Axon.Server` | The scheduler/dispatcher: SignalR hub, job store, background processors, and an admin dashboard. |
| `Axon.Store.SqlServer` | SQL Server-backed persistence for `Axon.Server` (in-memory storage is used by default). |

## Status

This is a proof of concept. See the [repository](https://github.com/housgh/Axon) for source, issues, and usage examples.
