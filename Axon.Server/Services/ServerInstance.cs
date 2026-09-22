namespace Axon.Server.Services;

public class ServerInstance
{
    public string InstanceId { get; set; } = null!;
    public string MachineName { get; set; } = null!;
    public long StartedAt { get; set; }
    public long LastSeenAt { get; set; }

    /// <summary>
    /// Comma-joined list of the queues this instance serves (see
    /// <c>AxonServerBuilder.AddQueues</c>) - e.g. <c>"default,billing"</c>. Always includes
    /// <c>"default"</c>. Refreshed on every heartbeat, so a restart with a different
    /// <c>AddQueues</c> call updates this rather than leaving a stale value.
    /// </summary>
    public string ServedQueues { get; set; } = "default";
}
