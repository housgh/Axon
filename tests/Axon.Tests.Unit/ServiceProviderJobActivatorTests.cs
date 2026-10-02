using Axon.Client.Services;
using Axon.Core.Models;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Axon.Tests.Unit;

public class ServiceProviderJobActivatorTests
{
    public interface IGreeter
    {
        string Greet();
    }

    public class Greeter : IGreeter
    {
        public string Greet() => "hello";
    }

    public class JobWithDependency(IGreeter greeter)
    {
        public string Run() => greeter.Greet();
    }

    private static JobInfo JobInfoFor(Type type, string methodName) => new()
    {
        Assembly = type.Assembly.FullName!,
        DeclaringType = type.FullName!,
        MethodName = methodName
    };

    private static JobInfo JobInfoFor(Type type) => JobInfoFor(type, nameof(JobWithDependency.Run));

    [Fact]
    public void CreateInstance_ResolvesConstructorDependencyFromServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IGreeter, Greeter>();
        var provider = services.BuildServiceProvider();
        var sut = new ServiceProviderJobActivator(provider);

        var activated = sut.CreateInstance(JobInfoFor(typeof(JobWithDependency)));

        activated.Instance.Should().BeOfType<JobWithDependency>();
        ((JobWithDependency)activated.Instance!).Run().Should().Be("hello");
        activated.Scope.Should().NotBeNull();
    }

    [Fact]
    public void CreateInstance_EachCall_GetsItsOwnScope()
    {
        var services = new ServiceCollection();
        services.AddScoped<IGreeter, Greeter>();
        var provider = services.BuildServiceProvider();
        var sut = new ServiceProviderJobActivator(provider);

        var first = sut.CreateInstance(JobInfoFor(typeof(JobWithDependency)));
        var second = sut.CreateInstance(JobInfoFor(typeof(JobWithDependency)));

        first.Scope.Should().NotBeSameAs(second.Scope);

        first.Scope!.Dispose();
        second.Scope!.Dispose();
    }

    [Fact]
    public void CreateInstance_MissingDependency_ThrowsRatherThanReturningNull()
    {
        // No IGreeter registered, so ActivatorUtilities.CreateInstance can't satisfy
        // JobWithDependency's constructor - this should surface as a clear DI exception (caught
        // and reported as a job failure by AxonClient.OnInvoke), not silently return null the
        // way an unresolvable assembly/type does.
        var provider = new ServiceCollection().BuildServiceProvider();
        var sut = new ServiceProviderJobActivator(provider);

        var act = () => sut.CreateInstance(JobInfoFor(typeof(JobWithDependency)));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void CreateInstance_UnknownDeclaringType_ReturnsDefaultActivatedJob()
    {
        var provider = new ServiceCollection().BuildServiceProvider();
        var sut = new ServiceProviderJobActivator(provider);
        var jobInfo = new JobInfo
        {
            Assembly = typeof(JobWithDependency).Assembly.FullName!,
            DeclaringType = "Nonexistent.Type.Name",
            MethodName = "Run"
        };

        var activated = sut.CreateInstance(jobInfo);

        activated.Instance.Should().BeNull();
        activated.Scope.Should().BeNull();
    }

    // Mirrors Hangfire's interface-job pattern: EnqueueAsync<IGreeter>(x => x.Greet()) records
    // IGreeter (not Greeter) as the JobInfo's DeclaringType, since that's the method's declaring
    // type at the call site - so CreateInstance must resolve it as a registered service rather
    // than trying (and failing) to construct the interface itself.
    [Fact]
    public void CreateInstance_InterfaceDeclaringType_ResolvesRegisteredImplementation()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IGreeter, Greeter>();
        var provider = services.BuildServiceProvider();
        var sut = new ServiceProviderJobActivator(provider);

        var activated = sut.CreateInstance(JobInfoFor(typeof(IGreeter), nameof(IGreeter.Greet)));

        activated.Instance.Should().BeOfType<Greeter>();
        ((IGreeter)activated.Instance!).Greet().Should().Be("hello");
        activated.Scope.Should().NotBeNull();
    }

    [Fact]
    public void CreateInstance_InterfaceDeclaringTypeNotRegistered_ThrowsRatherThanReturningNull()
    {
        var provider = new ServiceCollection().BuildServiceProvider();
        var sut = new ServiceProviderJobActivator(provider);

        var act = () => sut.CreateInstance(JobInfoFor(typeof(IGreeter), nameof(IGreeter.Greet)));

        act.Should().Throw<InvalidOperationException>();
    }
}
