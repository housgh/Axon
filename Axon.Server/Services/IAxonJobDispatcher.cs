namespace Axon.Server.Services;

/// <summary>
/// Hands a claimed job off to whatever executes it. <see cref="AxonJobProcessor"/> owns the
/// poll/claim loop and stays transport-agnostic; the dispatcher decides where a job runs - pushed
/// over SignalR to a remote device (<see cref="SignalRJobDispatcher"/>, the default), or run in
/// this same process (<see cref="InProcessJobDispatcher"/>, via <c>AddInProcessClient</c>).
/// </summary>
public interface IAxonJobDispatcher
{
    /// <summary>
    /// Whether <paramref name="job"/> could be dispatched right now. Checked before claiming, so a
    /// job with nowhere to run is left Enqueued/Scheduled for a later poll instead of being moved
    /// to Processing and then orphaned.
    /// </summary>
    ValueTask<bool> CanDispatchAsync(Job job);

    /// <summary>
    /// Starts running an already-claimed job. Must not wait for the job body to finish - completion
    /// is reported separately through <see cref="IAxonJobService"/>.
    /// </summary>
    Task DispatchAsync(Job job, CancellationToken cancellationToken);
}
