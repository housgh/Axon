using Axon.Server.Interfaces;
using Axon.Server.Services;
using Dapper;
using Microsoft.Data.SqlClient;
// ReSharper disable UseRawString

namespace Axon.SqlServer;

/// <summary>
/// Publishes this instance's device connections to a shared table, so the dashboard's Clients
/// tab reflects every instance's connections rather than only whichever instance answers a given
/// request. A stale row - left behind if an instance died without a clean SignalR disconnect
/// (OnDisconnectedAsync never running to call Unregister) - is filtered out by joining against
/// ServerInstances and checking the owning instance's heartbeat, the same staleness check the
/// Servers tab already uses for IsOnline.
/// </summary>
public class AxonSqlServerDeviceConnectionStore(string connectionString) : IDeviceConnectionRegistry
{
    // Matches DependencyInjection.ServerInstanceOfflineTimeout: an instance (and therefore its
    // published connections) is considered gone once its heartbeat is older than this.
    private static readonly long OfflineTimeoutTicks = TimeSpan.FromSeconds(45).Ticks;

    private SqlConnection CreateConnection() =>
        new(connectionString);

    public Task Register(string deviceName, string connectionId) => SqlExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        const string sql = @"
            MERGE DeviceConnections AS target
            USING (SELECT @DeviceName AS DeviceName) AS source
            ON target.DeviceName = source.DeviceName
            WHEN MATCHED AND target.ConnectionId <> @ConnectionId THEN
                UPDATE SET ConnectionId = @ConnectionId, InstanceId = @InstanceId, ConnectedAt = @ConnectedAt
            WHEN NOT MATCHED THEN
                INSERT (DeviceName, ConnectionId, InstanceId, ConnectedAt)
                VALUES (@DeviceName, @ConnectionId, @InstanceId, @ConnectedAt);";
        // A re-Register (e.g. SignalR auto-reconnect) with the same connection id is a no-op
        // (the MATCHED AND clause skips it), so ConnectedAt only resets on a genuinely new
        // connection id - mirroring InMemoryDeviceConnectionRegistry's AddOrUpdate semantics.
        await conn.ExecuteAsync(sql, new
        {
            DeviceName = deviceName,
            ConnectionId = connectionId,
            InstanceId = AxonServerInstanceHeartbeat.InstanceId,
            ConnectedAt = DateTime.UtcNow.Ticks
        });
    });

    public Task Unregister(string connectionId) => SqlExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        const string sql = "DELETE FROM DeviceConnections WHERE ConnectionId = @ConnectionId";
        await conn.ExecuteAsync(sql, new { ConnectionId = connectionId });
    });

    public Task<string?> GetConnectionId(string deviceName) => SqlExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        const string sql = @"
            SELECT dc.ConnectionId FROM DeviceConnections dc
            INNER JOIN ServerInstances si ON si.InstanceId = dc.InstanceId
            WHERE dc.DeviceName = @DeviceName AND si.LastSeenAt >= @Cutoff";
        return await conn.QueryFirstOrDefaultAsync<string>(sql, new
        {
            DeviceName = deviceName,
            Cutoff = DateTime.UtcNow.Ticks - OfflineTimeoutTicks
        });
    });

    public Task<string?> GetDeviceName(string connectionId) => SqlExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        const string sql = "SELECT DeviceName FROM DeviceConnections WHERE ConnectionId = @ConnectionId";
        return await conn.QueryFirstOrDefaultAsync<string>(sql, new { ConnectionId = connectionId });
    });

    public Task<List<ConnectedDevice>> GetAll() => SqlExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        const string sql = @"
            SELECT dc.DeviceName, dc.ConnectionId, dc.ConnectedAt FROM DeviceConnections dc
            INNER JOIN ServerInstances si ON si.InstanceId = dc.InstanceId
            WHERE si.LastSeenAt >= @Cutoff
            ORDER BY dc.ConnectedAt DESC";
        return (await conn.QueryAsync<ConnectedDevice>(sql, new
        {
            Cutoff = DateTime.UtcNow.Ticks - OfflineTimeoutTicks
        })).ToList();
    });
}
