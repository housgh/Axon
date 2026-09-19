using System.Collections.Concurrent;

namespace Axon.Server.Services;

/// <summary>
/// Per-username failed-login lockout, complementing the IP-keyed rate limiter middleware
/// (DependencyInjection.LoginRateLimitPolicy). The ASP.NET Core rate limiter picks a partition
/// key before the request body is read, so it can only key on IP cheaply; this tracker covers
/// the other axis - a distributed attempt against one specific username from many different IPs.
/// </summary>
internal class LoginAttemptTracker
{
    private const int MaxFailuresBeforeLockout = 5;
    private static readonly TimeSpan LockoutWindow = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, (int Failures, long WindowStartTicks)> _failuresByUsername = new();

    /// <summary>True if this username has exceeded the failure threshold within the current window.</summary>
    public bool IsLockedOut(string username)
    {
        if (!_failuresByUsername.TryGetValue(username, out var entry)) return false;

        if (DateTime.UtcNow.Ticks - entry.WindowStartTicks > LockoutWindow.Ticks)
        {
            return false; // window has expired; RecordFailure/RecordSuccess will reset it on next use
        }

        return entry.Failures >= MaxFailuresBeforeLockout;
    }

    public void RecordFailure(string username)
    {
        _failuresByUsername.AddOrUpdate(
            username,
            _ => (1, DateTime.UtcNow.Ticks),
            (_, existing) =>
            {
                var windowExpired = DateTime.UtcNow.Ticks - existing.WindowStartTicks > LockoutWindow.Ticks;
                return windowExpired ? (1, DateTime.UtcNow.Ticks) : (existing.Failures + 1, existing.WindowStartTicks);
            });
    }

    public void RecordSuccess(string username)
    {
        _failuresByUsername.TryRemove(username, out _);
    }
}
