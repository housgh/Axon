using Axon.Server.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Axon.Server.Services;

/// <summary>
/// Opt-in (see <c>AddJobCleanup</c>) background sweep that purges old completed jobs
/// (Succeeded/Failed/Skipped) and their history, so the Jobs/JobHistory tables don't grow
/// unbounded in a long-running deployment. Off by default - existing installs keep every job
/// forever unless this is explicitly enabled.
/// </summary>
public class AxonJobCleanupProcessor(
    IAxonJobStore jobStore,
    AxonJobCleanupOptions options,
    ILogger<AxonJobCleanupProcessor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var cutoff = DateTime.UtcNow.Subtract(options.Retention).Ticks;
                var deleted = await jobStore.DeleteCompletedJobsOlderThan(cutoff);
                if (deleted > 0)
                {
                    logger.LogInformation("Job cleanup deleted {Count} completed job(s) older than {Retention}", deleted, options.Retention);
                }
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Job cleanup sweep failed; will retry next interval");
            }

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
}
