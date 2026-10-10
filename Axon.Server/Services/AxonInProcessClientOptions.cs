namespace Axon.Server.Services;

/// <summary>Options for <c>AxonServerBuilder.AddInProcessClient</c> (monolith mode).</summary>
public class AxonInProcessClientOptions
{
    /// <summary>
    /// How many jobs this process runs at once. Once reached, further due jobs stay
    /// Enqueued/Scheduled (unclaimed, so another instance can pick them up) until a slot frees.
    /// </summary>
    public int MaxConcurrentJobs { get; set; } = Environment.ProcessorCount * 5;

    /// <summary>
    /// The device name recorded on jobs enqueued from this process and shown in the dashboard's
    /// Clients tab. A label only - in monolith mode any instance runs any job, regardless of
    /// which instance enqueued it.
    /// </summary>
    public string DeviceName { get; set; } = Environment.MachineName;
}
