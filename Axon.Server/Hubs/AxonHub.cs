using Axon.Core.Models;
using Axon.Server.Services;
using Microsoft.AspNetCore.SignalR;

namespace Axon.Server.Hubs;

public class AxonHub(IAxonJobService jobService) : Hub<AxonHub>
{
    
    public override async Task OnConnectedAsync()
    {
        Console.WriteLine($"OnConnectedAsync called {Context.ConnectionId}");
    }

    public Task Enqueue(string deviceName, string jobId, JobInfo jobInfo)
    {
        return jobService.EnqueueAsync(Context.ConnectionId, jobId, jobInfo);
    }

    public override async Task OnDisconnectedAsync(Exception exception)
    {
        Console.WriteLine($"OnDisconnectedAsync called {Context.ConnectionId}");
    }
}
