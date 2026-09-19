-- Axon.Store.SqlServer schema
-- Run this once against the target database before using AddAxonSqlServerStore().

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Jobs')
BEGIN
    CREATE TABLE Jobs
    (
        JobId         NVARCHAR(64)   NOT NULL PRIMARY KEY,
        DeviceName    NVARCHAR(256)  NOT NULL,
        Arguments     NVARCHAR(MAX)  NOT NULL,
        MethodName    NVARCHAR(256)  NOT NULL,
        Assembly      NVARCHAR(512)  NOT NULL,
        DeclaringType NVARCHAR(512)  NOT NULL,
        ScheduledFor      BIGINT     NULL,
        State             INT        NOT NULL,
        Attempts          INT        NOT NULL DEFAULT 0,
        MaxAttempts       INT        NOT NULL DEFAULT 3,
        ProcessingDeadline BIGINT    NULL,
        EnqueuedAt        BIGINT     NOT NULL DEFAULT 0,
        RetryPolicy       NVARCHAR(MAX) NULL,
        ConcurrencyKey    NVARCHAR(256) NULL,
        MaxConcurrent     INT        NULL,
        ParentJobId       NVARCHAR(64) NULL,
        ContinueOnParentFailure BIT  NOT NULL DEFAULT 0,
        IsDeleted         BIT        NOT NULL DEFAULT 0
    );

    CREATE INDEX IX_Jobs_State_ScheduledFor ON Jobs (State, ScheduledFor) WHERE IsDeleted = 0;
    CREATE INDEX IX_Jobs_DeviceName ON Jobs (DeviceName) WHERE IsDeleted = 0;
END

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Jobs') AND name = 'ProcessingDeadline')
BEGIN
    ALTER TABLE Jobs ADD ProcessingDeadline BIGINT NULL;
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('Jobs') AND name = 'IX_Jobs_Processing_Deadline')
BEGIN
    CREATE INDEX IX_Jobs_Processing_Deadline ON Jobs (State, ProcessingDeadline) WHERE IsDeleted = 0;
END

-- Used only for the axon.jobs.dispatch_latency metric; existing rows default to 0, which
-- AxonJobProcessor treats as "no data" and skips recording a latency sample for.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Jobs') AND name = 'EnqueuedAt')
BEGIN
    ALTER TABLE Jobs ADD EnqueuedAt BIGINT NOT NULL DEFAULT 0;
END

-- Per-job retry override (max attempts + backoff schedule), JSON-serialized. NULL means "use
-- Axon.Server's built-in default backoff".
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Jobs') AND name = 'RetryPolicy')
BEGIN
    ALTER TABLE Jobs ADD RetryPolicy NVARCHAR(MAX) NULL;
END

-- Per-job-type concurrency limit: at most MaxConcurrent jobs sharing the same ConcurrencyKey may
-- be Processing at once (enforced atomically inside TryClaimJob). NULL ConcurrencyKey means no
-- limit is enforced for that job.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Jobs') AND name = 'ConcurrencyKey')
BEGIN
    ALTER TABLE Jobs ADD ConcurrencyKey NVARCHAR(256) NULL;
END

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Jobs') AND name = 'MaxConcurrent')
BEGIN
    ALTER TABLE Jobs ADD MaxConcurrent INT NULL;
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('Jobs') AND name = 'IX_Jobs_ConcurrencyKey_State')
BEGIN
    CREATE INDEX IX_Jobs_ConcurrencyKey_State ON Jobs (ConcurrencyKey, State) WHERE IsDeleted = 0 AND ConcurrencyKey IS NOT NULL;
END

-- Continuations: ParentJobId identifies the job this one waits on (State = AwaitingParent until
-- promoted); ContinueOnParentFailure controls whether the continuation still runs if the parent
-- ends in Failed (default: no, it goes to Skipped instead).
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Jobs') AND name = 'ParentJobId')
BEGIN
    ALTER TABLE Jobs ADD ParentJobId NVARCHAR(64) NULL;
END

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Jobs') AND name = 'ContinueOnParentFailure')
BEGIN
    ALTER TABLE Jobs ADD ContinueOnParentFailure BIT NOT NULL DEFAULT 0;
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('Jobs') AND name = 'IX_Jobs_ParentJobId')
BEGIN
    CREATE INDEX IX_Jobs_ParentJobId ON Jobs (ParentJobId) WHERE IsDeleted = 0 AND ParentJobId IS NOT NULL;
END

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'JobHistory')
BEGIN
    CREATE TABLE JobHistory
    (
        Id        BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        JobId     NVARCHAR(64)  NOT NULL,
        State     INT           NOT NULL,
        Timestamp BIGINT        NOT NULL,
        Note      NVARCHAR(1024) NULL
    );

    CREATE INDEX IX_JobHistory_JobId_Timestamp ON JobHistory (JobId, Timestamp);
END

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'RecurringJobs')
BEGIN
    CREATE TABLE RecurringJobs
    (
        RecurringJobId NVARCHAR(64)   NOT NULL PRIMARY KEY,
        DeviceName     NVARCHAR(256)  NOT NULL,
        Arguments      NVARCHAR(MAX)  NOT NULL,
        MethodName     NVARCHAR(256)  NOT NULL,
        Assembly       NVARCHAR(512)  NOT NULL,
        DeclaringType  NVARCHAR(512)  NOT NULL,
        CronExpression NVARCHAR(64)   NOT NULL,
        NextRunAt      BIGINT         NOT NULL,
        LastRunAt      BIGINT         NULL
    );

    CREATE INDEX IX_RecurringJobs_NextRunAt ON RecurringJobs (NextRunAt);
END

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ServerInstances')
BEGIN
    CREATE TABLE ServerInstances
    (
        InstanceId  NVARCHAR(64)  NOT NULL PRIMARY KEY,
        MachineName NVARCHAR(256) NOT NULL,
        StartedAt   BIGINT        NOT NULL,
        LastSeenAt  BIGINT        NOT NULL
    );

    CREATE INDEX IX_ServerInstances_LastSeenAt ON ServerInstances (LastSeenAt);
END
