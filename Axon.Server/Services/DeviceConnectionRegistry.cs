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
    void Register(string deviceName, string connectionId);
    void Unregister(string connectionId);
    string? GetConnectionId(string deviceName);
    string? GetDeviceName(string connectionId);

    /// <summary>
    /// Devices with an open SignalR connection to this specific server instance. A SignalR
    /// connection is pinned to whichever instance accepted it, so this never reflects clients
    /// connected to other instances in a multi-instance deployment.
    /// </summary>
    List<ConnectedDevice> GetAll();
}

public class DeviceConnectionRegistry : IDeviceConnectionRegistry
{
    private readonly ConcurrentDictionary<string, (string ConnectionId, long ConnectedAt)> _deviceToConnection = new();

    public void Register(string deviceName, string connectionId)
    {
        // A re-Register (e.g. on SignalR auto-reconnect) with the same connection id shouldn't
        // reset ConnectedAt; only a genuinely new connection id counts as a fresh connection.
        _deviceToConnection.AddOrUpdate(
            deviceName,
            _ => (connectionId, DateTime.UtcNow.Ticks),
            (_, existing) => existing.ConnectionId == connectionId ? existing : (connectionId, DateTime.UtcNow.Ticks));
    }

    public void Unregister(string connectionId)
    {
        foreach (var pair in _deviceToConnection)
        {
            if (pair.Value.ConnectionId == connectionId)
            {
                _deviceToConnection.TryRemove(pair.Key, out _);
            }
        }
    }

    public string? GetConnectionId(string deviceName)
    {
        return _deviceToConnection.TryGetValue(deviceName, out var entry) ? entry.ConnectionId : null;
    }

    public string? GetDeviceName(string connectionId)
    {
        foreach (var pair in _deviceToConnection)
        {
            if (pair.Value.ConnectionId == connectionId)
            {
                return pair.Key;
            }
        }
        return null;
    }

    public List<ConnectedDevice> GetAll()
    {
        return _deviceToConnection
            .Select(pair => new ConnectedDevice
            {
                DeviceName = pair.Key,
                ConnectionId = pair.Value.ConnectionId,
                ConnectedAt = pair.Value.ConnectedAt
            })
            .OrderByDescending(d => d.ConnectedAt)
            .ToList();
    }
}
