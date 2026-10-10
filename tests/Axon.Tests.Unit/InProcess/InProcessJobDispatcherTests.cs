using Axon.Client.Services;
using Axon.Server.Services;
using Axon.Tests.Unit.TestHelpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Axon.Tests.Unit.InProcess;

[Collection(JobActivatorCollection.Name)]
public class InProcessJobDispatcherTests : IDisposable
{
    public class TestJobs
    {
        public static ManualResetEventSlim Gate = new(true);

        public void Succeed() { }
        public void Fail() => throw new InvalidOperationException("boom");
        public void Block() => Gate.Wait(TimeSpan.FromSeconds(10));
    }

    private readonly IAxonJobService _jobService = Substitute.For<IAxonJobService>();

    public InProcessJobDispatcherTests()
    {
        JobActivator.Current = new ServiceProviderJobActivator(new ServiceCollection().BuildServiceProvider());
        TestJobs.Gate = new ManualResetEventSlim(true);
    }

    public void Dispose()
    {
        TestJobs.Gate.Set();
        JobActivator.Current = new JobActivator();
    }

    private InProcessJobDispatcher CreateSut(int maxConcurrentJobs = 10) => new(
        new AxonJobExecutor(),
        _jobService,
        new AxonInProcessClientOptions { MaxConcurrentJobs = maxConcurrentJobs },
        NullLogger<InProcessJobDispatcher>.Instance);

    private static Job JobFor(string jobId, string methodName)
    {
        var job = JobFactory.CreateJob(jobId, deviceName: "some-other-machine");
        job.Assembly = typeof(TestJobs).Assembly.FullName!;
        job.DeclaringType = typeof(TestJobs).FullName!;
        job.MethodName = methodName;
        job.Arguments = [];
        return job;
    }

    [Fact]
    public async Task DispatchAsync_JobSucceeds_MarksSucceeded()
    {
        var sut = CreateSut();

        await sut.DispatchAsync(JobFor("job-1", nameof(TestJobs.Succeed)), CancellationToken.None);
        await sut.StopAsync(CancellationToken.None);

        await _jobService.Received(1).MarkSucceededAsync("job-1");
    }

    [Fact]
    public async Task DispatchAsync_JobThrows_MarksFailedWithError()
    {
        var sut = CreateSut();

        await sut.DispatchAsync(JobFor("job-1", nameof(TestJobs.Fail)), CancellationToken.None);
        await sut.StopAsync(CancellationToken.None);

        await _jobService.Received(1).MarkFailedAsync("job-1", Arg.Is<string?>(e => e!.Contains("boom")));
    }

    [Fact]
    public async Task CanDispatchAsync_IgnoresDeviceName()
    {
        var sut = CreateSut();

        (await sut.CanDispatchAsync(JobFor("job-1", nameof(TestJobs.Succeed)))).Should().BeTrue();
    }

    [Fact]
    public async Task CanDispatchAsync_AtCapacity_ReturnsFalseUntilASlotFrees()
    {
        TestJobs.Gate.Reset();
        var sut = CreateSut(maxConcurrentJobs: 1);

        await sut.DispatchAsync(JobFor("job-1", nameof(TestJobs.Block)), CancellationToken.None);

        (await sut.CanDispatchAsync(JobFor("job-2", nameof(TestJobs.Succeed)))).Should().BeFalse();

        TestJobs.Gate.Set();
        await sut.StopAsync(CancellationToken.None);
        sut.RunningCount.Should().Be(0);
    }

    [Fact]
    public async Task StopAsync_WaitsForInFlightJobsAndRejectsNewOnes()
    {
        TestJobs.Gate.Reset();
        var sut = CreateSut();
        await sut.DispatchAsync(JobFor("job-1", nameof(TestJobs.Block)), CancellationToken.None);

        var stop = sut.StopAsync(CancellationToken.None);
        (await sut.CanDispatchAsync(JobFor("job-2", nameof(TestJobs.Succeed)))).Should().BeFalse();
        stop.IsCompleted.Should().BeFalse();

        TestJobs.Gate.Set();
        await stop;
        await _jobService.Received(1).MarkSucceededAsync("job-1");
    }

    [Fact]
    public async Task StopAsync_ShutdownTimeoutElapses_ReturnsWithoutWaitingForever()
    {
        TestJobs.Gate.Reset();
        var sut = CreateSut();
        await sut.DispatchAsync(JobFor("job-1", nameof(TestJobs.Block)), CancellationToken.None);

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await sut.StopAsync(timeout.Token);

        await _jobService.DidNotReceive().MarkSucceededAsync(Arg.Any<string>());
    }
}
