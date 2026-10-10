using System.Collections.Concurrent;
using Axon.Client.Services;
using Microsoft.Extensions.Logging;

namespace Axon.Server.Services;

/// <summary>
/// The dispatcher registered by <c>AddInProcessClient</c> (monolith mode): runs claimed jobs on
/// this process's thread pool instead of pushing them to a remote device. Ignores
/// <see cref="Job.DeviceName"/> - every instance of the monolith can run every job, and
/// <c>TryClaimJob</c> already guarantees only one instance runs a given job.
/// </summary>
internal sealed class InProcessJobDispatcher(
    AxonJobExecutor executor,
    IAxonJobService jobService,
    AxonInProcessClientOptions options,
    ILogger<InProcessJobDispatcher> logger) : IAxonJobDispatcher
{
    private readonly ConcurrentDictionary<string, Task> _running = new();
    private volatile bool _stopping;

    public int RunningCount => _running.Count;

    public ValueTask<bool> CanDispatchAsync(Job job) =>
        ValueTask.FromResult(!_stopping && _running.Count < options.MaxConcurrentJobs);

    public Task DispatchAsync(Job job, CancellationToken cancellationToken)
    {
        // Deliberately not tied to the processor's stopping token: once claimed, a job is allowed
        // to finish; host shutdown waits for it in StopAsync instead of aborting it mid-body.
        var task = Task.Run(() => RunAsync(job), CancellationToken.None);
        _running[job.JobId] = task;
        task.ContinueWith(_ => _running.TryRemove(job.JobId, out Task? _), TaskScheduler.Default);
        return Task.CompletedTask;
    }

    private async Task RunAsync(Job job)
    {
        try
        {
            var result = await executor.ExecuteAsync(job);
            if (result.Succeeded)
            {
                await jobService.MarkSucceededAsync(job.JobId);
            }
            else
            {
                logger.LogWarning("Job {JobId} failed: {Error}", job.JobId, result.Error);
                await jobService.MarkFailedAsync(job.JobId, result.Error);
            }
        }
        catch (Exception e)
        {
            // Only reachable if reporting the outcome failed (e.g. the job store is unreachable) -
            // the executor itself never throws. The job stays Processing and is reclaimed once
            // its processing deadline passes, same as a job orphaned by a disconnected device.
            logger.LogError(e, "Failed to record the outcome of job {JobId}; it will be reclaimed after its processing deadline", job.JobId);
        }
    }

    /// <summary>
    /// Stops accepting new jobs and waits for in-flight ones to finish, or for
    /// <paramref name="cancellationToken"/> (the host's shutdown timeout). Jobs still running past
    /// that are left Processing and reclaimed via their processing deadline after a restart.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping = true;
        try
        {
            await Task.WhenAll(_running.Values).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Shutdown timed out with {Count} in-process job(s) still running", _running.Count);
        }
    }
}
