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
}