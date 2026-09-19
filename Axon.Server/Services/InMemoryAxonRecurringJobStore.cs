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
            // IsPaused is deliberately carried over from any existing row rather than taken from
            // the incoming recurringJob (which always has IsPaused = false, freshly constructed
            // by AxonRecurringJobService) - see the matching comment in
            // AxonSqlServerRecurringJobStore.AddOrUpdate for why a client re-registering its
            // recurring jobs on startup must not silently undo an operator's pause.
            if (_recurringJobs.TryGetValue(recurringJob.RecurringJobId, out var existing))
            {
                recurringJob.IsPaused = existing.IsPaused;
            }
            _recurringJobs[recurringJob.RecurringJobId] = recurringJob;
        }
        return Task.CompletedTask;
    }

    public Task<RecurringJob?> GetById(string recurringJobId)
    {
        lock (_lock)
        {
            return Task.FromResult(_recurringJobs.GetValueOrDefault(recurringJobId));
        }
    }

    public Task<List<RecurringJob>> GetAll(int skip = 0, int take = 20)
    {
        lock (_lock)
        {
            return Task.FromResult(_recurringJobs.Values
                .OrderBy(r => r.NextRunAt)
                .Skip(skip)
                .Take(take)
                .ToList());
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

    public Task SetPaused(string recurringJobId, bool isPaused)
    {
        lock (_lock)
        {
            if (_recurringJobs.TryGetValue(recurringJobId, out var job))
            {
                job.IsPaused = isPaused;
            }
        }
        return Task.CompletedTask;
    }

    public Task SkipNext(string recurringJobId, long newNextRunAt)
    {
        lock (_lock)
        {
            if (_recurringJobs.TryGetValue(recurringJobId, out var job))
            {
                job.NextRunAt = newNextRunAt;
            }
        }
        return Task.CompletedTask;
    }
}
