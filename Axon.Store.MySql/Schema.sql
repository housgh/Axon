-- Axon.Store.MySql schema
-- Run this once against the target database before using AddAxonMySqlStore().

CREATE TABLE IF NOT EXISTS `Jobs`
(
    `JobId`         VARCHAR(64)   NOT NULL PRIMARY KEY,
    `DeviceName`    VARCHAR(256)  NOT NULL,
    `Arguments`     LONGTEXT      NOT NULL,
    `MethodName`    VARCHAR(256)  NOT NULL,
    `Assembly`      VARCHAR(512)  NOT NULL,
    `DeclaringType` VARCHAR(512)  NOT NULL,
    `ScheduledFor`            BIGINT   NULL,
    `State`                   INT      NOT NULL,
    `Attempts`                INT      NOT NULL DEFAULT 0,
    `MaxAttempts`             INT      NOT NULL DEFAULT 3,
    `ProcessingDeadline`      BIGINT   NULL,
    `EnqueuedAt`              BIGINT   NOT NULL DEFAULT 0,
    `RetryPolicy`             LONGTEXT NULL,
    `ConcurrencyKey`          VARCHAR(256) NULL,
    `MaxConcurrent`           INT      NULL,
    `ParentJobId`             VARCHAR(64) NULL,
    `ContinueOnParentFailure` TINYINT(1) NOT NULL DEFAULT 0,
    `IsDeleted`               TINYINT(1) NOT NULL DEFAULT 0,
    `Priority`                INT      NOT NULL DEFAULT 0,

    -- MySQL has no partial/filtered indexes (no WHERE clause), so these index the whole table
    -- rather than only IsDeleted = 0 rows the way the SQL Server/Postgres schemas do - the query
    -- patterns (AxonMySqlStore.cs) still filter IsDeleted explicitly, this just means the index
    -- itself is a little larger.
    INDEX `IX_Jobs_State_ScheduledFor` (`State`, `ScheduledFor`),
    INDEX `IX_Jobs_DeviceName` (`DeviceName`),
    INDEX `IX_Jobs_Processing_Deadline` (`State`, `ProcessingDeadline`),
    INDEX `IX_Jobs_ConcurrencyKey_State` (`ConcurrencyKey`, `State`),
    INDEX `IX_Jobs_ParentJobId` (`ParentJobId`)
);

CREATE TABLE IF NOT EXISTS `JobHistory`
(
    `Id`        BIGINT AUTO_INCREMENT NOT NULL PRIMARY KEY,
    `JobId`     VARCHAR(64)   NOT NULL,
    `State`     INT           NOT NULL,
    `Timestamp` BIGINT        NOT NULL,
    `Note`      VARCHAR(1024) NULL,

    INDEX `IX_JobHistory_JobId_Timestamp` (`JobId`, `Timestamp`)
);

CREATE TABLE IF NOT EXISTS `RecurringJobs`
(
    `RecurringJobId` VARCHAR(64)   NOT NULL PRIMARY KEY,
    `DeviceName`     VARCHAR(256)  NOT NULL,
    `Arguments`      LONGTEXT      NOT NULL,
    `MethodName`     VARCHAR(256)  NOT NULL,
    `Assembly`       VARCHAR(512)  NOT NULL,
    `DeclaringType`  VARCHAR(512)  NOT NULL,
    `CronExpression` VARCHAR(64)   NOT NULL,
    `NextRunAt`      BIGINT        NOT NULL,
    `LastRunAt`      BIGINT        NULL,
    `IsPaused`       TINYINT(1)    NOT NULL DEFAULT 0,

    INDEX `IX_RecurringJobs_NextRunAt` (`NextRunAt`)
);

CREATE TABLE IF NOT EXISTS `ServerInstances`
(
    `InstanceId`  VARCHAR(64)  NOT NULL PRIMARY KEY,
    `MachineName` VARCHAR(256) NOT NULL,
    `StartedAt`   BIGINT       NOT NULL,
    `LastSeenAt`  BIGINT       NOT NULL,

    INDEX `IX_ServerInstances_LastSeenAt` (`LastSeenAt`)
);

-- Published device connections, so the dashboard's Clients tab reflects the whole fleet rather
-- than only whichever instance happens to answer a given request (a SignalR connection is
-- pinned to whichever instance accepted it). InstanceId identifies the owning instance so a row
-- can be recognized as stale (see AxonMySqlDeviceConnectionStore.GetAll) if that instance's
-- ServerInstances heartbeat has gone quiet.
CREATE TABLE IF NOT EXISTS `DeviceConnections`
(
    `DeviceName`   VARCHAR(256) NOT NULL PRIMARY KEY,
    `ConnectionId` VARCHAR(64)  NOT NULL,
    `InstanceId`   VARCHAR(64)  NOT NULL,
    `ConnectedAt`  BIGINT       NOT NULL,

    INDEX `IX_DeviceConnections_ConnectionId` (`ConnectionId`),
    INDEX `IX_DeviceConnections_InstanceId` (`InstanceId`)
);
