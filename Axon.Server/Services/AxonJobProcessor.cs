using System.Diagnostics;
using Axon.Core.Enums;
using Axon.Core.Models;
using Axon.Server.DependencyInjection;
using Axon.Server.Hubs;
using Axon.Server.Interfaces;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Axon.Server.Services;

public class AxonJobProcessor(
    IHubContext<AxonHub> hubContext,
    IAxonJobStore jobStore,
    IAxonJobService jobService,
    IDeviceConnectionRegistry deviceRegistry,
    IAxonDashboardNotifier notifier,
    ILogger<AxonJobProcessor> logger,
    AxonServerFeatures? features = null) : BackgroundService
{
    private const int PollInterval = 5000;

    // How long we wait for OnSuccess/OnFail after dispatching before treating a job as orphaned.
    // Axon has no execution-progress heartbeat from the client, so this bounds "acknowledge the
    // dispatch", not "how long the job body may run" - keep it generous.
    private static readonly TimeSpan ProcessingTimeout = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await ReclaimOrphanedJobsAsync();
            await DispatchDueJobsAsync(stoppingToken);

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task ReclaimOrphanedJobsAsync()
    {
        var orphaned = await jobStore.GetOrphanedProcessingJobs(DateTime.UtcNow.Ticks);
        foreach (var job in orphaned)
        {
            using var activity = AxonInstrumentation.ActivitySource.StartActivity("axon.job.reclaim_orphaned");
            activity?.SetTag("axon.job_id", job.JobId);
            activity?.SetTag("axon.device_name", job.DeviceName);

            logger.LogWarning(
                "Job {JobId} on device {DeviceName} exceeded its processing deadline; reclaiming",
                job.JobId, job.DeviceName);
            AxonInstrumentation.JobsOrphanedReclaimed.Add(1);

            // ProcessingDeadline is claimedAt + ProcessingTimeout, so subtracting it back out
            // recovers the claim time without needing a separate persisted "claimed at" column.
            if (job.ProcessingDeadline is { } deadline)
            {
                var claimedAt = deadline - ProcessingTimeout.Ticks;
                AxonInstrumentation.ExecutionDuration.Record(
                    TimeSpan.FromTicks(DateTime.UtcNow.Ticks - claimedAt).TotalMilliseconds);
            }

            await jobService.ReclaimOrphanedAsync(job);
        }
    }

    private async Task DispatchDueJobsAsync(CancellationToken stoppingToken)
    {
        var timestamp = DateTime.UtcNow.Ticks;
        var jobsToRun = await jobStore.GetJobs(take: int.MaxValue, states: [JobState.Enqueued, JobState.Scheduled]);
        jobsToRun = jobsToRun.Where(j => j.ScheduledFor is null || j.ScheduledFor < timestamp).ToList();
        AxonInstrumentation.SetQueueDepth(jobsToRun.Count);

        foreach (var job in jobsToRun)
        {
            if (features is not null && !features.ServedQueues.Contains(job.QueueName))
            {
                // This instance doesn't serve this job's queue; leave it for another instance
                // (or a future poll, once this instance's queue configuration changes).
                continue;
            }

            var connectionId = await deviceRegistry.GetConnectionId(job.DeviceName);
            if (connectionId is null)
            {
                // Device is currently offline; leave the job in place and retry next poll.
                continue;
            }

            using var activity = AxonInstrumentation.ActivitySource.StartActivity("axon.job.dispatch");
            activity?.SetTag("axon.job_id", job.JobId);
            activity?.SetTag("axon.device_name", job.DeviceName);

            try
            {
                // Claim before dispatch, not after: SendCoreAsync can hand off to a client that
                // executes and calls back OnSuccess before this method continues, and if the
                // claim then landed afterward it would clobber that Succeeded state back to
                // Processing, permanently stranding an already-completed job.
                //
                // The claim is also atomic (TryClaimJob only succeeds if the job is still
                // Enqueued/Scheduled), which is what makes it safe for multiple Axon.Server
                // instances to poll the same SQL-backed job store concurrently: at most one
                // instance's claim can succeed for a given job, so at most one instance ever
                // dispatches it.
                var claimedAt = DateTime.UtcNow;
                var deadline = claimedAt.Add(ProcessingTimeout).Ticks;
                var claimed = await jobStore.TryClaimJob(job.JobId, deadline, $"Dispatched to {job.DeviceName}");
                if (!claimed)
                {
                    // Another instance (or another poll cycle) already claimed this job.
                    AxonInstrumentation.JobsClaimFailed.Add(1);
                    activity?.SetTag("axon.claimed", false);
                    continue;
                }

                activity?.SetTag("axon.claimed", true);
                AxonInstrumentation.JobsDispatched.Add(1);
                if (job.EnqueuedAt > 0)
                {
                    AxonInstrumentation.DispatchLatency.Record(
                        TimeSpan.FromTicks(claimedAt.Ticks - job.EnqueuedAt).TotalMilliseconds);
                }

                await notifier.JobsChanged();
                await hubContext.Clients.Client(connectionId)
                    .SendCoreAsync("Invoke", [job.JobId, job], stoppingToken);
            }
            catch (Exception e)
            {
                activity?.SetStatus(ActivityStatusCode.Error, e.Message);
                logger.LogWarning(e, "Failed to dispatch job {JobId} to device {DeviceName}; will retry next poll", job.JobId, job.DeviceName);
            }
        }
    }
}


public class Job : JobInfo
{
    // Required for Dapper (and other reflection-based materializers) to construct a Job from a
    // SELECT * row via property setters; defining Job(JobInfo) alone removes the implicit
    // parameterless constructor, which broke every query-returning method against real SQL Server.
    public Job()
    {
    }

    public Job(JobInfo jobInfo)
    {
        Arguments = jobInfo.Arguments;
        MethodName = jobInfo.MethodName;
        Assembly = jobInfo.Assembly;
        DeclaringType = jobInfo.DeclaringType;
        RetryPolicy = jobInfo.RetryPolicy;
        ConcurrencyKey = jobInfo.ConcurrencyKey;
        MaxConcurrent = jobInfo.MaxConcurrent;
        Priority = jobInfo.Priority;
        QueueName = jobInfo.QueueName;
    }
    public string DeviceName { get; set; } = null!;
    public string JobId { get; set; } = null!;
    public long? ScheduledFor { get; set; }
    public JobState State { get; set; } = JobState.Enqueued;
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; } = 3;

    /// <summary>When this job was first added to the store. Used only for the dispatch-latency metric.</summary>
    public long EnqueuedAt { get; set; }

    /// <summary>
    /// While State is Processing, the point past which the job is considered orphaned
    /// (dispatched but never acknowledged via OnSuccess/OnFail) and eligible for reclaim.
    /// </summary>
    public long? ProcessingDeadline { get; set; }

    /// <summary>
    /// If set, this job is a continuation: it starts in <see cref="JobState.AwaitingParent"/> and
    /// is only promoted to Enqueued once the job identified by ParentJobId reaches a terminal
    /// state (Succeeded, or Failed with <see cref="ContinueOnParentFailure"/> true).
    /// </summary>
    public string? ParentJobId { get; set; }

    /// <summary>
    /// Only meaningful when <see cref="ParentJobId"/> is set. If the parent ends in Failed: true
    /// runs the continuation anyway, false (the default) leaves it permanently in
    /// <see cref="JobState.Skipped"/>.
    /// </summary>
    public bool ContinueOnParentFailure { get; set; }
}