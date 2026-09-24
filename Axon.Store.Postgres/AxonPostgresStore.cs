using System.Data;
using Axon.Core.Enums;
using Axon.Server.Interfaces;
using Axon.Server.Services;
using Dapper;
using Npgsql;

namespace Axon.Postgres;

public class AxonPostgresStore(string connectionString) : IAxonJobStore
{
    static AxonPostgresStore()
    {
        SqlMapper.AddTypeHandler(new JobArgumentsTypeHandler());
        SqlMapper.AddTypeHandler(new JobRetryPolicyTypeHandler());
    }

    private NpgsqlConnection CreateConnection() =>
        new(connectionString);

    private static async Task AppendHistory(NpgsqlConnection conn, IDbTransaction tx, string jobId, JobState state, string? note)
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

    public Task AddJob(Job job) => PostgresExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        const string sql = @"
            INSERT INTO ""Jobs""
            (""JobId"", ""DeviceName"", ""Arguments"", ""MethodName"", ""Assembly"", ""DeclaringType"", ""ScheduledFor"", ""State"", ""Attempts"", ""MaxAttempts"", ""EnqueuedAt"", ""RetryPolicy"", ""ConcurrencyKey"", ""MaxConcurrent"", ""ParentJobId"", ""ContinueOnParentFailure"", ""IsDeleted"", ""Priority"")
            VALUES
            (@JobId, @DeviceName, @Arguments, @MethodName, @Assembly, @DeclaringType, @ScheduledFor, @State, @Attempts, @MaxAttempts, @EnqueuedAt, @RetryPolicy, @ConcurrencyKey, @MaxConcurrent, @ParentJobId, @ContinueOnParentFailure, FALSE, @Priority)";
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

