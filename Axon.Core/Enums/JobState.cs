namespace Axon.Core.Enums;

public enum JobState
{
    Enqueued,
    Scheduled,
    Processing,
    Succeeded,
    Failed,

    // Appended rather than inserted: JobState is persisted as a raw int (see Schema.sql's State
    // column and the (int)state casts throughout the stores), so existing stored values must
    // keep their meaning.

    /// <summary>A continuation job, held until its parent job reaches a terminal state.</summary>
    AwaitingParent,

    /// <summary>
    /// A continuation whose parent job ended in Failed and ContinueOnParentFailure was false (the
    /// default) - the continuation never ran. Distinct from Failed, which means the job itself
    /// was attempted and failed.
    /// </summary>
    Skipped,
}