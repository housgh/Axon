namespace Axon.Core.Enums;

/// <summary>
/// Virtual-age bonus (in ticks) subtracted from a job's effective dispatch score
/// (<c>COALESCE(ScheduledFor, EnqueuedAt) - Boost</c> - see docs/architecture.md#job-priority).
/// A higher-priority job only jumps ahead of a lower-priority one if the lower-priority job's
/// actual age is less than the boost gap between them; a sufficiently old low-priority job still
/// wins, by design.
/// <para/>
/// This is the single source of truth for the boost values - every store backend's ORDER BY (or
/// aggregation pipeline, for MongoDB) embeds these same tick counts as literals, since SQL/BSON
/// query text can't reference a .NET dictionary directly. If these values change, every store's
/// query text must be updated to match.
/// </summary>
public static class JobPriorityBoost
{
    public static readonly IReadOnlyDictionary<JobPriority, long> Ticks = new Dictionary<JobPriority, long>
    {
        [JobPriority.Low] = 0,
        [JobPriority.Medium] = TimeSpan.FromMinutes(5).Ticks,
        [JobPriority.High] = TimeSpan.FromMinutes(15).Ticks,
        [JobPriority.Critical] = TimeSpan.FromHours(1).Ticks,
    };
}
