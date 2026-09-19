using Axon.Core.Enums;
using Axon.Core.Helpers;
using Axon.Server.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Axon.Server.Services;

public class AxonRecurringJobProcessor(
    IAxonRecurringJobStore recurringJobStore,
    IAxonJobStore jobStore,
    IAxonDashboardNotifier notifier,
    ILogger<AxonRecurringJobProcessor> logger) : BackgroundService
{
    private const int PollInterval = 15000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            var recurringJobs = await recurringJobStore.GetAll();

            foreach (var recurringJob in recurringJobs.Where(r => r.NextRunAt <= now.UtcTicks))
            {
                await jobStore.AddJob(new Job(recurringJob)
                {
                    JobId = Guid.NewGuid().ToString(),
                    DeviceName = recurringJob.DeviceName,
                    State = JobState.Enqueued
                });
                await notifier.JobsChanged();

                try
                {
                    var cron = CronExpression.Parse(recurringJob.CronExpression);
                    var nextRunAt = cron.GetNextOccurrence(now).UtcTicks;
                    await recurringJobStore.UpdateNextRun(recurringJob.RecurringJobId, nextRunAt, now.UtcTicks);
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Failed to compute next occurrence for recurring job {RecurringJobId}; removing it", recurringJob.RecurringJobId);
                    await recurringJobStore.Remove(recurringJob.RecurringJobId);
                }
                await notifier.RecurringJobsChanged();
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }
}
