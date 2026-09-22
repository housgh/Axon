-- Axon.Store.SQLite schema
-- Run this once against the target database file before using AddAxonSQLiteStore().

CREATE TABLE IF NOT EXISTS "Jobs"
(
    "JobId"         TEXT    NOT NULL PRIMARY KEY,
    "DeviceName"    TEXT    NOT NULL,
    "Arguments"     TEXT    NOT NULL,
    "MethodName"    TEXT    NOT NULL,
    "Assembly"      TEXT    NOT NULL,
    "DeclaringType" TEXT    NOT NULL,
    "ScheduledFor"            INTEGER NULL,
    "State"                   INTEGER NOT NULL,
    "Attempts"                INTEGER NOT NULL DEFAULT 0,
    "MaxAttempts"             INTEGER NOT NULL DEFAULT 3,
    "ProcessingDeadline"      INTEGER NULL,
    "EnqueuedAt"              INTEGER NOT NULL DEFAULT 0,
    "RetryPolicy"             TEXT    NULL,
    "ConcurrencyKey"          TEXT    NULL,
    "MaxConcurrent"           INTEGER NULL,
    "ParentJobId"             TEXT    NULL,
    "ContinueOnParentFailure" INTEGER NOT NULL DEFAULT 0,
    "IsDeleted"               INTEGER NOT NULL DEFAULT 0,
    "Priority"                INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX IF NOT EXISTS "IX_Jobs_State_ScheduledFor" ON "Jobs" ("State", "ScheduledFor") WHERE "IsDeleted" = 0;
CREATE INDEX IF NOT EXISTS "IX_Jobs_DeviceName" ON "Jobs" ("DeviceName") WHERE "IsDeleted" = 0;
CREATE INDEX IF NOT EXISTS "IX_Jobs_Processing_Deadline" ON "Jobs" ("State", "ProcessingDeadline") WHERE "IsDeleted" = 0;
CREATE INDEX IF NOT EXISTS "IX_Jobs_ConcurrencyKey_State" ON "Jobs" ("ConcurrencyKey", "State") WHERE "IsDeleted" = 0 AND "ConcurrencyKey" IS NOT NULL;
CREATE INDEX IF NOT EXISTS "IX_Jobs_ParentJobId" ON "Jobs" ("ParentJobId") WHERE "IsDeleted" = 0 AND "ParentJobId" IS NOT NULL;

CREATE TABLE IF NOT EXISTS "JobHistory"
(
    "Id"        INTEGER PRIMARY KEY AUTOINCREMENT,
    "JobId"     TEXT    NOT NULL,
    "State"     INTEGER NOT NULL,
    "Timestamp" INTEGER NOT NULL,
    "Note"      TEXT    NULL
);

CREATE INDEX IF NOT EXISTS "IX_JobHistory_JobId_Timestamp" ON "JobHistory" ("JobId", "Timestamp");

CREATE TABLE IF NOT EXISTS "RecurringJobs"
(
    "RecurringJobId" TEXT    NOT NULL PRIMARY KEY,
    "DeviceName"     TEXT    NOT NULL,
    "Arguments"      TEXT    NOT NULL,
    "MethodName"     TEXT    NOT NULL,
    "Assembly"       TEXT    NOT NULL,
    "DeclaringType"  TEXT    NOT NULL,
    "CronExpression" TEXT    NOT NULL,
    "NextRunAt"      INTEGER NOT NULL,
    "LastRunAt"      INTEGER NULL,
    "IsPaused"       INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX IF NOT EXISTS "IX_RecurringJobs_NextRunAt" ON "RecurringJobs" ("NextRunAt");

CREATE TABLE IF NOT EXISTS "ServerInstances"
(
    "InstanceId"   TEXT    NOT NULL PRIMARY KEY,
    "MachineName"  TEXT    NOT NULL,
    "StartedAt"    INTEGER NOT NULL,
    "LastSeenAt"   INTEGER NOT NULL,
    "ServedQueues" TEXT    NOT NULL DEFAULT 'default'
);

CREATE INDEX IF NOT EXISTS "IX_ServerInstances_LastSeenAt" ON "ServerInstances" ("LastSeenAt");

-- Published device connections, so the dashboard's Clients tab reflects the whole fleet rather
-- than only whichever instance happens to answer a given request (a SignalR connection is
-- pinned to whichever instance accepted it). InstanceId identifies the owning instance so a row
-- can be recognized as stale (see AxonSQLiteDeviceConnectionStore.GetAll) if that instance's
-- ServerInstances heartbeat has gone quiet. In practice, with SQLite being single-instance-only
-- (see the README), this table only ever has rows owned by the one process using it.
CREATE TABLE IF NOT EXISTS "DeviceConnections"
(
    "DeviceName"   TEXT    NOT NULL PRIMARY KEY,
    "ConnectionId" TEXT    NOT NULL,
    "InstanceId"   TEXT    NOT NULL,
    "ConnectedAt"  INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS "IX_DeviceConnections_ConnectionId" ON "DeviceConnections" ("ConnectionId");
CREATE INDEX IF NOT EXISTS "IX_DeviceConnections_InstanceId" ON "DeviceConnections" ("InstanceId");
