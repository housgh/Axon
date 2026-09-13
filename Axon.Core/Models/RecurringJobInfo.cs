namespace Axon.Core.Models;

public class RecurringJobInfo : JobInfo
{
    public string RecurringJobId { get; set; } = null!;
    public string CronExpression { get; set; } = null!;
}
