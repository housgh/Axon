using Axon.Core.Models;

namespace Axon.Server.Services;

public class RecurringJob : JobInfo
{
    // See Job() in AxonJobProcessor.cs: required for Dapper/reflection-based materialization.
    public RecurringJob()
    {
    }

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

    /// <summary>
    /// While true, the poll loop skips this recurring job entirely - it never fires and
    /// NextRunAt is left untouched, so resuming (setting this back to false) picks up the
    /// existing schedule exactly where it would have been, rather than recomputing it.
    /// </summary>
    public bool IsPaused { get; set; }
}
