namespace Axon.Server.Services;

public class AxonJobCleanupOptions
{
    /// <summary>How long a completed job (Succeeded/Failed/Skipped) is kept before being purged.</summary>
    public TimeSpan Retention { get; set; }

    /// <summary>How often the cleanup sweep runs. Defaults to once an hour.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromHours(1);
}
