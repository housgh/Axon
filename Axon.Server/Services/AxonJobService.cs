using Axon.Core.Enums;
using Axon.Core.Models;
using Axon.Server.Interfaces;

namespace Axon.Server.Services;

public interface IAxonJobService
{
    Task EnqueueAsync(string deviceName, string jobId, JobInfo jobInfo, long? scheduledFor);
    Task MarkSucceededAsync(string jobId);
    Task MarkFailedAsync(string jobId, string? error = null);
    Task ReclaimOrphanedAsync(Job job);
}

public class AxonJobService(IAxonJobStore jobStore, IAxonDashboardNotifier notifier) : IAxonJobService
{
    private static readonly TimeSpan[] RetryBackoff =
    [
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
    ];

    public async Task EnqueueAsync(string deviceName, string jobId, JobInfo jobInfo, long? scheduledFor)
    {
        using var activity = AxonInstrumentation.ActivitySource.StartActivity("axon.job.enqueue");
        activity?.SetTag("axon.job_id", jobId);
        activity?.SetTag("axon.device_name", deviceName);

        var job = new Job(jobInfo)
        {
            JobId = jobId,
            DeviceName = deviceName,
            ScheduledFor = scheduledFor,
            State = scheduledFor is null ? JobState.Enqueued : JobState.Scheduled,
            EnqueuedAt = DateTime.UtcNow.Ticks
        };
        if (jobInfo.RetryPolicy is { MaxAttempts: > 0 } policy)
        {
            job.MaxAttempts = policy.MaxAttempts;
        }

        await jobStore.AddJob(job);
        AxonInstrumentation.JobsEnqueued.Add(1);
        await notifier.JobsChanged();
    }

    public async Task MarkSucceededAsync(string jobId)
    {
        using var activity = AxonInstrumentation.ActivitySource.StartActivity("axon.job.succeed");
        activity?.SetTag("axon.job_id", jobId);

        await jobStore.UpdateState(jobId, JobState.Succeeded);
        AxonInstrumentation.JobsSucceeded.Add(1);
        await notifier.JobsChanged();
    }

    public async Task MarkFailedAsync(string jobId, string? error = null)
    {
        using var activity = AxonInstrumentation.ActivitySource.StartActivity("axon.job.fail");
        activity?.SetTag("axon.job_id", jobId);

        var job = await jobStore.GetJob(jobId);
        if (job is null) return;

        await jobStore.RecordFailure(jobId, error);
        await RetryOrFailAsync(job);
    }

    public async Task ReclaimOrphanedAsync(Job job)
    {
        await jobStore.RecordFailure(job.JobId, "Job orphaned: dispatched but never acknowledged (device disconnected or timed out)");
        await RetryOrFailAsync(job);
    }

    private async Task RetryOrFailAsync(Job job)
    {
        // job.Attempts counts attempts already made (this failure included once RequeueForRetry
        // runs), so retry only while at least one more attempt would still fit under MaxAttempts.
        if (job.Attempts + 1 < job.MaxAttempts)
        {
            var backoff = GetBackoff(job);
            await jobStore.RequeueForRetry(job.JobId, DateTime.UtcNow.Add(backoff).Ticks, $"Retrying in {backoff}");
            AxonInstrumentation.JobsRetried.Add(1);
        }
        else
        {
            await jobStore.UpdateState(job.JobId, JobState.Failed);
            AxonInstrumentation.JobsFailed.Add(1);
        }
        await notifier.JobsChanged();
    }

    private static TimeSpan GetBackoff(Job job)
    {
        var delaysSeconds = job.RetryPolicy?.RetryDelaysSeconds;
        if (delaysSeconds is { Count: > 0 })
        {
            // Past the configured entries, reuse the last one for every subsequent retry -
            // mirrors the fallback behavior of the built-in RetryBackoff array below.
            var index = Math.Min(job.Attempts, delaysSeconds.Count - 1);
            return TimeSpan.FromSeconds(delaysSeconds[index]);
        }

        return RetryBackoff[Math.Min(job.Attempts, RetryBackoff.Length - 1)];
    }
}

