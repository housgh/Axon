using Axon.Core.Enums;

namespace Axon.Core.Models;

public class JobInfo
{
    public List<object?> Arguments { get; set; } = [];
    public string MethodName { get; set; } = null!;
    public string Assembly { get; set; } = null!;
    public string DeclaringType { get; set; } = null!;

    /// <summary>Overrides Axon.Server's default retry behavior for this job. Null uses the default.</summary>
    public AxonRetryPolicy? RetryPolicy { get; set; }

    /// <summary>
    /// Shifts this job's effective dispatch order - see <see cref="JobPriorityBoost"/> for the
    /// scoring formula. Not a hard tier: a higher-priority job only jumps ahead of an
    /// already-waiting lower-priority job if the wait-time gap between them is smaller than the
    /// boost gap between their priorities.
    /// </summary>
    public JobPriority Priority { get; set; } = JobPriority.Medium;

    /// <summary>
    /// The named queue this job is dispatched through. An <c>Axon.Server</c> instance only
    /// claims/dispatches jobs on queues it was registered for (see
    /// <c>AxonServerBuilder.AddQueues</c>) - a job on a queue no live instance serves simply sits
    /// waiting until one does. Defaults to <c>"default"</c>, which every instance serves
    /// implicitly even if <c>AddQueues</c> was never called.
    /// </summary>
    public string QueueName { get; set; } = "default";

    /// <summary>
    /// Groups jobs for the <see cref="MaxConcurrent"/> limit - e.g. "email-sender". Null means no
    /// concurrency limit is enforced for this job, regardless of <see cref="MaxConcurrent"/>.
    /// </summary>
    public string? ConcurrencyKey { get; set; }

    /// <summary>
    /// Maximum number of jobs sharing this <see cref="ConcurrencyKey"/> allowed to be Processing
    /// at once across the whole fleet. Ignored if <see cref="ConcurrencyKey"/> is null.
    /// </summary>
    public int? MaxConcurrent { get; set; }
}