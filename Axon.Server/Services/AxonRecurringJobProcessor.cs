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
            await TriggerDueRecurringJobsAsync();
            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task TriggerDueRecurringJobsAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var recurringJobs = await recurringJobStore.GetAll(take: int.MaxValue);

        foreach (var recurringJob in recurringJobs.Where(r => !r.IsPaused && r.NextRunAt <= now.UtcTicks))
        {
            using var activity = AxonInstrumentation.ActivitySource.StartActivity("axon.recurring_job.trigger");
            activity?.SetTag("axon.recurring_job_id", recurringJob.RecurringJobId);

            await jobStore.AddJob(new Job(recurringJob)
            {
                JobId = Guid.NewGuid().ToString(),
                DeviceName = recurringJob.DeviceName,
                State = JobState.Enqueued,
                EnqueuedAt = now.UtcTicks
            });
            AxonInstrumentation.RecurringJobsTriggered.Add(1);
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
    }
}
