# Axon Architecture

## Job dispatch and completion

```mermaid
sequenceDiagram
    participant API as Controller (EnqueueAsync)
    participant Store as IAxonJobStore (SQL/in-memory)
    participant Processor as AxonJobProcessor (BackgroundService)
    participant Hub as AxonHub (SignalR)
    participant Client as Device (AxonClient)

    API->>Store: AddJob (State=Enqueued)
    Note over Processor: polls every 5s

    Processor->>Store: MarkProcessing(jobId, deadline)
    Note over Processor,Store: State set to Processing BEFORE dispatch,<br/>so a fast client ack can never race ahead of it
    Processor->>Hub: SendCoreAsync("Invoke", job)
    Hub->>Client: Invoke(jobId, job)
    Client->>Client: execute method body

    alt success
        Client->>Hub: OnSuccess(jobId)
        Hub->>Store: UpdateState(Succeeded)
    else failure
        Client->>Hub: OnFail(jobId, error)
        Hub->>Store: RecordFailure + retry-or-fail
    end
```

## Job state machine

```mermaid
stateDiagram-v2
    [*] --> Enqueued
    Enqueued --> Processing: dispatched to device
    Scheduled --> Processing: due, dispatched
    [*] --> Scheduled: enqueued with delay
    Processing --> Succeeded: OnSuccess
    Processing --> Scheduled: OnFail / orphaned, attempts < max (retry backoff)
    Processing --> Failed: OnFail / orphaned, attempts >= max
    Succeeded --> [*]
    Failed --> [*]
```

## Orphan reclaim (crash / disconnect recovery)

Two independent paths reclaim a job stuck in `Processing`, so recovery doesn't depend on a clean disconnect:

```mermaid
flowchart TD
    subgraph "Path 1: disconnect-triggered (fast)"
        A[Device disconnects] --> B[AxonHub.OnDisconnectedAsync]
        B --> C[jobStore.GetProcessingJobsForDevice]
        C --> D[jobService.ReclaimOrphanedAsync per job]
    end

    subgraph "Path 2: deadline sweep (crash-safe)"
        E[AxonJobProcessor poll loop, every 5s] --> F[jobStore.GetOrphanedProcessingJobs]
        F -->|ProcessingDeadline elapsed| D
    end

    D --> G[RecordFailure: 'orphaned' note]
    G --> H{attempts < MaxAttempts?}
    H -->|yes| I[State=Scheduled, retry after backoff]
    H -->|no| J[State=Failed]
```

Path 2 is what makes the reclaim survive a server crash: `ProcessingDeadline` is persisted in SQL alongside the job, so a freshly restarted server rediscovers stuck jobs from durable state alone — it doesn't need to have witnessed the original disconnect.

## Server restart recovery

```mermaid
flowchart LR
    subgraph "Before crash"
        J1[Job: Processing] -.persisted.-> DB[(SQL Server)]
        R1[RecurringJob: hourly-hello] -.persisted.-> DB
    end

    DB -.survives crash.-> DB2[(SQL Server)]

    subgraph "After restart"
        DB2 --> J2[Job still Processing,<br/>deadline sweep reclaims it]
        DB2 --> R2[Recurring job reloaded,<br/>re-registered to new device connection]
    end
```

## SQL store transactional writes

Every paired state-change write (job state + history row) runs inside a single SQL transaction, so a failure between the two statements can't leave the history table out of sync with the job's actual state:

```mermaid
flowchart TD
    A[BeginTransaction] --> B[UPDATE Jobs SET State = ...]
    B --> C[INSERT INTO JobHistory ...]
    C --> D{Both succeeded?}
    D -->|yes| E[Commit]
    D -->|no| F[Rollback: no orphaned history row]
```
