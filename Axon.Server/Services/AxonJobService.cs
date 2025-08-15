using Axon.Core.Models;

namespace Axon.Server.Services;

public interface IAxonJobService
{
    Task EnqueueAsync(string connectionId, string jobId, JobInfo jobInfo);
}

public class AxonJobService : IAxonJobService
{
    public Task EnqueueAsync(string connectionId, string jobId, JobInfo jobInfo)
    {
        AxonJobProcessor.Jobs.Add(new Job(jobInfo)
        {
            JobId = jobId,
            ConnectionId = connectionId
        });
        return Task.CompletedTask;
    }
}

