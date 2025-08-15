using Axon.Core.Models;
using Axon.Server.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;

namespace Axon.Server.Services;

public class AxonJobProcessor(IHubContext<AxonHub> hubContext) : BackgroundService
{
    public static readonly List<Job> Jobs = [];
    private const int PollInterval = 5000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var timestamp = DateTime.UtcNow.Ticks;
            var jobsToRun = Jobs.Where(i => i.ScheduledFor is null || i.ScheduledFor < timestamp).ToList();
            foreach (var job in jobsToRun)
            {
                await hubContext.Clients.Client(job.ConnectionId)
                    .SendCoreAsync("Invoke", [job.JobId, job], stoppingToken);
                Jobs.Remove(job);
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
    public string ConnectionId { get; set; } = null!;
    public string JobId { get; set; } = null!;
    public long? ScheduledFor { get; set; }
}