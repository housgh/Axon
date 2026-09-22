using Axon.Server.Interfaces;
using Axon.Server.Services;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Axon.SQLite;

public class AxonSQLiteInstanceStore(string connectionString) : IAxonServerInstanceStore
{
    private SqliteConnection CreateConnection() =>
        new(connectionString);

    public Task Heartbeat(ServerInstance instance) => SQLiteExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        const string sql = @"
            INSERT INTO ""ServerInstances"" (""InstanceId"", ""MachineName"", ""StartedAt"", ""LastSeenAt"", ""ServedQueues"")
            VALUES (@InstanceId, @MachineName, @StartedAt, @LastSeenAt, @ServedQueues)
            ON CONFLICT (""InstanceId"") DO UPDATE SET ""LastSeenAt"" = @LastSeenAt, ""ServedQueues"" = @ServedQueues";
        await conn.ExecuteAsync(sql, new
        {
            instance.InstanceId,
            instance.MachineName,
            instance.StartedAt,
            instance.LastSeenAt,
            instance.ServedQueues
        });
    });

    public Task<List<ServerInstance>> GetAll() => SQLiteExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        const string sql = @"SELECT * FROM ""ServerInstances"" ORDER BY ""LastSeenAt"" DESC";
        return (await conn.QueryAsync<ServerInstance>(sql)).ToList();
    });

    public Task Remove(string instanceId) => SQLiteExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        const string sql = @"DELETE FROM ""ServerInstances"" WHERE ""InstanceId"" = @InstanceId";
        await conn.ExecuteAsync(sql, new { InstanceId = instanceId });
    });
}
