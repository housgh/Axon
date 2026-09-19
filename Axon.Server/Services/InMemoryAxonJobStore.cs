using Axon.Core.Enums;
using Axon.Server.Interfaces;

namespace Axon.Server.Services;

internal class InMemoryAxonJobStore : IAxonJobStore
{
    private readonly List<Job> _jobs = [];
    private readonly List<JobHistoryEntry> _history = [];
    private readonly object _lock = new();

    private void AppendHistory(string jobId, JobState state, string? note)
    {
        _history.Add(new JobHistoryEntry
        {
            JobId = jobId,
            State = state,
            Timestamp = DateTime.UtcNow.Ticks,
            Note = note
        });
    }

    public Task AddJob(Job job)
    {
        lock (_lock)
        {
            _jobs.Add(job);
            AppendHistory(job.JobId, job.State, null);
        }
        return Task.CompletedTask;
    }

    public Task<Job?> GetJob(string id)
    {
        lock (_lock)
        {
            return Task.FromResult(_jobs.FirstOrDefault(j => j.JobId == id));
        }
    }

    public Task<List<Job>> GetJobs(int skip = 0, int take = 20, JobState[]? states = null)
    {
        lock (_lock)
        {
            var query = states is { Length: > 0 }
                ? _jobs.Where(j => states.Contains(j.State))
                : _jobs;

            return Task.FromResult(query.Skip(skip).Take(take).ToList());
        }
    }

    public Task UpdateState(string id, JobState state, string? note = null)
    {
        lock (_lock)
        {
            var job = _jobs.FirstOrDefault(j => j.JobId == id);
            if (job is not null)
            {
                job.State = state;
                AppendHistory(id, state, note);
            }
        }
        return Task.CompletedTask;
    }

    public Task RequeueForRetry(string id, long scheduledFor, string? note = null)
    {
        lock (_lock)
        {
            var job = _jobs.FirstOrDefault(j => j.JobId == id);
            if (job is not null)
            {
                job.Attempts++;
                job.ScheduledFor = scheduledFor;
                job.State = JobState.Scheduled;
                AppendHistory(id, JobState.Scheduled, note);
            }
        }
        return Task.CompletedTask;
    }

    public Task Requeue(string id, string note = "Requeued manually")
    {
        lock (_lock)
        {
            var job = _jobs.FirstOrDefault(j => j.JobId == id);
            if (job is not null)
            {
                job.Attempts = 0;
                job.ScheduledFor = null;
                job.State = JobState.Enqueued;
                AppendHistory(id, JobState.Enqueued, note);
            }
        }
        return Task.CompletedTask;
    }

    public Task DeleteJob(string id)
    {
        lock (_lock)
        {
            _jobs.RemoveAll(j => j.JobId == id);
            _history.RemoveAll(h => h.JobId == id);
        }
        return Task.CompletedTask;
    }

    public Task RecordFailure(string id, string? error)
    {
        lock (_lock)
        {
            AppendHistory(id, JobState.Failed, error);
        }
        return Task.CompletedTask;
    }

    public Task<bool> TryClaimJob(string id, long processingDeadline, string? note = null)
    {
        lock (_lock)
        {
            var job = _jobs.FirstOrDefault(j => j.JobId == id);
            if (job is null || (job.State != JobState.Enqueued && job.State != JobState.Scheduled))
            {
                return Task.FromResult(false);
            }

            // Concurrency-limit check happens under the same lock as the claim itself, so it's
            // atomic against other concurrent TryClaimJob calls for the same ConcurrencyKey -
            // the same race-safety requirement as the State IN (...) claim guard above.
            if (job.ConcurrencyKey is not null && job.MaxConcurrent is { } maxConcurrent)
            {
                var currentlyProcessing = _jobs.Count(j => j.ConcurrencyKey == job.ConcurrencyKey && j.State == JobState.Processing);
                if (currentlyProcessing >= maxConcurrent)
                {
                    return Task.FromResult(false);
                }
            }

            job.State = JobState.Processing;
            job.ProcessingDeadline = processingDeadline;
            AppendHistory(id, JobState.Processing, note);
            return Task.FromResult(true);
        }
    }

    public Task<List<Job>> GetOrphanedProcessingJobs(long asOf)
    {
        lock (_lock)
        {
            return Task.FromResult(_jobs
                .Where(j => j.State == JobState.Processing && j.ProcessingDeadline is not null && j.ProcessingDeadline < asOf)
                .ToList());
        }
    }

    public Task<List<Job>> GetProcessingJobsForDevice(string deviceName)
    {
        lock (_lock)
        {
            return Task.FromResult(_jobs
                .Where(j => j.State == JobState.Processing && j.DeviceName == deviceName)
                .ToList());
        }
    }

    public Task<List<Job>> GetContinuationsWaitingOn(string parentJobId)
    {
        lock (_lock)
        {
            return Task.FromResult(_jobs
                .Where(j => j.State == JobState.AwaitingParent && j.ParentJobId == parentJobId)
                .ToList());
        }
    }

    public Task<List<JobHistoryEntry>> GetHistory(string jobId)
    {
        lock (_lock)
        {
            return Task.FromResult(_history
                .Where(h => h.JobId == jobId)
                .OrderBy(h => h.Timestamp)
                .ToList());
        }
    }

    private static readonly JobState[] TerminalStates = [JobState.Succeeded, JobState.Failed, JobState.Skipped];

    public Task<int> DeleteCompletedJobsOlderThan(long cutoff)
    {
        lock (_lock)
        {
            var toDelete = _jobs
                .Where(j => TerminalStates.Contains(j.State))
                .Where(j =>
                {
                    var lastHistoryTimestamp = _history.Where(h => h.JobId == j.JobId).Select(h => h.Timestamp).DefaultIfEmpty(0).Max();
                    return lastHistoryTimestamp < cutoff;
                })
                .Select(j => j.JobId)
                .ToList();

            _jobs.RemoveAll(j => toDelete.Contains(j.JobId));
            _history.RemoveAll(h => toDelete.Contains(h.JobId));

            return Task.FromResult(toDelete.Count);
        }
    }
}
