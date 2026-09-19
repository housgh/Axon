using Axon.Server.Interfaces;

namespace Axon.Server.Services;

internal class InMemoryAxonRecurringJobStore : IAxonRecurringJobStore
{
    private readonly Dictionary<string, RecurringJob> _recurringJobs = new();
    private readonly object _lock = new();

    public Task AddOrUpdate(RecurringJob recurringJob)
    {
        lock (_lock)
        {
            _recurringJobs[recurringJob.RecurringJobId] = recurringJob;
        }
        return Task.CompletedTask;
    }

    public Task<List<RecurringJob>> GetAll()
    {
        lock (_lock)
        {
            return Task.FromResult(_recurringJobs.Values.ToList());
        }
    }

    public Task UpdateNextRun(string recurringJobId, long nextRunAt, long lastRunAt)
    {
        lock (_lock)
        {
            if (_recurringJobs.TryGetValue(recurringJobId, out var job))
            {
                job.NextRunAt = nextRunAt;
                job.LastRunAt = lastRunAt;
            }
        }
        return Task.CompletedTask;
    }

    public Task Remove(string recurringJobId)
    {
        lock (_lock)
        {
            _recurringJobs.Remove(recurringJobId);
        }
        return Task.CompletedTask;
    }
}
