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

    /// <summary>Marks a job Processing and records the deadline by which it must be acknowledged.</summary>
    Task MarkProcessing(string id, long processingDeadline, string? note = null);

    /// <summary>Jobs currently Processing whose deadline has passed (dispatched but never acknowledged).</summary>
    Task<List<Job>> GetOrphanedProcessingJobs(long asOf);

    /// <summary>Jobs currently Processing on the given device (used to reclaim immediately on disconnect).</summary>
    Task<List<Job>> GetProcessingJobsForDevice(string deviceName);
}