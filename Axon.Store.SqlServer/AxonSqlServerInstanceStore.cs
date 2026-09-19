using Axon.Server.Interfaces;
using Axon.Server.Services;
using Dapper;
using Microsoft.Data.SqlClient;
// ReSharper disable UseRawString

namespace Axon.SqlServer;

public class AxonSqlServerInstanceStore(string connectionString) : IAxonServerInstanceStore
{
    private SqlConnection CreateConnection() =>
        new(connectionString);

    public async Task Heartbeat(ServerInstance instance)
    {
        await using var conn = CreateConnection();
        const string sql = @"
            MERGE ServerInstances AS target
            USING (SELECT @InstanceId AS InstanceId) AS source
            ON target.InstanceId = source.InstanceId
            WHEN MATCHED THEN
                UPDATE SET LastSeenAt = @LastSeenAt
            WHEN NOT MATCHED THEN
                INSERT (InstanceId, MachineName, StartedAt, LastSeenAt)
                VALUES (@InstanceId, @MachineName, @StartedAt, @LastSeenAt);";
        await conn.ExecuteAsync(sql, new
        {
            instance.InstanceId,
            instance.MachineName,
            instance.StartedAt,
            instance.LastSeenAt
        });
    }

    public async Task<List<ServerInstance>> GetAll()
    {
        await using var conn = CreateConnection();
        const string sql = "SELECT * FROM ServerInstances ORDER BY LastSeenAt DESC";
        return (await conn.QueryAsync<ServerInstance>(sql)).ToList();
    }

    public async Task Remove(string instanceId)
    {
        await using var conn = CreateConnection();
        const string sql = "DELETE FROM ServerInstances WHERE InstanceId = @InstanceId";
        await conn.ExecuteAsync(sql, new { InstanceId = instanceId });
    }
}
