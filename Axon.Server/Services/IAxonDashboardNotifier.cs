namespace Axon.Server.Services;

/// <summary>
/// Broadcasts coarse "this category changed, re-fetch" signals to connected dashboard viewers.
/// Deliberately not fine-grained (no per-job diffs) - it mirrors what polling already did: the
/// dashboard re-fetches the relevant list whenever it's told that list may have changed.
/// </summary>
public interface IAxonDashboardNotifier
{
    Task JobsChanged();
    Task RecurringJobsChanged();
    Task ServersChanged();
    Task ClientsChanged();
}
