using Axon.Core.Models;
using Axon.Server.Services;
using Microsoft.AspNetCore.SignalR;

namespace Axon.Server.Hubs;

public class AxonHub(
    IAxonJobService jobService,
    IAxonRecurringJobService recurringJobService,
    IDeviceConnectionRegistry deviceRegistry) : Hub<AxonHub>
{

    public override Task OnConnectedAsync()
    {
        Console.WriteLine($"OnConnectedAsync called {Context.ConnectionId}");
        return Task.CompletedTask;
    }

    public void Register(string deviceName)
    {
        deviceRegistry.Register(deviceName, Context.ConnectionId);
    }

    public Task Enqueue(string deviceName, string jobId, JobInfo jobInfo, long? scheduledFor)
    {
        deviceRegistry.Register(deviceName, Context.ConnectionId);
        return jobService.EnqueueAsync(deviceName, jobId, jobInfo, scheduledFor);
    }

    public Task AddOrUpdateRecurring(string deviceName, string recurringJobId, JobInfo jobInfo, string cronExpression)
    {
        deviceRegistry.Register(deviceName, Context.ConnectionId);
        return recurringJobService.AddOrUpdateAsync(deviceName, recurringJobId, jobInfo, cronExpression);
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

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        Console.WriteLine($"OnDisconnectedAsync called {Context.ConnectionId}");
        deviceRegistry.Unregister(Context.ConnectionId);
        return Task.CompletedTask;
    }
}
