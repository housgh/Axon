using Axon.Server.Interfaces;
using Axon.Server.Services;
using Dapper;
using Npgsql;

namespace Axon.Postgres;

public class AxonPostgresRecurringJobStore(string connectionString) : IAxonRecurringJobStore
{
    static AxonPostgresRecurringJobStore()
    {
        SqlMapper.AddTypeHandler(new JobArgumentsTypeHandler());
    }

    private NpgsqlConnection CreateConnection() =>
        new(connectionString);

    public Task AddOrUpdate(RecurringJob recurringJob) => PostgresExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        // IsPaused is deliberately absent from the UPDATE clause: a client re-registering an
        // existing recurring job (AddOrUpdateRecurringAsync is idempotent by id, and this runs
        // on every client startup) has no isPaused concept of its own to declare - IsPaused is
        // purely an operator-driven flag set via SetPaused (e.g. from the dashboard), so a
        // client restart must not silently undo an operator's pause. New rows still default to
        // unpaused.
        const string sql = @"
            INSERT INTO ""RecurringJobs""
            (""RecurringJobId"", ""DeviceName"", ""Arguments"", ""MethodName"", ""Assembly"", ""DeclaringType"", ""CronExpression"", ""NextRunAt"", ""LastRunAt"", ""IsPaused"")
            VALUES
            (@RecurringJobId, @DeviceName, @Arguments, @MethodName, @Assembly, @DeclaringType, @CronExpression, @NextRunAt, @LastRunAt, FALSE)
            ON CONFLICT (""RecurringJobId"") DO UPDATE SET
                ""DeviceName"" = @DeviceName,
                ""Arguments"" = @Arguments,
                ""MethodName"" = @MethodName,
                ""Assembly"" = @Assembly,
                ""DeclaringType"" = @DeclaringType,
                ""CronExpression"" = @CronExpression,
                ""NextRunAt"" = @NextRunAt,
                ""LastRunAt"" = @LastRunAt";
        await conn.ExecuteAsync(sql, new
        {
            recurringJob.RecurringJobId,
            recurringJob.DeviceName,
            recurringJob.Arguments,
            recurringJob.MethodName,
            recurringJob.Assembly,
            recurringJob.DeclaringType,
            recurringJob.CronExpression,
            recurringJob.NextRunAt,
            recurringJob.LastRunAt
        });
    });

    public Task<RecurringJob?> GetById(string recurringJobId) => PostgresExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        const string sql = @"SELECT * FROM ""RecurringJobs"" WHERE ""RecurringJobId"" = @RecurringJobId";
        return await conn.QueryFirstOrDefaultAsync<RecurringJob>(sql, new { RecurringJobId = recurringJobId });
    });

    public Task<List<RecurringJob>> GetAll(int skip = 0, int take = 20) => PostgresExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        const string sql = @"
            SELECT * FROM ""RecurringJobs""
            ORDER BY ""NextRunAt""
            OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY";
        return (await conn.QueryAsync<RecurringJob>(sql, new { Skip = skip, Take = take })).ToList();
    });

    public Task UpdateNextRun(string recurringJobId, long nextRunAt, long lastRunAt) => PostgresExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        const string sql = @"
            UPDATE ""RecurringJobs""
            SET ""NextRunAt"" = @NextRunAt, ""LastRunAt"" = @LastRunAt
            WHERE ""RecurringJobId"" = @RecurringJobId";
        await conn.ExecuteAsync(sql, new { RecurringJobId = recurringJobId, NextRunAt = nextRunAt, LastRunAt = lastRunAt });
    });

    public Task Remove(string recurringJobId) => PostgresExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        const string sql = @"DELETE FROM ""RecurringJobs"" WHERE ""RecurringJobId"" = @RecurringJobId";
        await conn.ExecuteAsync(sql, new { RecurringJobId = recurringJobId });
    });

    public Task SetPaused(string recurringJobId, bool isPaused) => PostgresExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        const string sql = @"UPDATE ""RecurringJobs"" SET ""IsPaused"" = @IsPaused WHERE ""RecurringJobId"" = @RecurringJobId";
        await conn.ExecuteAsync(sql, new { RecurringJobId = recurringJobId, IsPaused = isPaused });
    });

    public Task SkipNext(string recurringJobId, long newNextRunAt) => PostgresExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        const string sql = @"UPDATE ""RecurringJobs"" SET ""NextRunAt"" = @NextRunAt WHERE ""RecurringJobId"" = @RecurringJobId";
        await conn.ExecuteAsync(sql, new { RecurringJobId = recurringJobId, NextRunAt = newNextRunAt });
    });
}
