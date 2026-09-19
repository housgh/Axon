using Axon.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Axon.Client.Services;

/// <summary>
/// The default <see cref="JobActivator"/> registered by <c>AddAxonClient</c>: creates a job
/// instance via <see cref="ActivatorUtilities.CreateInstance"/> against a fresh
/// <see cref="IServiceScope"/> of the host's own <see cref="IServiceProvider"/>, so a job
/// class's constructor can take DI-registered dependencies - including scoped ones - instead of
/// being limited to a parameterless constructor. The scope is returned as
/// <see cref="ActivatedJob.Scope"/> so the caller disposes it only after the job body has
/// actually run, not right after this method returns.
/// </summary>
public class ServiceProviderJobActivator(IServiceProvider serviceProvider) : JobActivator
{
    public override ActivatedJob CreateInstance(JobInfo jobInfo)
    {
        var assembly = System.Reflection.Assembly.Load(jobInfo.Assembly);
        var declaringType = assembly.GetType(jobInfo.DeclaringType);
        if (declaringType is null)
        {
            return default;
        }

        var scope = serviceProvider.CreateScope();
        var instance = ActivatorUtilities.CreateInstance(scope.ServiceProvider, declaringType);
        return new ActivatedJob(instance, scope);
    }
}
