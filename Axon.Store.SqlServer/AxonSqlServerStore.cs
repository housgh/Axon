using System.Data;
using Axon.Core.Enums;
using Axon.Server.Interfaces;
using Axon.Server.Services;
using Dapper;
using Microsoft.Data.SqlClient;
// ReSharper disable UseRawString

namespace Axon.SqlServer;

public class AxonSqlServerStore(string connectionString) : IAxonJobStore
{
    static AxonSqlServerStore()
    {
        SqlMapper.AddTypeHandler(new JobArgumentsTypeHandler());
    }

    private SqlConnection CreateConnection() =>
        new(connectionString);

    private static async Task AppendHistory(SqlConnection conn, IDbTransaction tx, string jobId, JobState state, string? note)
    {
        const string sql = @"
            INSERT INTO JobHistory (JobId, State, Timestamp, Note)
            VALUES (@JobId, @State, @Timestamp, @Note)";
        await conn.ExecuteAsync(sql, new
        {
            JobId = jobId,
            State = (int)state,
            Timestamp = DateTime.UtcNow.Ticks,
            Note = note
        }, tx);
    }

    public async Task AddJob(Job job)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        const string sql = @"
            INSERT INTO Jobs
            (JobId, DeviceName, Arguments, MethodName, Assembly, DeclaringType, ScheduledFor, State, Attempts, MaxAttempts, IsDeleted)
            VALUES
            (@JobId, @DeviceName, @Arguments, @MethodName, @Assembly, @DeclaringType, @ScheduledFor, @State, @Attempts, @MaxAttempts, 0)";
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
            job.MaxAttempts
        }, tx);
        await AppendHistory(conn, tx, job.JobId, job.State, null);

        await tx.CommitAsync();
    }

    public async Task<Job?> GetJob(string id)
    {
        await using var conn = CreateConnection();
        const string sql = "SELECT * FROM Jobs WHERE JobId = @Id AND IsDeleted = 0";
        return await conn.QueryFirstOrDefaultAsync<Job>(sql, new { Id = id });
    }

    public async Task<List<Job>> GetJobs(int skip = 0, int take = 20, JobState[]? states = null)
    {
        await using var conn = CreateConnection();

        if (states is { Length: > 0 })
        {
            const string sql = @"
                SELECT * FROM Jobs
                WHERE IsDeleted = 0 AND State IN @States
                ORDER BY ScheduledFor
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
                SELECT * FROM Jobs
                WHERE IsDeleted = 0
                ORDER BY ScheduledFor
                OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY";
            return (await conn.QueryAsync<Job>(sql, new { Skip = skip, Take = take })).ToList();
        }
    }

    public async Task UpdateState(string id, JobState state, string? note = null)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        const string sql = "UPDATE Jobs SET State = @State WHERE JobId = @Id AND IsDeleted = 0";
        await conn.ExecuteAsync(sql, new { Id = id, State = (int)state }, tx);
        await AppendHistory(conn, tx, id, state, note);

        await tx.CommitAsync();
    }

    public async Task RequeueForRetry(string id, long scheduledFor, string? note = null)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        const string sql = @"
            UPDATE Jobs
            SET State = @State, ScheduledFor = @ScheduledFor, Attempts = Attempts + 1
            WHERE JobId = @Id AND IsDeleted = 0";
        await conn.ExecuteAsync(sql, new { Id = id, State = (int)JobState.Scheduled, ScheduledFor = scheduledFor }, tx);
        await AppendHistory(conn, tx, id, JobState.Scheduled, note);

        await tx.CommitAsync();
    }

    public async Task Requeue(string id)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        const string sql = @"
            UPDATE Jobs
            SET State = @State, ScheduledFor = NULL, Attempts = 0
            WHERE JobId = @Id AND IsDeleted = 0";
        await conn.ExecuteAsync(sql, new { Id = id, State = (int)JobState.Enqueued }, tx);
        await AppendHistory(conn, tx, id, JobState.Enqueued, "Requeued manually");

        await tx.CommitAsync();
    }

    public async Task DeleteJob(string id)
    {
        await using var conn = CreateConnection();
        const string sql = "UPDATE Jobs SET IsDeleted = 1 WHERE JobId = @Id";
        await conn.ExecuteAsync(sql, new { Id = id });
    }

    public async Task RecordFailure(string id, string? error)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await AppendHistory(conn, tx, id, JobState.Failed, error);

        await tx.CommitAsync();
    }

    public async Task<List<JobHistoryEntry>> GetHistory(string jobId)
    {
        await using var conn = CreateConnection();
        const string sql = "SELECT * FROM JobHistory WHERE JobId = @JobId ORDER BY Timestamp";
        return (await conn.QueryAsync<JobHistoryEntry>(sql, new { JobId = jobId })).ToList();
    }

    public async Task MarkProcessing(string id, long processingDeadline, string? note = null)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        const string sql = @"
            UPDATE Jobs
            SET State = @State, ProcessingDeadline = @ProcessingDeadline
            WHERE JobId = @Id AND IsDeleted = 0";
        await conn.ExecuteAsync(sql, new { Id = id, State = (int)JobState.Processing, ProcessingDeadline = processingDeadline }, tx);
        await AppendHistory(conn, tx, id, JobState.Processing, note);

        await tx.CommitAsync();
    }

    public async Task<List<Job>> GetOrphanedProcessingJobs(long asOf)
    {
        await using var conn = CreateConnection();
        const string sql = @"
            SELECT * FROM Jobs
            WHERE IsDeleted = 0 AND State = @State AND ProcessingDeadline IS NOT NULL AND ProcessingDeadline < @AsOf";
        return (await conn.QueryAsync<Job>(sql, new { State = (int)JobState.Processing, AsOf = asOf })).ToList();
    }

    public async Task<List<Job>> GetProcessingJobsForDevice(string deviceName)
    {
        await using var conn = CreateConnection();
        const string sql = @"
            SELECT * FROM Jobs
            WHERE IsDeleted = 0 AND State = @State AND DeviceName = @DeviceName";
        return (await conn.QueryAsync<Job>(sql, new { State = (int)JobState.Processing, DeviceName = deviceName })).ToList();
    }
}
