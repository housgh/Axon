using Axon.Server.Interfaces;
using Axon.Server.Services;
using Dapper;
using Microsoft.Data.SqlClient;
// ReSharper disable UseRawString

namespace Axon.SqlServer;

public class AxonSqlServerRecurringJobStore(string connectionString) : IAxonRecurringJobStore
{
    static AxonSqlServerRecurringJobStore()
    {
        SqlMapper.AddTypeHandler(new JobArgumentsTypeHandler());
    }

    private SqlConnection CreateConnection() =>
        new(connectionString);

    public Task AddOrUpdate(RecurringJob recurringJob) => SqlExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        // IsPaused is deliberately absent from WHEN MATCHED: a client re-registering an existing
        // recurring job (AddOrUpdateRecurringAsync is idempotent by id, and this runs on every
        // client startup) has no isPaused concept of its own to declare - IsPaused is purely an
        // operator-driven flag set via SetPaused (e.g. from the dashboard), so a client restart
        // must not silently undo an operator's pause. New rows still default to unpaused.
        const string sql = @"
            MERGE RecurringJobs AS target
            USING (SELECT @RecurringJobId AS RecurringJobId) AS source
            ON target.RecurringJobId = source.RecurringJobId
            WHEN MATCHED THEN
                UPDATE SET
                    DeviceName = @DeviceName,
                    Arguments = @Arguments,
                    MethodName = @MethodName,
                    Assembly = @Assembly,
                    DeclaringType = @DeclaringType,
                    CronExpression = @CronExpression,
                    NextRunAt = @NextRunAt,
                    LastRunAt = @LastRunAt
            WHEN NOT MATCHED THEN
                INSERT (RecurringJobId, DeviceName, Arguments, MethodName, Assembly, DeclaringType, CronExpression, NextRunAt, LastRunAt, IsPaused)
                VALUES (@RecurringJobId, @DeviceName, @Arguments, @MethodName, @Assembly, @DeclaringType, @CronExpression, @NextRunAt, @LastRunAt, 0);";
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

    public Task<RecurringJob?> GetById(string recurringJobId) => SqlExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        const string sql = "SELECT * FROM RecurringJobs WHERE RecurringJobId = @RecurringJobId";
        return await conn.QueryFirstOrDefaultAsync<RecurringJob>(sql, new { RecurringJobId = recurringJobId });
    });

    public Task<List<RecurringJob>> GetAll(int skip = 0, int take = 20) => SqlExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        const string sql = @"
            SELECT * FROM RecurringJobs
            ORDER BY NextRunAt
            OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY";
        return (await conn.QueryAsync<RecurringJob>(sql, new { Skip = skip, Take = take })).ToList();
    });

    public Task UpdateNextRun(string recurringJobId, long nextRunAt, long lastRunAt) => SqlExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        const string sql = @"
            UPDATE RecurringJobs
            SET NextRunAt = @NextRunAt, LastRunAt = @LastRunAt
            WHERE RecurringJobId = @RecurringJobId";
        await conn.ExecuteAsync(sql, new { RecurringJobId = recurringJobId, NextRunAt = nextRunAt, LastRunAt = lastRunAt });
    });

    public Task Remove(string recurringJobId) => SqlExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        const string sql = "DELETE FROM RecurringJobs WHERE RecurringJobId = @RecurringJobId";
        await conn.ExecuteAsync(sql, new { RecurringJobId = recurringJobId });
    });

    public Task SetPaused(string recurringJobId, bool isPaused) => SqlExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        const string sql = "UPDATE RecurringJobs SET IsPaused = @IsPaused WHERE RecurringJobId = @RecurringJobId";
        await conn.ExecuteAsync(sql, new { RecurringJobId = recurringJobId, IsPaused = isPaused });
    });

    public Task SkipNext(string recurringJobId, long newNextRunAt) => SqlExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        const string sql = "UPDATE RecurringJobs SET NextRunAt = @NextRunAt WHERE RecurringJobId = @RecurringJobId";
        await conn.ExecuteAsync(sql, new { RecurringJobId = recurringJobId, NextRunAt = newNextRunAt });
    });
}
