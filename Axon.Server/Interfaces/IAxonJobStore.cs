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
    Task Requeue(string id, string note = "Requeued manually");
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

    /// <summary>Continuation jobs (State == AwaitingParent) whose ParentJobId is the given job.</summary>
    Task<List<Job>> GetContinuationsWaitingOn(string parentJobId);

    /// <summary>
    /// Deletes (soft-deletes, same as <see cref="DeleteJob"/>) every <see cref="JobState.Succeeded"/>
    /// job whose most recent history entry is older than <paramref name="cutoff"/>, along with that
    /// job's history rows. Returns the number of jobs deleted. Failed and Skipped jobs are never
    /// touched, regardless of age. Used by the job-data retention cleanup (see <c>AddJobCleanup</c>).
    /// </summary>
    Task<int> DeleteCompletedJobsOlderThan(long cutoff);

    /// <summary>
    /// Lifetime counts per <see cref="JobState"/>. For the terminal states (Succeeded, Failed,
    /// Skipped) this includes jobs already soft-deleted by <see cref="DeleteCompletedJobsOlderThan"/>
    /// or <see cref="DeleteJob"/>, so cleanup never makes these numbers go down - a job that
    /// succeeded and was later cleaned up still counts. Non-terminal states (Enqueued, Scheduled,
    /// Processing, AwaitingParent) reflect only live jobs, since those are never subject to cleanup.
    /// States with no jobs are omitted rather than present with a zero count.
    /// </summary>
    Task<Dictionary<JobState, int>> CountJobsByState();
}