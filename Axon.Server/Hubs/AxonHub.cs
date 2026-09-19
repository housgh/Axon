using Axon.Core.Models;
using Axon.Server.Interfaces;
using Axon.Server.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Axon.Server.Hubs;

public class AxonHub(
    IAxonJobService jobService,
    IAxonRecurringJobService recurringJobService,
    IDeviceConnectionRegistry deviceRegistry,
    IAxonJobStore jobStore,
    IAxonDashboardNotifier notifier,
    ILogger<AxonHub> logger) : Hub<AxonHub>
{

    public override Task OnConnectedAsync()
    {
        logger.LogDebug("Connection {ConnectionId} connected", Context.ConnectionId);
        return Task.CompletedTask;
    }

    public async Task Register(string deviceName)
    {
        await deviceRegistry.Register(deviceName, Context.ConnectionId);
        await notifier.ClientsChanged();
    }

    public async Task Enqueue(string deviceName, string jobId, JobInfo jobInfo, long? scheduledFor)
    {
        await deviceRegistry.Register(deviceName, Context.ConnectionId);
        await notifier.ClientsChanged();
        await jobService.EnqueueAsync(deviceName, jobId, jobInfo, scheduledFor);
    }

    public async Task EnqueueContinuation(string deviceName, string jobId, JobInfo jobInfo, string parentJobId, bool continueOnParentFailure)
    {
        await deviceRegistry.Register(deviceName, Context.ConnectionId);
        await notifier.ClientsChanged();
        await jobService.EnqueueContinuationAsync(deviceName, jobId, jobInfo, parentJobId, continueOnParentFailure);
    }

    public async Task AddOrUpdateRecurring(string deviceName, string recurringJobId, JobInfo jobInfo, string cronExpression)
    {
        await deviceRegistry.Register(deviceName, Context.ConnectionId);
        await notifier.ClientsChanged();
        await recurringJobService.AddOrUpdateAsync(deviceName, recurringJobId, jobInfo, cronExpression);
    }

    public Task RemoveRecurring(string recurringJobId)
    {
        return recurringJobService.RemoveAsync(recurringJobId);
    }

    public Task OnSuccess(string jobId)
    {
        return jobService.MarkSucceededAsync(jobId);
    }

    public Task OnFail(string jobId, string error)
    {
        logger.LogWarning("Job {JobId} failed: {Error}", jobId, error);
        return jobService.MarkFailedAsync(jobId, error);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        logger.LogDebug("Connection {ConnectionId} disconnected", Context.ConnectionId);

        var deviceName = await deviceRegistry.GetDeviceName(Context.ConnectionId);
        await deviceRegistry.Unregister(Context.ConnectionId);
        await notifier.ClientsChanged();

        // Any job this device was actively processing can no longer be acknowledged on this
        // connection; reclaim it immediately instead of waiting for the processing deadline.
        if (deviceName is not null)
        {
            var stranded = await jobStore.GetProcessingJobsForDevice(deviceName);
            foreach (var job in stranded)
            {
                await jobService.ReclaimOrphanedAsync(job);
            }
        }
    }
}
