using Axon.Core.Helpers;
using Axon.Core.Models;

namespace Axon.Client.Services;

/// <summary>The outcome of running a job: <see cref="Error"/> is set only when it failed.</summary>
public readonly record struct JobExecutionResult(bool Succeeded, string? Error)
{
    public static JobExecutionResult Success() => new(true, null);
    public static JobExecutionResult Failure(string error) => new(false, error);
}

/// <summary>
/// Runs a dispatched job's method body locally: activates its declaring type through
/// <see cref="JobActivator.Current"/>, invokes the method with the job's arguments, and reports
/// the outcome. Transport-agnostic - the SignalR client reports the result back over its hub
/// connection, while Axon.Server's in-process mode reports it straight to the job service.
/// </summary>
public class AxonJobExecutor
{
    public Task<JobExecutionResult> ExecuteAsync(JobInfo jobInfo)
    {
        ActivatedJob activated = default;
        try
        {
            activated = JobActivator.Current.CreateInstance(jobInfo);
            var methodInfo = activated.Instance?.GetType().GetMethod(jobInfo.MethodName);
            if (methodInfo is null)
            {
                return Task.FromResult(JobExecutionResult.Failure($"Could not find method: {jobInfo.MethodName}"));
            }

            var arguments = JsonElementHelper.ToObjectArray(jobInfo.Arguments.ToArray());
            methodInfo.Invoke(activated.Instance, arguments);
            return Task.FromResult(JobExecutionResult.Success());
        }
        catch (FileNotFoundException)
        {
            return Task.FromResult(JobExecutionResult.Failure($"Could not load assembly: {jobInfo.Assembly}"));
        }
        catch (Exception e)
        {
            return Task.FromResult(JobExecutionResult.Failure(e.ToString()));
        }
        finally
        {
            // Disposed only now - after the job method has actually run, not right after
            // activation - so a scoped dependency (e.g. ServiceProviderJobActivator's DI scope)
            // injected into the job's constructor stays alive for the whole call.
            activated.Scope?.Dispose();
        }
    }
}
