using System.Data;
using Axon.Core.Enums;
using Axon.Server.Interfaces;
using Axon.Server.Services;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Axon.SQLite;

public class AxonSQLiteStore(string connectionString) : IAxonJobStore
{
    static AxonSQLiteStore()
    {
        SqlMapper.AddTypeHandler(new JobArgumentsTypeHandler());
        SqlMapper.AddTypeHandler(new JobRetryPolicyTypeHandler());
    }

    private SqliteConnection CreateConnection() =>
        new(connectionString);

    // IsolationLevel.Serializable maps to SQLite's BEGIN IMMEDIATE rather than the default
    // deferred BEGIN, taking the write lock up front instead of only when the first write
    // statement runs. SQLite is single-writer regardless (the whole database file is locked for
    // any write transaction), so this doesn't add cross-process concurrency guarantees the way
    // the other backends' row/range locks do - it only avoids a specific SQLite failure mode
    // where two deferred transactions each successfully read, then both try to upgrade to a
    // write lock and one gets SQLITE_BUSY/"database is locked" instead of just waiting its turn.
    private static async Task<SqliteTransaction> BeginImmediateAsync(SqliteConnection conn) =>
        (SqliteTransaction)await conn.BeginTransactionAsync(IsolationLevel.Serializable);

    private static async Task AppendHistory(SqliteConnection conn, IDbTransaction tx, string jobId, JobState state, string? note)
    {
        const string sql = @"
            INSERT INTO ""JobHistory"" (""JobId"", ""State"", ""Timestamp"", ""Note"")
            VALUES (@JobId, @State, @Timestamp, @Note)";
        await conn.ExecuteAsync(sql, new
        {
            JobId = jobId,
            State = (int)state,
            Timestamp = DateTime.UtcNow.Ticks,
            Note = note
        }, tx);
    }

    public Task AddJob(Job job) => SQLiteExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync();
        await using var tx = await BeginImmediateAsync(conn);

        const string sql = @"
            INSERT INTO ""Jobs""
            (""JobId"", ""DeviceName"", ""Arguments"", ""MethodName"", ""Assembly"", ""DeclaringType"", ""ScheduledFor"", ""State"", ""Attempts"", ""MaxAttempts"", ""EnqueuedAt"", ""RetryPolicy"", ""ConcurrencyKey"", ""MaxConcurrent"", ""ParentJobId"", ""ContinueOnParentFailure"", ""IsDeleted"", ""Priority"")
            VALUES
            (@JobId, @DeviceName, @Arguments, @MethodName, @Assembly, @DeclaringType, @ScheduledFor, @State, @Attempts, @MaxAttempts, @EnqueuedAt, @RetryPolicy, @ConcurrencyKey, @MaxConcurrent, @ParentJobId, @ContinueOnParentFailure, 0, @Priority)";
        await conn.ExecuteAsync(sql, new
        {
            job.JobId,
            job.DeviceName,
            job.Arguments,
            job.MethodName,
            job.Assembly,
            job.DeclaringType,
            job.ScheduledFor,
            State = (int)job.State,
            job.Attempts,
            job.MaxAttempts,
            job.EnqueuedAt,
            job.RetryPolicy,
            job.ConcurrencyKey,
            job.MaxConcurrent,
            job.ParentJobId,
            job.ContinueOnParentFailure,
            Priority = (int)job.Priority
        }, tx);
        await AppendHistory(conn, tx, job.JobId, job.State, null);

