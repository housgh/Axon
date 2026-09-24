using System.Reflection;
using Axon.Core.Enums;
using Axon.Tests.Unit.TestHelpers;
using Axon.Server.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Axon.Tests.Unit.Services;

public class AxonJobCleanupProcessorTests
{
    private readonly InMemoryAxonJobStore _jobStore = new();
    private readonly IAxonDashboardNotifier _notifier = Substitute.For<IAxonDashboardNotifier>();
    private readonly AxonJobCleanupOptions _options = new() { Retention = TimeSpan.FromDays(1) };
    private readonly AxonJobCleanupProcessor _sut;

    public AxonJobCleanupProcessorTests()
    {
        _sut = new AxonJobCleanupProcessor(_jobStore, _options, _notifier, Substitute.For<ILogger<AxonJobCleanupProcessor>>());
    }

    private Task RunSweepAsync()
    {
        // RunSweepAsync is private; invoked via reflection so the test exercises the actual
        // sweep logic (cutoff computation, notifier call) rather than reimplementing it, matching
        // the pattern AxonJobProcessorTests/AxonRecurringJobProcessorTests already use.
        var method = typeof(AxonJobCleanupProcessor).GetMethod("RunSweepAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Task)method.Invoke(_sut, [])!;
    }

    [Fact]
    public async Task RunSweepAsync_DeletesOldSucceededJobs_NotifiesDashboard()
    {
        await _jobStore.AddJob(JobFactory.CreateJob("job-1", state: JobState.Enqueued));
        await _jobStore.UpdateState("job-1", JobState.Succeeded);
        _options.Retention = TimeSpan.Zero;

        await RunSweepAsync();

        (await _jobStore.GetJob("job-1")).Should().BeNull();
        await _notifier.Received(1).JobsChanged();
    }

    [Fact]
    public async Task RunSweepAsync_NothingToDelete_DoesNotNotifyDashboard()
    {
        await _jobStore.AddJob(JobFactory.CreateJob("job-1", state: JobState.Enqueued));

        await RunSweepAsync();

        await _notifier.DidNotReceive().JobsChanged();
    }

    [Fact]
    public async Task RunSweepAsync_NeverDeletesFailedOrSkippedJobs()
    {
        await _jobStore.AddJob(JobFactory.CreateJob("job-1", state: JobState.Enqueued));
        await _jobStore.UpdateState("job-1", JobState.Failed);
        await _jobStore.AddJob(JobFactory.CreateJob("job-2", state: JobState.AwaitingParent));
        await _jobStore.UpdateState("job-2", JobState.Skipped);
        _options.Retention = TimeSpan.Zero;

        await RunSweepAsync();

        (await _jobStore.GetJob("job-1")).Should().NotBeNull();
        (await _jobStore.GetJob("job-2")).Should().NotBeNull();
        await _notifier.DidNotReceive().JobsChanged();
    }
}
