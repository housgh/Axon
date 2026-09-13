using Axon.Core.Enums;

namespace Axon.Server.Services;

public class JobHistoryEntry
{
    public string JobId { get; set; } = null!;
    public JobState State { get; set; }
    public long Timestamp { get; set; }
    public string? Note { get; set; }
}
