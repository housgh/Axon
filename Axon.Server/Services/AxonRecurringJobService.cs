using Axon.Core.Helpers;
using Axon.Core.Models;
using Axon.Server.Interfaces;

namespace Axon.Server.Services;

public interface IAxonRecurringJobService
{
    Task AddOrUpdateAsync(string deviceName, string recurringJobId, JobInfo jobInfo, string cronExpression);
    Task RemoveAsync(string recurringJobId);
}

public class AxonRecurringJobService(IAxonRecurringJobStore recurringJobStore, IAxonDashboardNotifier notifier) : IAxonRecurringJobService
{
    public async Task AddOrUpdateAsync(string deviceName, string recurringJobId, JobInfo jobInfo, string cronExpression)
    {
        var cron = CronExpression.Parse(cronExpression); // validates the expression up front
        var nextRunAt = cron.GetNextOccurrence(DateTimeOffset.UtcNow).UtcTicks;

        await recurringJobStore.AddOrUpdate(new RecurringJob(jobInfo)
        {
            RecurringJobId = recurringJobId,
            DeviceName = deviceName,
            CronExpression = cronExpression,
            NextRunAt = nextRunAt
        });
        await notifier.RecurringJobsChanged();
    }

    public async Task RemoveAsync(string recurringJobId)
    {
        await recurringJobStore.Remove(recurringJobId);
        await notifier.RecurringJobsChanged();
    }
}
