using Axon.Core.Enums;
using Axon.Server.Services;

namespace Axon.Server.Interfaces;

public interface IAxonJobStore
{
    Task AddJob(Job job);
    Task<Job?> GetJob(string id);
    Task<List<Job>> GetJobs(int skip = 0, int take = 20, JobState[]? states = null);
    Task UpdateState(string id, JobState state, string? note = null);
    Task RequeueForRetry(string id, long scheduledFor, string? note = null);
    Task Requeue(string id);
    Task DeleteJob(string id);
    Task<List<JobHistoryEntry>> GetHistory(string jobId);
    Task RecordFailure(string id, string? error);

    /// <summary>
    /// Atomically claims a job for dispatch: marks it Processing and records the deadline by
    /// which it must be acknowledged, but only if it is still Enqueued or Scheduled. Returns
    /// false without making any change if another server instance already claimed it first -
    /// callers must not dispatch the job when this returns false. This is what makes concurrent
    /// dispatch safe across multiple Axon.Server instances sharing one SQL store.
    /// </summary>
    Task<bool> TryClaimJob(string id, long processingDeadline, string? note = null);

    /// <summary>Jobs currently Processing whose deadline has passed (dispatched but never acknowledged).</summary>
    Task<List<Job>> GetOrphanedProcessingJobs(long asOf);

    /// <summary>Jobs currently Processing on the given device (used to reclaim immediately on disconnect).</summary>
    Task<List<Job>> GetProcessingJobsForDevice(string deviceName);
}