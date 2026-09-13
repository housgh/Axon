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
        ScheduledFor  BIGINT         NULL,
        State         INT            NOT NULL,
        Attempts      INT            NOT NULL DEFAULT 0,
        MaxAttempts   INT            NOT NULL DEFAULT 3,
        IsDeleted     BIT            NOT NULL DEFAULT 0
    );

    CREATE INDEX IX_Jobs_State_ScheduledFor ON Jobs (State, ScheduledFor) WHERE IsDeleted = 0;
    CREATE INDEX IX_Jobs_DeviceName ON Jobs (DeviceName) WHERE IsDeleted = 0;
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
