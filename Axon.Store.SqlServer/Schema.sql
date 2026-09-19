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