    public Task<Job?> GetJob(string id) => PostgresExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        const string sql = @"SELECT * FROM ""Jobs"" WHERE ""JobId"" = @Id AND ""IsDeleted"" = FALSE";
        return await conn.QueryFirstOrDefaultAsync<Job>(sql, new { Id = id });
    });

    public Task<List<Job>> GetJobs(int skip = 0, int take = 20, JobState[]? states = null) => PostgresExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();

        if (states is { Length: > 0 })
        {
            const string sql = @"
                SELECT * FROM ""Jobs""
                WHERE ""IsDeleted"" = FALSE AND ""State"" = ANY(@States)
                ORDER BY " + EffectiveScoreOrderBy + @"
                OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY";
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
                WHERE ""IsDeleted"" = FALSE
                ORDER BY " + EffectiveScoreOrderBy + @"
                OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY";
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

    public Task UpdateState(string id, JobState state, string? note = null) => PostgresExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        const string sql = @"UPDATE ""Jobs"" SET ""State"" = @State WHERE ""JobId"" = @Id AND ""IsDeleted"" = FALSE";
        await conn.ExecuteAsync(sql, new { Id = id, State = (int)state }, tx);
        await AppendHistory(conn, tx, id, state, note);

        await tx.CommitAsync();
    });

    public Task RequeueForRetry(string id, long scheduledFor, string? note = null) => PostgresExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        const string sql = @"
            UPDATE ""Jobs""
            SET ""State"" = @State, ""ScheduledFor"" = @ScheduledFor, ""Attempts"" = ""Attempts"" + 1
            WHERE ""JobId"" = @Id AND ""IsDeleted"" = FALSE";
        await conn.ExecuteAsync(sql, new { Id = id, State = (int)JobState.Scheduled, ScheduledFor = scheduledFor }, tx);
        await AppendHistory(conn, tx, id, JobState.Scheduled, note);

        await tx.CommitAsync();
    });

    public Task Requeue(string id, string note = "Requeued manually") => PostgresExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        const string sql = @"
            UPDATE ""Jobs""
            SET ""State"" = @State, ""ScheduledFor"" = NULL, ""Attempts"" = 0
            WHERE ""JobId"" = @Id AND ""IsDeleted"" = FALSE";
        await conn.ExecuteAsync(sql, new { Id = id, State = (int)JobState.Enqueued }, tx);
        await AppendHistory(conn, tx, id, JobState.Enqueued, note);

        await tx.CommitAsync();
    });

    public Task DeleteJob(string id) => PostgresExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        const string sql = @"UPDATE ""Jobs"" SET ""IsDeleted"" = TRUE WHERE ""JobId"" = @Id";
        await conn.ExecuteAsync(sql, new { Id = id });
    });

    public Task RecordFailure(string id, string? error) => PostgresExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await AppendHistory(conn, tx, id, JobState.Failed, error);

        await tx.CommitAsync();
    });

    public Task<List<JobHistoryEntry>> GetHistory(string jobId) => PostgresExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        const string sql = @"SELECT * FROM ""JobHistory"" WHERE ""JobId"" = @JobId ORDER BY ""Timestamp""";
        return (await conn.QueryAsync<JobHistoryEntry>(sql, new { JobId = jobId })).ToList();
    });

    public Task<bool> TryClaimJob(string id, long processingDeadline, string? note = null) => PostgresExceptionTranslator.Run(connectionString, async () =>
    {
        // Postgres doesn't deadlock on this pattern the way SQL Server's UPDLOCK/HOLDLOCK count
        // subquery can (see AxonSqlServerStore.TryClaimJob), because TryClaimJobCore always takes
        // its SELECT ... FOR UPDATE lock before its UPDATE, giving every transaction the same
        // lock-acquisition order - but a serialization failure (40001) is still possible under
        // concurrent load, so it gets the same bounded-retry treatment as a defensive measure.
        const int maxAttempts = 5;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return await TryClaimJobCore(id, processingDeadline, note);
            }
            catch (PostgresException e) when (e.SqlState == "40001" && attempt < maxAttempts)
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
        await using var tx = await conn.BeginTransactionAsync();

        // SQL Server's WITH (UPDLOCK, HOLDLOCK) locks the whole scanned key-range for the
        // ConcurrencyKey count subquery, so a second claim against the same key blocks even
        // though the row it's trying to claim isn't itself Processing yet (see
        // AxonSqlServerStore.TryClaimJob). A plain "SELECT ... FOR UPDATE" in Postgres can only
        // lock rows that already exist and match the WHERE clause - when every competing claim
        // is racing to be the *first* job with this key to become Processing, there's no
        // existing Processing row yet for any of them to lock, so FOR UPDATE alone doesn't
        // serialize them. pg_advisory_xact_lock keyed on the ConcurrencyKey does: it's a
        // transaction-scoped lock on the key itself (not on any row), so every concurrent claim
        // against the same key queues up here regardless of whether a Processing row exists yet,
        // and the lock is released automatically on commit/rollback.
        var concurrencyKey = await conn.ExecuteScalarAsync<string?>(
            @"SELECT ""ConcurrencyKey"" FROM ""Jobs"" WHERE ""JobId"" = @Id", new { Id = id }, tx);

        long lockedCount = 0;
        if (concurrencyKey is not null)
        {
            const string lockSql = @"SELECT pg_advisory_xact_lock(hashtext(@ConcurrencyKey))";
            await conn.ExecuteAsync(lockSql, new { ConcurrencyKey = concurrencyKey }, tx);

            const string countSql = @"
                SELECT COUNT(*) FROM ""Jobs""
                WHERE ""ConcurrencyKey"" = @ConcurrencyKey AND ""State"" = @Processing AND ""IsDeleted"" = FALSE";
            lockedCount = await conn.ExecuteScalarAsync<long>(countSql, new { ConcurrencyKey = concurrencyKey, Processing = (int)JobState.Processing }, tx);
        }

        // The State IN (...) guard is what makes this an atomic claim rather than a plain
        // update: Postgres holds the row lock for the duration of this UPDATE, so if two server
        // instances race to claim the same job, only one UPDATE can match and affect a row - the
        // other sees 0 rows affected and must not dispatch. lockedCount was captured under the
        // advisory lock above, so it's safe to compare directly rather than re-querying it here.
        const string sql = @"
            UPDATE ""Jobs""
            SET ""State"" = @State, ""ProcessingDeadline"" = @ProcessingDeadline
            WHERE ""JobId"" = @Id AND ""IsDeleted"" = FALSE AND ""State"" IN (@Enqueued, @Scheduled)
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

    public Task<List<Job>> GetOrphanedProcessingJobs(long asOf) => PostgresExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        const string sql = @"
            SELECT * FROM ""Jobs""
            WHERE ""IsDeleted"" = FALSE AND ""State"" = @State AND ""ProcessingDeadline"" IS NOT NULL AND ""ProcessingDeadline"" < @AsOf";
        return (await conn.QueryAsync<Job>(sql, new { State = (int)JobState.Processing, AsOf = asOf })).ToList();
    });

    public Task<List<Job>> GetProcessingJobsForDevice(string deviceName) => PostgresExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        const string sql = @"
            SELECT * FROM ""Jobs""
            WHERE ""IsDeleted"" = FALSE AND ""State"" = @State AND ""DeviceName"" = @DeviceName";
        return (await conn.QueryAsync<Job>(sql, new { State = (int)JobState.Processing, DeviceName = deviceName })).ToList();
    });

    public Task<List<Job>> GetContinuationsWaitingOn(string parentJobId) => PostgresExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        const string sql = @"
            SELECT * FROM ""Jobs""
            WHERE ""IsDeleted"" = FALSE AND ""State"" = @State AND ""ParentJobId"" = @ParentJobId";
        return (await conn.QueryAsync<Job>(sql, new { State = (int)JobState.AwaitingParent, ParentJobId = parentJobId })).ToList();
    });

    public Task<int> DeleteCompletedJobsOlderThan(long cutoff) => PostgresExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        // A job's "finished at" isn't its own column - it's the timestamp of its most recent
        // JobHistory row - so identify candidates via that join rather than a dedicated
        // completed-at column, to avoid adding one more field every terminal-state code path
        // would need to remember to set. Only Succeeded jobs are cleaned up - Failed and Skipped
        // are kept indefinitely regardless of age.
        const string selectSql = @"
            SELECT j.""JobId"" FROM ""Jobs"" j
            INNER JOIN (
                SELECT ""JobId"", MAX(""Timestamp"") AS ""LastTimestamp"" FROM ""JobHistory"" GROUP BY ""JobId""
            ) h ON h.""JobId"" = j.""JobId""
            WHERE j.""IsDeleted"" = FALSE AND j.""State"" = @Succeeded AND h.""LastTimestamp"" < @Cutoff";
        var jobIds = (await conn.QueryAsync<string>(selectSql, new
        {
            Succeeded = (int)JobState.Succeeded,
            Cutoff = cutoff
        }, tx)).ToList();

        if (jobIds.Count == 0)
        {
            await tx.CommitAsync();
            return 0;
        }

        const string deleteJobsSql = @"UPDATE ""Jobs"" SET ""IsDeleted"" = TRUE WHERE ""JobId"" = ANY(@JobIds)";
        await conn.ExecuteAsync(deleteJobsSql, new { JobIds = jobIds }, tx);

        const string deleteHistorySql = @"DELETE FROM ""JobHistory"" WHERE ""JobId"" = ANY(@JobIds)";
        await conn.ExecuteAsync(deleteHistorySql, new { JobIds = jobIds }, tx);

        await tx.CommitAsync();
        return jobIds.Count;
    });

    public Task<Dictionary<JobState, int>> CountJobsByState() => PostgresExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();

        // Live jobs in any state, plus soft-deleted (cleaned-up) jobs only when terminal - so a
        // Succeeded job purged by DeleteCompletedJobsOlderThan still counts, but IsDeleted never
        // applies to a non-terminal state in practice anyway.
        const string sql = @"
            SELECT ""State"", COUNT(*) AS ""Count"" FROM ""Jobs""
            WHERE ""IsDeleted"" = FALSE OR ""State"" = ANY(@States)
            GROUP BY ""State""";
        var rows = await conn.QueryAsync<(int State, int Count)>(sql, new
        {
            States = new[] { (int)JobState.Succeeded, (int)JobState.Failed, (int)JobState.Skipped }
        });

        return rows.ToDictionary(r => (JobState)r.State, r => r.Count);
    });
}
