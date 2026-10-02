using Axon.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Axon.Client.Services;

/// <summary>
/// The default <see cref="JobActivator"/> registered by <c>AddAxonClient</c>: resolves a job
/// instance against a fresh <see cref="IServiceScope"/> of the host's own
/// <see cref="IServiceProvider"/>, so job classes/interfaces work the same way they would as any
/// other DI-resolved dependency. Two shapes are supported:
/// <list type="bullet">
/// <item>A concrete class enqueued directly (e.g. <c>EnqueueAsync&lt;MyJob&gt;(x =&gt; x.Run())</c>)
/// is constructed via <see cref="ActivatorUtilities.CreateInstance"/>, so its constructor can take
/// DI-registered dependencies - including scoped ones - instead of being limited to a
/// parameterless constructor.</item>
/// <item>An interface enqueued via its registered implementation (e.g.
/// <c>EnqueueAsync&lt;IMyService&gt;(x =&gt; x.Run())</c>, mirroring Hangfire) is resolved with
/// <see cref="ServiceProviderServiceExtensions.GetRequiredService(IServiceProvider,Type)"/> instead -
/// an interface has no constructor of its own to activate, so it must already be registered.</item>
/// </list>
/// The scope is returned as <see cref="ActivatedJob.Scope"/> so the caller disposes it only after
/// the job body has actually run, not right after this method returns.
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
        var instance = declaringType.IsInterface
            ? scope.ServiceProvider.GetRequiredService(declaringType)
            : ActivatorUtilities.CreateInstance(scope.ServiceProvider, declaringType);
        return new ActivatedJob(instance, scope);
    }
}
