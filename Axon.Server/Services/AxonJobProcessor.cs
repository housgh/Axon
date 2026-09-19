using Axon.Core.Enums;
using Axon.Core.Models;
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
    ILogger<AxonJobProcessor> logger) : BackgroundService
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
            logger.LogWarning(
                "Job {JobId} on device {DeviceName} exceeded its processing deadline; reclaiming",
                job.JobId, job.DeviceName);
            await jobService.ReclaimOrphanedAsync(job);
        }
    }

    private async Task DispatchDueJobsAsync(CancellationToken stoppingToken)
    {
        var timestamp = DateTime.UtcNow.Ticks;
        var jobsToRun = await jobStore.GetJobs(take: int.MaxValue, states: [JobState.Enqueued, JobState.Scheduled]);
        jobsToRun = jobsToRun.Where(j => j.ScheduledFor is null || j.ScheduledFor < timestamp).ToList();

        foreach (var job in jobsToRun)
        {
            var connectionId = deviceRegistry.GetConnectionId(job.DeviceName);
            if (connectionId is null)
            {
                // Device is currently offline; leave the job in place and retry next poll.
                continue;
            }

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
                var deadline = DateTime.UtcNow.Add(ProcessingTimeout).Ticks;
                var claimed = await jobStore.TryClaimJob(job.JobId, deadline, $"Dispatched to {job.DeviceName}");
                if (!claimed)
                {
                    // Another instance (or another poll cycle) already claimed this job.
                    continue;
                }
                await notifier.JobsChanged();
                await hubContext.Clients.Client(connectionId)
                    .SendCoreAsync("Invoke", [job.JobId, job], stoppingToken);
            }
            catch (Exception e)
            {
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
    }
    public string DeviceName { get; set; } = null!;
    public string JobId { get; set; } = null!;
    public long? ScheduledFor { get; set; }
    public JobState State { get; set; } = JobState.Enqueued;
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; } = 3;

    /// <summary>
    /// While State is Processing, the point past which the job is considered orphaned
    /// (dispatched but never acknowledged via OnSuccess/OnFail) and eligible for reclaim.
    /// </summary>
    public long? ProcessingDeadline { get; set; }
}