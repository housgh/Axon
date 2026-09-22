using Axon.Core.Enums;

namespace Axon.Core.Models;

/// <summary>
/// Optional per-call settings for <c>IAxonClient.EnqueueAsync</c>/<c>ScheduleAsync</c>/
/// <c>ContinueWithAsync</c>. Bundled into one object (rather than one parameter per setting) so
/// adding a new option later doesn't grow every overload's parameter list.
/// </summary>
public class AxonEnqueueOptions
{
    /// <summary>Overrides Axon.Server's default retry/backoff schedule for this job only. Null uses the default.</summary>
    public AxonRetryPolicy? RetryPolicy { get; set; }

    /// <summary>
    /// Shifts this job's effective dispatch order - see <see cref="JobPriorityBoost"/> for the
    /// scoring formula. Defaults to <see cref="JobPriority.Medium"/>.
    /// </summary>
    public JobPriority Priority { get; set; } = JobPriority.Medium;

    /// <summary>
    /// The named queue to dispatch this job through - see <c>AxonServerBuilder.AddQueues</c>.
    /// Null (the default) resolves to <c>"default"</c>, which every <c>Axon.Server</c> instance
    /// serves even if it never called <c>AddQueues</c>.
    /// </summary>
    public string? QueueName { get; set; }

    /// <summary>
    /// Groups this job for the <see cref="MaxConcurrent"/> limit - e.g. "email-sender". An
    /// explicit value here always overrides an <c>AxonConcurrencyLimitAttribute</c> on the
    /// method, if both are present. Null (the default) falls back to that attribute, if any.
    /// </summary>
    public string? ConcurrencyKey { get; set; }

    /// <summary>
    /// Maximum number of jobs sharing <see cref="ConcurrencyKey"/> allowed to be Processing at
    /// once across the whole fleet (enforced atomically by the job store, not just locally).
    /// Ignored if <see cref="ConcurrencyKey"/> ends up null.
    /// </summary>
    public int? MaxConcurrent { get; set; }

    /// <summary>Cancels the enqueue/schedule request itself - has no effect once the server has durably stored the job.</summary>
    public CancellationToken CancellationToken { get; set; }
}
