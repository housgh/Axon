namespace Axon.Server.Services;

public class ServerInstance
{
    public string InstanceId { get; set; } = null!;
    public string MachineName { get; set; } = null!;
    public long StartedAt { get; set; }
    public long LastSeenAt { get; set; }
}
