using System.Collections.Concurrent;

namespace Axon.Server.Services;

public interface IDeviceConnectionRegistry
{
    void Register(string deviceName, string connectionId);
    void Unregister(string connectionId);
    string? GetConnectionId(string deviceName);
    string? GetDeviceName(string connectionId);
}

public class DeviceConnectionRegistry : IDeviceConnectionRegistry
{
    private readonly ConcurrentDictionary<string, string> _deviceToConnection = new();

    public void Register(string deviceName, string connectionId)
    {
        _deviceToConnection[deviceName] = connectionId;
    }

    public void Unregister(string connectionId)
    {
        foreach (var pair in _deviceToConnection)
        {
            if (pair.Value == connectionId)
            {
                _deviceToConnection.TryRemove(pair.Key, out _);
            }
        }
    }

    public string? GetConnectionId(string deviceName)
    {
        return _deviceToConnection.GetValueOrDefault(deviceName);
    }

    public string? GetDeviceName(string connectionId)
    {
        foreach (var pair in _deviceToConnection)
        {
            if (pair.Value == connectionId)
            {
                return pair.Key;
            }
        }
        return null;
    }
}
