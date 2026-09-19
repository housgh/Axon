namespace Axon.Core;

/// <summary>
/// Declares a default concurrency limit for a job method, read via reflection by Axon.Client at
/// enqueue time. An explicit <c>concurrencyKey</c>/<c>maxConcurrent</c> argument passed to
/// <c>EnqueueAsync</c>/<c>ScheduleAsync</c> takes precedence over this attribute when both are
/// present.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class AxonConcurrencyLimitAttribute(string concurrencyKey, int maxConcurrent) : Attribute
{
    public string ConcurrencyKey { get; } = concurrencyKey;
    public int MaxConcurrent { get; } = maxConcurrent;
}