        await tx.CommitAsync();
    });

    public Task<Job?> GetJob(string id) => SQLiteExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        const string sql = @"SELECT * FROM ""Jobs"" WHERE ""JobId"" = @Id AND ""IsDeleted"" = 0";
        return await conn.QueryFirstOrDefaultAsync<Job>(sql, new { Id = id });
    });

    public Task<List<Job>> GetJobs(int skip = 0, int take = 20, JobState[]? states = null) => SQLiteExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();

        if (states is { Length: > 0 })
        {
            const string sql = @"
                SELECT * FROM ""Jobs""
                WHERE ""IsDeleted"" = 0 AND ""State"" IN @States
                ORDER BY " + EffectiveScoreOrderBy + @"
                LIMIT @Take OFFSET @Skip";
            return (await conn.QueryAsync<Job>(sql, new
            {
                States = states.Select(s => (int)s).ToArray(),
                Skip = skip,
                Take = take
            })).ToList();
        }
        else
        {
            const string sql = @"
                SELECT * FROM ""Jobs""
                WHERE ""IsDeleted"" = 0
                ORDER BY " + EffectiveScoreOrderBy + @"
                LIMIT @Take OFFSET @Skip";
            return (await conn.QueryAsync<Job>(sql, new { Skip = skip, Take = take })).ToList();
        }
    });

    // Effective dispatch score: COALESCE(ScheduledFor, EnqueuedAt) - Boost[Priority], ascending
    // (lower score = dispatched sooner). The tick literals below must match
    // Axon.Core.Enums.JobPriorityBoost.Ticks exactly - SQL text can't reference that dictionary
    // directly. JobPriority's enum order is Medium=0, Low=1, High=2, Critical=3.
    private const string EffectiveScoreOrderBy = @"
        COALESCE(""ScheduledFor"", ""EnqueuedAt"") - (CASE ""Priority""
            WHEN 0 THEN 3000000000  -- Medium: 5 min
            WHEN 1 THEN 0           -- Low: 0
            WHEN 2 THEN 9000000000  -- High: 15 min
            WHEN 3 THEN 36000000000 -- Critical: 60 min
            ELSE 0 END)";

    public Task UpdateState(string id, JobState state, string? note = null) => SQLiteExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync();
        await using var tx = await BeginImmediateAsync(conn);

        const string sql = @"UPDATE ""Jobs"" SET ""State"" = @State WHERE ""JobId"" = @Id AND ""IsDeleted"" = 0";
        await conn.ExecuteAsync(sql, new { Id = id, State = (int)state }, tx);
        await AppendHistory(conn, tx, id, state, note);

        await tx.CommitAsync();
    });

    public Task RequeueForRetry(string id, long scheduledFor, string? note = null) => SQLiteExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync();
        await using var tx = await BeginImmediateAsync(conn);

        const string sql = @"
            UPDATE ""Jobs""
            SET ""State"" = @State, ""ScheduledFor"" = @ScheduledFor, ""Attempts"" = ""Attempts"" + 1
            WHERE ""JobId"" = @Id AND ""IsDeleted"" = 0";
        await conn.ExecuteAsync(sql, new { Id = id, State = (int)JobState.Scheduled, ScheduledFor = scheduledFor }, tx);
        await AppendHistory(conn, tx, id, JobState.Scheduled, note);

        await tx.CommitAsync();
    });

    public Task Requeue(string id, string note = "Requeued manually") => SQLiteExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync();
        await using var tx = await BeginImmediateAsync(conn);

        const string sql = @"
            UPDATE ""Jobs""
            SET ""State"" = @State, ""ScheduledFor"" = NULL, ""Attempts"" = 0
            WHERE ""JobId"" = @Id AND ""IsDeleted"" = 0";
        await conn.ExecuteAsync(sql, new { Id = id, State = (int)JobState.Enqueued }, tx);
        await AppendHistory(conn, tx, id, JobState.Enqueued, note);

        await tx.CommitAsync();
    });

    public Task DeleteJob(string id) => SQLiteExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        const string sql = @"UPDATE ""Jobs"" SET ""IsDeleted"" = 1 WHERE ""JobId"" = @Id";
        await conn.ExecuteAsync(sql, new { Id = id });
    });

    public Task RecordFailure(string id, string? error) => SQLiteExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync();
        await using var tx = await BeginImmediateAsync(conn);

        await AppendHistory(conn, tx, id, JobState.Failed, error);

        await tx.CommitAsync();
    });

    public Task<List<JobHistoryEntry>> GetHistory(string jobId) => SQLiteExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        const string sql = @"SELECT * FROM ""JobHistory"" WHERE ""JobId"" = @JobId ORDER BY ""Timestamp""";
        return (await conn.QueryAsync<JobHistoryEntry>(sql, new { JobId = jobId })).ToList();
    });

    public Task<bool> TryClaimJob(string id, long processingDeadline, string? note = null) => SQLiteExceptionTranslator.Run(connectionString, async () =>
    {
        // BEGIN IMMEDIATE (via BeginImmediateAsync) takes SQLite's single process-wide write
        // lock up front, before the count-then-update below runs - so unlike the other backends,
        // there's no separate row/range/advisory lock to reason about: only one write
        // transaction can be mid-flight against this database file at a time, full stop. A
        // second concurrent TryClaimJob call (even for a different job) simply waits for this
        // transaction to commit or roll back before it can begin its own. This is coarser than
        // the other backends' per-ConcurrencyKey locking, but it's also unconditionally correct -
        // see the README/csproj description for why Axon.Store.SQLite targets single-instance
        // use rather than the multi-instance fleet scenario the other backends are built for.
        const int maxAttempts = 5;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return await TryClaimJobCore(id, processingDeadline, note);
            }
            catch (SqliteException e) when (e.SqliteErrorCode == 5 && attempt < maxAttempts) // SQLITE_BUSY
            {
                await Task.Delay(Random.Shared.Next(10, 50) * attempt);
            }
        }

        // Unreachable: the loop either returns or the final attempt's exception propagates.
        throw new InvalidOperationException("TryClaimJob retry loop exited without returning or throwing.");
    });

    private async Task<bool> TryClaimJobCore(string id, long processingDeadline, string? note)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync();
        await using var tx = await BeginImmediateAsync(conn);

        const string countSql = @"
            SELECT COUNT(*) FROM ""Jobs""
            WHERE ""ConcurrencyKey"" = (SELECT ""ConcurrencyKey"" FROM ""Jobs"" WHERE ""JobId"" = @Id)
            AND ""State"" = @Processing AND ""IsDeleted"" = 0";
        var lockedCount = await conn.ExecuteScalarAsync<long>(countSql, new { Id = id, Processing = (int)JobState.Processing }, tx);

        // The State IN (...) guard is what makes this an atomic claim rather than a plain
        // update: no other write transaction can be interleaved with this one (BEGIN IMMEDIATE
        // holds SQLite's single write lock for the whole transaction), so if two callers race to
        // claim the same job, the second one's transaction simply starts after the first has
        // already committed the state change - it sees the updated State and its UPDATE matches
        // 0 rows.
        const string sql = @"
            UPDATE ""Jobs""
            SET ""State"" = @State, ""ProcessingDeadline"" = @ProcessingDeadline
            WHERE ""JobId"" = @Id AND ""IsDeleted"" = 0 AND ""State"" IN (@Enqueued, @Scheduled)
            AND (
                ""ConcurrencyKey"" IS NULL OR ""MaxConcurrent"" IS NULL
                OR @LockedCount < ""MaxConcurrent""
            )";
        var rowsAffected = await conn.ExecuteAsync(sql, new
        {
            Id = id,
            State = (int)JobState.Processing,
            ProcessingDeadline = processingDeadline,
            Enqueued = (int)JobState.Enqueued,
            Scheduled = (int)JobState.Scheduled,
            LockedCount = lockedCount
        }, tx);

        if (rowsAffected == 0)
        {
            await tx.RollbackAsync();
            return false;
        }

        await AppendHistory(conn, tx, id, JobState.Processing, note);
        await tx.CommitAsync();
        return true;
    }

    public Task<List<Job>> GetOrphanedProcessingJobs(long asOf) => SQLiteExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        const string sql = @"
            SELECT * FROM ""Jobs""
            WHERE ""IsDeleted"" = 0 AND ""State"" = @State AND ""ProcessingDeadline"" IS NOT NULL AND ""ProcessingDeadline"" < @AsOf";
        return (await conn.QueryAsync<Job>(sql, new { State = (int)JobState.Processing, AsOf = asOf })).ToList();
    });

    public Task<List<Job>> GetProcessingJobsForDevice(string deviceName) => SQLiteExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        const string sql = @"
            SELECT * FROM ""Jobs""
            WHERE ""IsDeleted"" = 0 AND ""State"" = @State AND ""DeviceName"" = @DeviceName";
        return (await conn.QueryAsync<Job>(sql, new { State = (int)JobState.Processing, DeviceName = deviceName })).ToList();
    });

    public Task<List<Job>> GetContinuationsWaitingOn(string parentJobId) => SQLiteExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        const string sql = @"
            SELECT * FROM ""Jobs""
            WHERE ""IsDeleted"" = 0 AND ""State"" = @State AND ""ParentJobId"" = @ParentJobId";
        return (await conn.QueryAsync<Job>(sql, new { State = (int)JobState.AwaitingParent, ParentJobId = parentJobId })).ToList();
    });

    public Task<int> DeleteCompletedJobsOlderThan(long cutoff) => SQLiteExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync();
        await using var tx = await BeginImmediateAsync(conn);

        // A job's "finished at" isn't its own column - it's the timestamp of its most recent
        // JobHistory row - so identify candidates via that join rather than a dedicated
        // completed-at column, to avoid adding one more field every terminal-state code path
        // would need to remember to set.
        const string selectSql = @"
            SELECT j.""JobId"" FROM ""Jobs"" j
            INNER JOIN (
                SELECT ""JobId"", MAX(""Timestamp"") AS ""LastTimestamp"" FROM ""JobHistory"" GROUP BY ""JobId""
            ) h ON h.""JobId"" = j.""JobId""
            WHERE j.""IsDeleted"" = 0 AND j.""State"" IN @States AND h.""LastTimestamp"" < @Cutoff";
        var jobIds = (await conn.QueryAsync<string>(selectSql, new
        {
            States = new[] { (int)JobState.Succeeded, (int)JobState.Failed, (int)JobState.Skipped },
            Cutoff = cutoff
        }, tx)).ToList();

        if (jobIds.Count == 0)
        {
            await tx.CommitAsync();
            return 0;
        }

        const string deleteJobsSql = @"UPDATE ""Jobs"" SET ""IsDeleted"" = 1 WHERE ""JobId"" IN @JobIds";
        await conn.ExecuteAsync(deleteJobsSql, new { JobIds = jobIds }, tx);

        const string deleteHistorySql = @"DELETE FROM ""JobHistory"" WHERE ""JobId"" IN @JobIds";
        await conn.ExecuteAsync(deleteHistorySql, new { JobIds = jobIds }, tx);

        await tx.CommitAsync();
        return jobIds.Count;
    });
}
