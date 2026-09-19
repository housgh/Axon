namespace Axon.Core.Models;

/// <summary>
/// Per-job retry configuration. When null on a job, Axon.Server falls back to its built-in
/// default (3 attempts, 10s/30s/2min backoff).
/// </summary>
public class AxonRetryPolicy
{
    /// <summary>Total attempts allowed, including the first. Must be at least 1.</summary>
    public int MaxAttempts { get; set; }

    /// <summary>
    /// Delay before each retry, in seconds, indexed by attempt number (0-based). If there are
    /// more retries than entries, the last entry is reused for every subsequent retry - matching
    /// the behavior of Axon.Server's built-in default backoff array.
    /// </summary>
    public List<int> RetryDelaysSeconds { get; set; } = [];
}
