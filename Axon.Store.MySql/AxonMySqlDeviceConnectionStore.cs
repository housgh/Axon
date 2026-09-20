using Axon.Server.Interfaces;
using Axon.Server.Services;
using Dapper;
using MySqlConnector;

namespace Axon.MySql;

/// <summary>
/// Publishes this instance's device connections to a shared table, so the dashboard's Clients
/// tab reflects every instance's connections rather than only whichever instance answers a given
/// request. A stale row - left behind if an instance died without a clean SignalR disconnect
/// (OnDisconnectedAsync never running to call Unregister) - is filtered out by joining against
/// ServerInstances and checking the owning instance's heartbeat, the same staleness check the
/// Servers tab already uses for IsOnline.
/// </summary>
public class AxonMySqlDeviceConnectionStore(string connectionString) : IDeviceConnectionRegistry
{
    // Matches DependencyInjection.ServerInstanceOfflineTimeout: an instance (and therefore its
    // published connections) is considered gone once its heartbeat is older than this.
    private static readonly long OfflineTimeoutTicks = TimeSpan.FromSeconds(45).Ticks;

    private MySqlConnection CreateConnection() =>
        new(connectionString);

    public Task Register(string deviceName, string connectionId) => MySqlExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        // MySQL's ON DUPLICATE KEY UPDATE has no WHERE clause to skip a no-op update the way
        // Postgres's ON CONFLICT ... DO UPDATE ... WHERE can, so the "same connection id is a
        // no-op" check (mirroring InMemoryDeviceConnectionRegistry's AddOrUpdate semantics - a
        // re-Register from SignalR auto-reconnect with the same connection id shouldn't reset
        // ConnectedAt) is expressed with VALUES()/IF() inside the UPDATE assignment instead:
        // ConnectedAt only changes when the existing row's ConnectionId differs from the new one.
        const string sql = @"
            INSERT INTO `DeviceConnections` (`DeviceName`, `ConnectionId`, `InstanceId`, `ConnectedAt`)
            VALUES (@DeviceName, @ConnectionId, @InstanceId, @ConnectedAt)
            ON DUPLICATE KEY UPDATE
                `ConnectedAt` = IF(`ConnectionId` <> VALUES(`ConnectionId`), VALUES(`ConnectedAt`), `ConnectedAt`),
                `ConnectionId` = VALUES(`ConnectionId`),
                `InstanceId` = VALUES(`InstanceId`)";
        await conn.ExecuteAsync(sql, new
        {
            DeviceName = deviceName,
            ConnectionId = connectionId,
            InstanceId = AxonServerInstanceHeartbeat.InstanceId,
            ConnectedAt = DateTime.UtcNow.Ticks
        });
    });

    public Task Unregister(string connectionId) => MySqlExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        const string sql = "DELETE FROM `DeviceConnections` WHERE `ConnectionId` = @ConnectionId";
        await conn.ExecuteAsync(sql, new { ConnectionId = connectionId });
    });

    public Task<string?> GetConnectionId(string deviceName) => MySqlExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        const string sql = @"
            SELECT dc.`ConnectionId` FROM `DeviceConnections` dc
            INNER JOIN `ServerInstances` si ON si.`InstanceId` = dc.`InstanceId`
            WHERE dc.`DeviceName` = @DeviceName AND si.`LastSeenAt` >= @Cutoff";
        return await conn.QueryFirstOrDefaultAsync<string>(sql, new
        {
            DeviceName = deviceName,
            Cutoff = DateTime.UtcNow.Ticks - OfflineTimeoutTicks
        });
    });

    public Task<string?> GetDeviceName(string connectionId) => MySqlExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        const string sql = "SELECT `DeviceName` FROM `DeviceConnections` WHERE `ConnectionId` = @ConnectionId";
        return await conn.QueryFirstOrDefaultAsync<string>(sql, new { ConnectionId = connectionId });
    });

    public Task<List<ConnectedDevice>> GetAll() => MySqlExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        const string sql = @"
            SELECT dc.`DeviceName`, dc.`ConnectionId`, dc.`ConnectedAt` FROM `DeviceConnections` dc
            INNER JOIN `ServerInstances` si ON si.`InstanceId` = dc.`InstanceId`
            WHERE si.`LastSeenAt` >= @Cutoff
            ORDER BY dc.`ConnectedAt` DESC";
        return (await conn.QueryAsync<ConnectedDevice>(sql, new
        {
            Cutoff = DateTime.UtcNow.Ticks - OfflineTimeoutTicks
        })).ToList();
    });
}
