using Axon.Core.Enums;
using Axon.Server.Interfaces;

namespace Axon.Server.Services;

internal class InMemoryAxonJobStore : IAxonJobStore
{
    private readonly List<Job> _jobs = [];
    private readonly List<JobHistoryEntry> _history = [];

    // Mirrors the SQL/Mongo backends' IsDeleted column/field: DeleteJob and
    // DeleteCompletedJobsOlderThan soft-delete (the Jobs row stays, so CountJobsByState can still
    // see it) rather than removing from _jobs outright - otherwise this store's lifetime stats
    // would go down after cleanup while every real backend's wouldn't.
    private readonly HashSet<string> _deletedJobIds = [];
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
            return Task.FromResult(_jobs.FirstOrDefault(j => j.JobId == id && !_deletedJobIds.Contains(j.JobId)));
        }
    }

    public Task<List<Job>> GetJobs(int skip = 0, int take = 20, JobState[]? states = null)
    {
        lock (_lock)
        {
            var query = states is { Length: > 0 }
                ? _jobs.Where(j => states.Contains(j.State))
                : _jobs.AsEnumerable();
            query = query.Where(j => !_deletedJobIds.Contains(j.JobId));

            // Effective dispatch score: COALESCE(ScheduledFor, EnqueuedAt) - Boost[Priority] -
            // see JobPriorityBoost for why this shape lets a sufficiently old low-priority job
            // still win over a recent high-priority one.
            var ordered = query.OrderBy(j => (j.ScheduledFor ?? j.EnqueuedAt) - JobPriorityBoost.Ticks[j.Priority]);

            return Task.FromResult(ordered.Skip(skip).Take(take).ToList());
        }
    }

    public Task UpdateState(string id, JobState state, string? note = null)
    {
        lock (_lock)
        {
            var job = _jobs.FirstOrDefault(j => j.JobId == id && !_deletedJobIds.Contains(j.JobId));
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
            var job = _jobs.FirstOrDefault(j => j.JobId == id && !_deletedJobIds.Contains(j.JobId));
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
            var job = _jobs.FirstOrDefault(j => j.JobId == id && !_deletedJobIds.Contains(j.JobId));
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
            // Soft delete, same as the SQL/Mongo backends: the Jobs row and its State stay, only
            // marked deleted, so CountJobsByState's lifetime tally is unaffected. History is left
            // alone too - matches DeleteJob (manual single-job delete) on every other backend,
            // which only ever touches the Jobs row.
            _deletedJobIds.Add(id);
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
            var job = _jobs.FirstOrDefault(j => j.JobId == id && !_deletedJobIds.Contains(j.JobId));
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

    public Task<int> DeleteCompletedJobsOlderThan(long cutoff)
    {
        lock (_lock)
        {
            var toDelete = _jobs
                .Where(j => j.State == JobState.Succeeded && !_deletedJobIds.Contains(j.JobId))
                .Where(j =>
                {
                    var lastHistoryTimestamp = _history.Where(h => h.JobId == j.JobId).Select(h => h.Timestamp).DefaultIfEmpty(0).Max();
                    return lastHistoryTimestamp < cutoff;
                })
                .Select(j => j.JobId)
                .ToList();

            foreach (var jobId in toDelete)
            {
                _deletedJobIds.Add(jobId);
            }
            _history.RemoveAll(h => toDelete.Contains(h.JobId));

            return Task.FromResult(toDelete.Count);
        }
    }

    public Task<Dictionary<JobState, int>> CountJobsByState()
    {
        lock (_lock)
        {
            // Soft-deleted jobs still count here (unlike everywhere else in this store) so the
            // lifetime Succeeded/Failed/Skipped tally never drops when cleanup runs - see the
            // IAxonJobStore.CountJobsByState doc comment.
            return Task.FromResult(_jobs
                .GroupBy(j => j.State)
                .ToDictionary(g => g.Key, g => g.Count()));
        }
    }
}
