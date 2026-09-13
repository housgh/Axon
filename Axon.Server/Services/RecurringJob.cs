using Axon.Core.Models;

namespace Axon.Server.Services;

public class RecurringJob : JobInfo
{
    public RecurringJob(JobInfo jobInfo)
    {
        Arguments = jobInfo.Arguments;
        MethodName = jobInfo.MethodName;
        Assembly = jobInfo.Assembly;
        DeclaringType = jobInfo.DeclaringType;
    }

    public string RecurringJobId { get; set; } = null!;
    public string DeviceName { get; set; } = null!;
    public string CronExpression { get; set; } = null!;
    public long NextRunAt { get; set; }
    public long? LastRunAt { get; set; }
}
