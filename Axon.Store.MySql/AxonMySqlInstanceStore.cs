using Axon.Server.Interfaces;
using Axon.Server.Services;
using Dapper;
using MySqlConnector;

namespace Axon.MySql;

public class AxonMySqlInstanceStore(string connectionString) : IAxonServerInstanceStore
{
    private MySqlConnection CreateConnection() =>
        new(connectionString);

    public Task Heartbeat(ServerInstance instance) => MySqlExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        const string sql = @"
            INSERT INTO `ServerInstances` (`InstanceId`, `MachineName`, `StartedAt`, `LastSeenAt`, `ServedQueues`)
            VALUES (@InstanceId, @MachineName, @StartedAt, @LastSeenAt, @ServedQueues)
            ON DUPLICATE KEY UPDATE `LastSeenAt` = @LastSeenAt, `ServedQueues` = @ServedQueues";
        await conn.ExecuteAsync(sql, new
        {
            instance.InstanceId,
            instance.MachineName,
            instance.StartedAt,
            instance.LastSeenAt,
            instance.ServedQueues
        });
    });

    public Task<List<ServerInstance>> GetAll() => MySqlExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        const string sql = "SELECT * FROM `ServerInstances` ORDER BY `LastSeenAt` DESC";
        return (await conn.QueryAsync<ServerInstance>(sql)).ToList();
    });

    public Task Remove(string instanceId) => MySqlExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        const string sql = "DELETE FROM `ServerInstances` WHERE `InstanceId` = @InstanceId";
        await conn.ExecuteAsync(sql, new { InstanceId = instanceId });
    });
}
