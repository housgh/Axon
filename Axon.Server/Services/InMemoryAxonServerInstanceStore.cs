using Axon.Server.Interfaces;

namespace Axon.Server.Services;

/// <summary>
/// In-memory instance registry: each process only ever sees itself, since there's no shared
/// storage to publish presence to other instances. True multi-instance visibility requires
/// AddAxonSqlServerStore (see AxonSqlServerInstanceStore).
/// </summary>
public class InMemoryAxonServerInstanceStore : IAxonServerInstanceStore
{
    private ServerInstance? _self;
    private readonly object _lock = new();

    public Task Heartbeat(ServerInstance instance)
    {
        lock (_lock)
        {
            _self = instance;
        }
        return Task.CompletedTask;
    }

    public Task<List<ServerInstance>> GetAll()
    {
        lock (_lock)
        {
            return Task.FromResult(_self is null ? [] : new List<ServerInstance> { _self });
        }
    }

    public Task Remove(string instanceId)
    {
        lock (_lock)
        {
            if (_self?.InstanceId == instanceId)
                _self = null;
        }
        return Task.CompletedTask;
    }
}
