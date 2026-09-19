using Microsoft.Extensions.Logging;

namespace Axon.Server.Services;

/// <summary>
/// A marker type used only as the ILogger&lt;T&gt; category for audit events - keeps them under a
/// distinct, filterable logger name ("Axon.Server.Services.AxonAuditLog") separate from each
/// endpoint handler's own category, so audit events can be routed/retained differently (e.g.
/// sent to a separate sink) without touching application logging configuration.
/// </summary>
public sealed class AxonAuditLog
{
    public static void LoginSucceeded(ILogger logger, string username, string? remoteIp) =>
        logger.LogInformation("Dashboard login succeeded for {Username} from {RemoteIp}", username, remoteIp ?? "unknown");

    public static void LoginFailed(ILogger logger, string? username, string? remoteIp) =>
        logger.LogWarning("Dashboard login failed for {Username} from {RemoteIp}", username ?? "(empty)", remoteIp ?? "unknown");

    public static void JobDeleted(ILogger logger, string username, string jobId) =>
        logger.LogInformation("{Username} deleted job {JobId}", username, jobId);

    public static void JobRetried(ILogger logger, string username, string jobId) =>
        logger.LogInformation("{Username} retried job {JobId}", username, jobId);

    public static void RecurringJobDeleted(ILogger logger, string username, string recurringJobId) =>
        logger.LogInformation("{Username} deleted recurring job {RecurringJobId}", username, recurringJobId);

    public static void RecurringJobTriggered(ILogger logger, string username, string recurringJobId, string jobId) =>
        logger.LogInformation("{Username} manually triggered recurring job {RecurringJobId} (new job {JobId})", username, recurringJobId, jobId);

    public static void RecurringJobPaused(ILogger logger, string username, string recurringJobId) =>
        logger.LogInformation("{Username} paused recurring job {RecurringJobId}", username, recurringJobId);

    public static void RecurringJobResumed(ILogger logger, string username, string recurringJobId) =>
        logger.LogInformation("{Username} resumed recurring job {RecurringJobId}", username, recurringJobId);

    public static void RecurringJobNextSkipped(ILogger logger, string username, string recurringJobId) =>
        logger.LogInformation("{Username} skipped the next occurrence of recurring job {RecurringJobId}", username, recurringJobId);
}
