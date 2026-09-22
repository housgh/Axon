-- Axon.Store.Postgres schema
-- Run this once against the target database before using AddAxonPostgresStore().

CREATE TABLE IF NOT EXISTS "Jobs"
(
    "JobId"         VARCHAR(64)   NOT NULL PRIMARY KEY,
    "DeviceName"    VARCHAR(256)  NOT NULL,
    "Arguments"     TEXT          NOT NULL,
    "MethodName"    VARCHAR(256)  NOT NULL,
    "Assembly"      VARCHAR(512)  NOT NULL,
    "DeclaringType" VARCHAR(512)  NOT NULL,
    "ScheduledFor"           BIGINT  NULL,
    "State"                  INT     NOT NULL,
    "Attempts"               INT     NOT NULL DEFAULT 0,
    "MaxAttempts"            INT     NOT NULL DEFAULT 3,
    "ProcessingDeadline"     BIGINT  NULL,
    "EnqueuedAt"             BIGINT  NOT NULL DEFAULT 0,
    "RetryPolicy"            TEXT    NULL,
    "ConcurrencyKey"         VARCHAR(256) NULL,
    "MaxConcurrent"          INT     NULL,
    "ParentJobId"            VARCHAR(64) NULL,
    "ContinueOnParentFailure" BOOLEAN NOT NULL DEFAULT FALSE,
    "IsDeleted"              BOOLEAN NOT NULL DEFAULT FALSE,
    "Priority"               INT     NOT NULL DEFAULT 0
);

CREATE INDEX IF NOT EXISTS "IX_Jobs_State_ScheduledFor" ON "Jobs" ("State", "ScheduledFor") WHERE "IsDeleted" = FALSE;
CREATE INDEX IF NOT EXISTS "IX_Jobs_DeviceName" ON "Jobs" ("DeviceName") WHERE "IsDeleted" = FALSE;
CREATE INDEX IF NOT EXISTS "IX_Jobs_Processing_Deadline" ON "Jobs" ("State", "ProcessingDeadline") WHERE "IsDeleted" = FALSE;
CREATE INDEX IF NOT EXISTS "IX_Jobs_ConcurrencyKey_State" ON "Jobs" ("ConcurrencyKey", "State") WHERE "IsDeleted" = FALSE AND "ConcurrencyKey" IS NOT NULL;
CREATE INDEX IF NOT EXISTS "IX_Jobs_ParentJobId" ON "Jobs" ("ParentJobId") WHERE "IsDeleted" = FALSE AND "ParentJobId" IS NOT NULL;

CREATE TABLE IF NOT EXISTS "JobHistory"
(
    "Id"        BIGSERIAL     NOT NULL PRIMARY KEY,
    "JobId"     VARCHAR(64)   NOT NULL,
    "State"     INT           NOT NULL,
    "Timestamp" BIGINT        NOT NULL,
    "Note"      VARCHAR(1024) NULL
);

CREATE INDEX IF NOT EXISTS "IX_JobHistory_JobId_Timestamp" ON "JobHistory" ("JobId", "Timestamp");

CREATE TABLE IF NOT EXISTS "RecurringJobs"
(
    "RecurringJobId" VARCHAR(64)   NOT NULL PRIMARY KEY,
    "DeviceName"     VARCHAR(256)  NOT NULL,
    "Arguments"      TEXT          NOT NULL,
    "MethodName"     VARCHAR(256)  NOT NULL,
    "Assembly"       VARCHAR(512)  NOT NULL,
    "DeclaringType"  VARCHAR(512)  NOT NULL,
    "CronExpression" VARCHAR(64)   NOT NULL,
    "NextRunAt"      BIGINT        NOT NULL,
    "LastRunAt"      BIGINT        NULL,
    "IsPaused"       BOOLEAN       NOT NULL DEFAULT FALSE
);

CREATE INDEX IF NOT EXISTS "IX_RecurringJobs_NextRunAt" ON "RecurringJobs" ("NextRunAt");

CREATE TABLE IF NOT EXISTS "ServerInstances"
(
    "InstanceId"   VARCHAR(64)   NOT NULL PRIMARY KEY,
    "MachineName"  VARCHAR(256)  NOT NULL,
    "StartedAt"    BIGINT        NOT NULL,
    "LastSeenAt"   BIGINT        NOT NULL,
    "ServedQueues" VARCHAR(1024) NOT NULL DEFAULT 'default'
);

CREATE INDEX IF NOT EXISTS "IX_ServerInstances_LastSeenAt" ON "ServerInstances" ("LastSeenAt");

-- Published device connections, so the dashboard's Clients tab reflects the whole fleet rather
-- than only whichever instance happens to answer a given request (a SignalR connection is
-- pinned to whichever instance accepted it). InstanceId identifies the owning instance so a row
-- can be recognized as stale (see AxonPostgresDeviceConnectionStore.GetAll) if that instance's
-- ServerInstances heartbeat has gone quiet.
CREATE TABLE IF NOT EXISTS "DeviceConnections"
(
    "DeviceName"   VARCHAR(256) NOT NULL PRIMARY KEY,
    "ConnectionId" VARCHAR(64)  NOT NULL,
    "InstanceId"   VARCHAR(64)  NOT NULL,
    "ConnectedAt"  BIGINT       NOT NULL
);

CREATE INDEX IF NOT EXISTS "IX_DeviceConnections_ConnectionId" ON "DeviceConnections" ("ConnectionId");
CREATE INDEX IF NOT EXISTS "IX_DeviceConnections_InstanceId" ON "DeviceConnections" ("InstanceId");
