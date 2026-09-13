using Axon.Core.Enums;
using Axon.Core.Models;
using Axon.Server.Interfaces;

namespace Axon.Server.Services;

public interface IAxonJobService
{
    Task EnqueueAsync(string deviceName, string jobId, JobInfo jobInfo, long? scheduledFor);
    Task MarkSucceededAsync(string jobId);
    Task MarkFailedAsync(string jobId, string? error = null);
}

public class AxonJobService(IAxonJobStore jobStore) : IAxonJobService
{
    private static readonly TimeSpan[] RetryBackoff =
    [
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
    ];

    public Task EnqueueAsync(string deviceName, string jobId, JobInfo jobInfo, long? scheduledFor)
    {
        return jobStore.AddJob(new Job(jobInfo)
        {
            JobId = jobId,
            DeviceName = deviceName,
            ScheduledFor = scheduledFor,
            State = scheduledFor is null ? JobState.Enqueued : JobState.Scheduled
        });
    }

    public Task MarkSucceededAsync(string jobId)
    {
        return jobStore.UpdateState(jobId, JobState.Succeeded);
    }

    public async Task MarkFailedAsync(string jobId, string? error = null)
    {
        var job = await jobStore.GetJob(jobId);
        if (job is null) return;

        await jobStore.RecordFailure(jobId, error);

        // job.Attempts counts attempts already made (this failure included once RequeueForRetry
        // runs), so retry only while at least one more attempt would still fit under MaxAttempts.
        if (job.Attempts + 1 < job.MaxAttempts)
        {
            var backoff = RetryBackoff[Math.Min(job.Attempts, RetryBackoff.Length - 1)];
            await jobStore.RequeueForRetry(jobId, DateTime.UtcNow.Add(backoff).Ticks, $"Retrying in {backoff}");
        }
        else
        {
            await jobStore.UpdateState(jobId, JobState.Failed);
        }
    }
}

