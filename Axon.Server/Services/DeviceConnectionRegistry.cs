using System.Collections.Concurrent;

namespace Axon.Server.Services;

public class ConnectedDevice
{
    public string DeviceName { get; set; } = null!;
    public string ConnectionId { get; set; } = null!;
    public long ConnectedAt { get; set; }
}

public interface IDeviceConnectionRegistry
{
    Task Register(string deviceName, string connectionId);
    Task Unregister(string connectionId);
    Task<string?> GetConnectionId(string deviceName);
    Task<string?> GetDeviceName(string connectionId);

    /// <summary>
    /// Devices with an open SignalR connection. The in-memory implementation only ever sees
    /// connections accepted by this process (a SignalR connection is pinned to whichever instance
    /// accepted it); AxonSqlServerDeviceConnectionStore (via AddAxonSqlServerStore) instead
    /// publishes this across every instance, so this reflects the whole fleet's connected
    /// devices regardless of which instance handles the request.
    /// </summary>
    Task<List<ConnectedDevice>> GetAll();
}

/// <summary>
/// In-memory device registry: each process only ever sees devices connected directly to it,
/// since there's no shared storage to publish presence to other instances. True multi-instance
/// visibility requires AddAxonSqlServerStore (see AxonSqlServerDeviceConnectionStore).
/// </summary>
public class InMemoryDeviceConnectionRegistry : IDeviceConnectionRegistry
{
    private readonly ConcurrentDictionary<string, (string ConnectionId, long ConnectedAt)> _deviceToConnection = new();

    public Task Register(string deviceName, string connectionId)
    {
        // A re-Register (e.g. on SignalR auto-reconnect) with the same connection id shouldn't
        // reset ConnectedAt; only a genuinely new connection id counts as a fresh connection.
        _deviceToConnection.AddOrUpdate(
            deviceName,
            _ => (connectionId, DateTime.UtcNow.Ticks),
            (_, existing) => existing.ConnectionId == connectionId ? existing : (connectionId, DateTime.UtcNow.Ticks));
        return Task.CompletedTask;
    }

    public Task Unregister(string connectionId)
    {
        foreach (var pair in _deviceToConnection)
        {
            if (pair.Value.ConnectionId == connectionId)
            {
                _deviceToConnection.TryRemove(pair.Key, out _);
            }
        }
        return Task.CompletedTask;
    }

    public Task<string?> GetConnectionId(string deviceName)
    {
        return Task.FromResult(_deviceToConnection.TryGetValue(deviceName, out var entry) ? entry.ConnectionId : null);
    }

    public Task<string?> GetDeviceName(string connectionId)
    {
        foreach (var pair in _deviceToConnection)
        {
            if (pair.Value.ConnectionId == connectionId)
            {
                return Task.FromResult<string?>(pair.Key);
            }
        }
        return Task.FromResult<string?>(null);
    }

    public Task<List<ConnectedDevice>> GetAll()
    {
        return Task.FromResult(_deviceToConnection
            .Select(pair => new ConnectedDevice
            {
                DeviceName = pair.Key,
                ConnectionId = pair.Value.ConnectionId,
                ConnectedAt = pair.Value.ConnectedAt
            })
            .OrderByDescending(d => d.ConnectedAt)
            .ToList());
    }
}
