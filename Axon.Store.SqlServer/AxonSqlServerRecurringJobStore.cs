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
                INSERT (RecurringJobId, DeviceName, Arguments, MethodName, Assembly, DeclaringType, CronExpression, NextRunAt, LastRunAt)
                VALUES (@RecurringJobId, @DeviceName, @Arguments, @MethodName, @Assembly, @DeclaringType, @CronExpression, @NextRunAt, @LastRunAt);";
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
}
