using Axon.Core.Enums;
using Axon.Core.Models;
using Axon.Server.Interfaces;

namespace Axon.Server.Services;

public interface IAxonJobService
{
    Task EnqueueAsync(string deviceName, string jobId, JobInfo jobInfo, long? scheduledFor);

    /// <summary>
    /// Enqueues a job that only becomes runnable once <paramref name="parentJobId"/> reaches a
    /// terminal state: promoted to Enqueued on the parent's success, or on the parent's failure
    /// only if <paramref name="continueOnParentFailure"/> is true (otherwise left permanently in
    /// <see cref="JobState.Skipped"/>). If the parent has already reached a terminal state by the
    /// time this is called, the continuation is promoted/skipped immediately rather than left
    /// waiting forever for an event that already happened.
    /// </summary>
    Task EnqueueContinuationAsync(string deviceName, string jobId, JobInfo jobInfo, string parentJobId, bool continueOnParentFailure);

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

        var job = BuildJob(deviceName, jobId, jobInfo);
        job.ScheduledFor = scheduledFor;
        job.State = scheduledFor is null ? JobState.Enqueued : JobState.Scheduled;

        await jobStore.AddJob(job);
        AxonInstrumentation.JobsEnqueued.Add(1);
        await notifier.JobsChanged();
    }

    public async Task EnqueueContinuationAsync(string deviceName, string jobId, JobInfo jobInfo, string parentJobId, bool continueOnParentFailure)
    {
        using var activity = AxonInstrumentation.ActivitySource.StartActivity("axon.job.enqueue_continuation");
        activity?.SetTag("axon.job_id", jobId);
        activity?.SetTag("axon.device_name", deviceName);
        activity?.SetTag("axon.parent_job_id", parentJobId);

        var job = BuildJob(deviceName, jobId, jobInfo);
        job.State = JobState.AwaitingParent;
        job.ParentJobId = parentJobId;
        job.ContinueOnParentFailure = continueOnParentFailure;

        await jobStore.AddJob(job);
        AxonInstrumentation.JobsEnqueued.Add(1);

        // The parent may have already reached a terminal state before this continuation was
        // created (e.g. it ran and finished between the caller getting the parent's job id and
        // this call landing) - resolve immediately rather than leaving the continuation waiting
        // forever for a state transition that already happened.
        var parent = await jobStore.GetJob(parentJobId);
        if (parent is { State: JobState.Succeeded or JobState.Failed })
        {
            await ResolveContinuationsAsync(parent);
        }
        else
        {
            await notifier.JobsChanged();
        }
    }

    private static Job BuildJob(string deviceName, string jobId, JobInfo jobInfo)
    {
        var job = new Job(jobInfo)
        {
            JobId = jobId,
            DeviceName = deviceName,
            EnqueuedAt = DateTime.UtcNow.Ticks
        };
        if (jobInfo.RetryPolicy is { MaxAttempts: > 0 } policy)
        {
            job.MaxAttempts = policy.MaxAttempts;
        }
        return job;
    }

    public async Task MarkSucceededAsync(string jobId)
    {
        using var activity = AxonInstrumentation.ActivitySource.StartActivity("axon.job.succeed");
        activity?.SetTag("axon.job_id", jobId);

        await jobStore.UpdateState(jobId, JobState.Succeeded);
        AxonInstrumentation.JobsSucceeded.Add(1);

        var job = await jobStore.GetJob(jobId);
        if (job is not null)
        {
            await ResolveContinuationsAsync(job);
        }
        else
        {
            await notifier.JobsChanged();
        }
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
            await notifier.JobsChanged();
        }
        else
        {
            await jobStore.UpdateState(job.JobId, JobState.Failed);
            AxonInstrumentation.JobsFailed.Add(1);
            job.State = JobState.Failed;
            await ResolveContinuationsAsync(job);
        }
    }

    /// <summary>
    /// Promotes every continuation waiting on <paramref name="parent"/> to Enqueued (parent
    /// succeeded, or failed with ContinueOnParentFailure), or to Skipped (parent failed and the
    /// continuation didn't opt into running anyway). <paramref name="parent"/> must already be in
    /// a terminal state (Succeeded or Failed).
    /// </summary>
    private async Task ResolveContinuationsAsync(Job parent)
    {
        var waiting = await jobStore.GetContinuationsWaitingOn(parent.JobId) ?? [];
        foreach (var continuation in waiting)
        {
            using var activity = AxonInstrumentation.ActivitySource.StartActivity("axon.job.resolve_continuation");
            activity?.SetTag("axon.job_id", continuation.JobId);
            activity?.SetTag("axon.parent_job_id", parent.JobId);

            var shouldRun = parent.State == JobState.Succeeded || continuation.ContinueOnParentFailure;
            activity?.SetTag("axon.continuation_runs", shouldRun);

            if (shouldRun)
            {
                var reason = parent.State == JobState.Succeeded
                    ? $"Parent job {parent.JobId} succeeded"
                    : $"Parent job {parent.JobId} failed; continuation configured to run anyway";
                await jobStore.Requeue(continuation.JobId, reason);
            }
            else
            {
                await jobStore.UpdateState(continuation.JobId, JobState.Skipped,
                    $"Parent job {parent.JobId} failed; continuation not configured to run on failure");
            }
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

