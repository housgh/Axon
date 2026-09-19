using Axon.Server.Services;

namespace Axon.Server.Interfaces;

public interface IAxonServerInstanceStore
{
    /// <summary>Upserts this instance's row with the current time as LastSeenAt.</summary>
    Task Heartbeat(ServerInstance instance);

    Task<List<ServerInstance>> GetAll();

    /// <summary>Removes this instance's row (called on graceful shutdown).</summary>
    Task Remove(string instanceId);
}
