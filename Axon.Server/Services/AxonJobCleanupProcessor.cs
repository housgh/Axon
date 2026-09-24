using Axon.Server.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Axon.Server.Services;

/// <summary>
/// On-by-default (see <c>AddAxonServer</c>/<c>AddJobCleanup</c>) background sweep that purges
/// Succeeded jobs older than the configured retention, and their history, so the Jobs/JobHistory
/// tables don't grow unbounded in a long-running deployment. Failed and Skipped jobs are never
/// touched, regardless of age.
/// </summary>
public class AxonJobCleanupProcessor(
    IAxonJobStore jobStore,
    AxonJobCleanupOptions options,
    IAxonDashboardNotifier notifier,
    ILogger<AxonJobCleanupProcessor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunSweepAsync();

            try
            {
                await Task.Delay(options.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }
    }

    // Split out from ExecuteAsync's loop so a single sweep can be exercised directly in tests,
    // matching the pattern AxonJobProcessor/AxonRecurringJobProcessor use for their per-cycle logic.
    private async Task RunSweepAsync()
    {
        try
        {
            var cutoff = DateTime.UtcNow.Subtract(options.Retention).Ticks;
            var deleted = await jobStore.DeleteCompletedJobsOlderThan(cutoff);
            if (deleted > 0)
            {
                logger.LogInformation("Job cleanup deleted {Count} completed job(s) older than {Retention}", deleted, options.Retention);
                await notifier.JobsChanged();
            }
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Job cleanup sweep failed; will retry next interval");
        }
    }
}
