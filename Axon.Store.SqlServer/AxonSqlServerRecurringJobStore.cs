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

    public async Task AddOrUpdate(RecurringJob recurringJob)
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
    }

    public async Task<List<RecurringJob>> GetAll()
    {
        await using var conn = CreateConnection();
        const string sql = "SELECT * FROM RecurringJobs ORDER BY NextRunAt";
        return (await conn.QueryAsync<RecurringJob>(sql)).ToList();
    }

    public async Task UpdateNextRun(string recurringJobId, long nextRunAt, long lastRunAt)
    {
        await using var conn = CreateConnection();
        const string sql = @"
            UPDATE RecurringJobs
            SET NextRunAt = @NextRunAt, LastRunAt = @LastRunAt
            WHERE RecurringJobId = @RecurringJobId";
        await conn.ExecuteAsync(sql, new { RecurringJobId = recurringJobId, NextRunAt = nextRunAt, LastRunAt = lastRunAt });
    }

    public async Task Remove(string recurringJobId)
    {
        await using var conn = CreateConnection();
        const string sql = "DELETE FROM RecurringJobs WHERE RecurringJobId = @RecurringJobId";
        await conn.ExecuteAsync(sql, new { RecurringJobId = recurringJobId });
    }
}
