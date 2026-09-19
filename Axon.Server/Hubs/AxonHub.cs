using Axon.Core.Models;
using Axon.Server.Interfaces;
using Axon.Server.Services;
using Microsoft.AspNetCore.SignalR;

namespace Axon.Server.Hubs;

public class AxonHub(
    IAxonJobService jobService,
    IAxonRecurringJobService recurringJobService,
    IDeviceConnectionRegistry deviceRegistry,
    IAxonJobStore jobStore,
    IAxonDashboardNotifier notifier) : Hub<AxonHub>
{

    public override Task OnConnectedAsync()
    {
        Console.WriteLine($"OnConnectedAsync called {Context.ConnectionId}");
        return Task.CompletedTask;
    }

    public Task Register(string deviceName)
    {
        deviceRegistry.Register(deviceName, Context.ConnectionId);
        return notifier.ClientsChanged();
    }

    public async Task Enqueue(string deviceName, string jobId, JobInfo jobInfo, long? scheduledFor)
    {
        deviceRegistry.Register(deviceName, Context.ConnectionId);
        await notifier.ClientsChanged();
        await jobService.EnqueueAsync(deviceName, jobId, jobInfo, scheduledFor);
    }

    public async Task AddOrUpdateRecurring(string deviceName, string recurringJobId, JobInfo jobInfo, string cronExpression)
    {
        deviceRegistry.Register(deviceName, Context.ConnectionId);
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
        Console.WriteLine($"Job {jobId} failed: {error}");
        return jobService.MarkFailedAsync(jobId, error);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        Console.WriteLine($"OnDisconnectedAsync called {Context.ConnectionId}");

        var deviceName = deviceRegistry.GetDeviceName(Context.ConnectionId);
        deviceRegistry.Unregister(Context.ConnectionId);
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
