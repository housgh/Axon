using System.Reflection;
using Axon.Core.Models;

namespace Axon.Client.Services;

/// <summary>
/// The result of activating a job: the instance its dispatched method is invoked on, plus
/// whatever backs that instance's lifetime (e.g. a DI scope). <see cref="Scope"/> is disposed
/// once the job body has finished running - after invocation, not right after activation - so a
/// scoped dependency injected into the job's constructor stays alive for the whole call.
/// </summary>
public readonly record struct ActivatedJob(object? Instance, IDisposable? Scope);

/// <summary>
/// Creates the instance a dispatched job's method is invoked on.
/// <c>Axon.Client.DependencyInjection.AddAxonClient</c> registers a
/// <see cref="ServiceProviderJobActivator"/> as <see cref="Current"/> by default, so job classes
/// with constructor dependencies are resolved through the host's own DI container - set
/// <see cref="Current"/> to a different <see cref="JobActivator"/> (or override
/// <see cref="CreateInstance"/> in a subclass) for any other activation strategy.
/// </summary>
public class JobActivator
{
    public static JobActivator Current { get; set; } = new();

    public virtual ActivatedJob CreateInstance(JobInfo jobInfo)
    {
        var assembly = Assembly.Load(jobInfo.Assembly);

        var declaringType = assembly.GetType(jobInfo.DeclaringType);

        if (declaringType is null)
        {
            return default;
        }

        var instance = Activator.CreateInstance(declaringType);

        return new ActivatedJob(instance, null);
    }
}
