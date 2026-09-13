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
    IDeviceConnectionRegistry deviceRegistry,
    ILogger<AxonJobProcessor> logger) : BackgroundService
{
    private const int PollInterval = 5000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
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
                    await hubContext.Clients.Client(connectionId)
                        .SendCoreAsync("Invoke", [job.JobId, job], stoppingToken);
                    await jobStore.UpdateState(job.JobId, JobState.Processing, $"Dispatched to {job.DeviceName}");
                }
                catch (Exception e)
                {
                    logger.LogWarning(e, "Failed to dispatch job {JobId} to device {DeviceName}; will retry next poll", job.JobId, job.DeviceName);
                }
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }
}


public class Job : JobInfo
{
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
}