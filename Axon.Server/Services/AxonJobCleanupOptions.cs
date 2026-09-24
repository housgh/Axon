namespace Axon.Server.Services;

public class AxonJobCleanupOptions
{
    /// <summary>
    /// How long a Succeeded job is kept before being purged. Defaults to 1 day. Failed and
    /// Skipped jobs are never purged automatically, regardless of age.
    /// </summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(1);

    /// <summary>How often the cleanup sweep runs. Defaults to once an hour.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromHours(1);
}
