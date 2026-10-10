using System.Text.Json;
using Axon.Client.Services;
using Axon.Core.Models;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Axon.Tests.Unit.InProcess;

[Collection(JobActivatorCollection.Name)]
public class AxonJobExecutorTests : IDisposable
{
    public class TrackedDependency : IDisposable
    {
        public bool IsDisposed { get; private set; }
        public void Dispose() => IsDisposed = true;
    }

    public class TestJobs(TrackedDependency dependency)
    {
        public static string? LastMessage;
        public static TrackedDependency? LastDependency;

        public void Record(string message)
        {
            LastMessage = message;
            LastDependency = dependency;
        }

        public void Throw() => throw new InvalidOperationException("boom");
    }

    private readonly AxonJobExecutor _sut = new();

    public AxonJobExecutorTests()
    {
        var services = new ServiceCollection();
        services.AddScoped<TrackedDependency>();
        JobActivator.Current = new ServiceProviderJobActivator(services.BuildServiceProvider());
        TestJobs.LastMessage = null;
        TestJobs.LastDependency = null;
    }

    public void Dispose() => JobActivator.Current = new JobActivator();

    private static JobInfo JobInfoFor(string methodName, params object?[] args) => new()
    {
        Assembly = typeof(TestJobs).Assembly.FullName!,
        DeclaringType = typeof(TestJobs).FullName!,
        MethodName = methodName,
        Arguments = args.Select(a => (object?)JsonSerializer.SerializeToElement(a)).ToList()
    };

    [Fact]
    public async Task ExecuteAsync_Success_InvokesMethodWithArgumentsAndDisposesScopeAfterward()
    {
        var result = await _sut.ExecuteAsync(JobInfoFor(nameof(TestJobs.Record), "hello"));

        result.Should().Be(JobExecutionResult.Success());
        TestJobs.LastMessage.Should().Be("hello");
        TestJobs.LastDependency!.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_MethodThrows_ReturnsFailureWithException()
    {
        var result = await _sut.ExecuteAsync(JobInfoFor(nameof(TestJobs.Throw)));

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("boom");
    }

    [Fact]
    public async Task ExecuteAsync_UnknownMethod_ReturnsCouldNotFindMethod()
    {
        var result = await _sut.ExecuteAsync(JobInfoFor("DoesNotExist"));

        result.Should().Be(JobExecutionResult.Failure("Could not find method: DoesNotExist"));
    }

    [Fact]
    public async Task ExecuteAsync_UnknownAssembly_ReturnsCouldNotLoadAssembly()
    {
        var jobInfo = JobInfoFor(nameof(TestJobs.Record), "x");
        jobInfo.Assembly = "Not.A.Real.Assembly, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";

        var result = await _sut.ExecuteAsync(jobInfo);

        result.Should().Be(JobExecutionResult.Failure($"Could not load assembly: {jobInfo.Assembly}"));
    }
}
