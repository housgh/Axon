using Axon.Core.Enums;
using Axon.Core.Models;
using Axon.Server.Services;
using FluentAssertions;
using NSubstitute;

namespace Axon.Tests.Unit.Services;

/// <summary>
/// Exercises job continuations against the real InMemoryAxonJobStore (rather than a mocked
/// IAxonJobStore) so the whole AddJob -> GetJob -> GetContinuationsWaitingOn -> Requeue/
/// UpdateState chain is proven correct together, not just each call verified in isolation.
/// </summary>
public class AxonJobServiceContinuationTests
{
    private readonly InMemoryAxonJobStore _jobStore = new();
    private readonly IAxonDashboardNotifier _notifier = Substitute.For<IAxonDashboardNotifier>();
    private readonly AxonJobService _sut;

    public AxonJobServiceContinuationTests()
    {
        _sut = new AxonJobService(_jobStore, _notifier);
    }

    private static JobInfo NewJobInfo() => new() { MethodName = "M", Assembly = "A", DeclaringType = "T", Arguments = [] };

    [Fact]
    public async Task ContinuationPromotedWhenParentSucceeds()
    {
        await _sut.EnqueueAsync("device-1", "parent", NewJobInfo(), scheduledFor: null);
        await _sut.EnqueueContinuationAsync("device-1", "child", NewJobInfo(), "parent", continueOnParentFailure: false);

        (await _jobStore.GetJob("child"))!.State.Should().Be(JobState.AwaitingParent);

        await _sut.MarkSucceededAsync("parent");

        var child = await _jobStore.GetJob("child");
        child!.State.Should().Be(JobState.Enqueued);
    }

    [Fact]
    public async Task ContinuationSkippedWhenParentFailsAndNotConfiguredToContinue()
    {
        await _sut.EnqueueAsync("device-1", "parent", NewJobInfo(), scheduledFor: null);
        await _sut.EnqueueContinuationAsync("device-1", "child", NewJobInfo(), "parent", continueOnParentFailure: false);

        // Drive parent to terminal Failed (MaxAttempts default is 3; fail 3 times).
        for (var i = 0; i < 3; i++)
        {
            await _sut.MarkFailedAsync("parent", "boom");
        }

        var parent = await _jobStore.GetJob("parent");
        parent!.State.Should().Be(JobState.Failed);

        var child = await _jobStore.GetJob("child");
        child!.State.Should().Be(JobState.Skipped);
    }

    [Fact]
    public async Task ContinuationRunsWhenParentFailsAndConfiguredToContinue()
    {
        await _sut.EnqueueAsync("device-1", "parent", NewJobInfo(), scheduledFor: null);
        await _sut.EnqueueContinuationAsync("device-1", "child", NewJobInfo(), "parent", continueOnParentFailure: true);

        for (var i = 0; i < 3; i++)
        {
            await _sut.MarkFailedAsync("parent", "boom");
        }

        var child = await _jobStore.GetJob("child");
        child!.State.Should().Be(JobState.Enqueued);
    }

    [Fact]
    public async Task ContinuationCreatedAfterParentAlreadySucceeded_PromotesImmediately()
    {
        await _sut.EnqueueAsync("device-1", "parent", NewJobInfo(), scheduledFor: null);
        await _sut.MarkSucceededAsync("parent");

        await _sut.EnqueueContinuationAsync("device-1", "child", NewJobInfo(), "parent", continueOnParentFailure: false);

        var child = await _jobStore.GetJob("child");
        child!.State.Should().Be(JobState.Enqueued);
    }

    [Fact]
    public async Task ContinuationCreatedAfterParentAlreadyFailed_SkippedImmediately()
    {
        await _sut.EnqueueAsync("device-1", "parent", NewJobInfo(), scheduledFor: null);
        for (var i = 0; i < 3; i++)
        {
            await _sut.MarkFailedAsync("parent", "boom");
        }

        await _sut.EnqueueContinuationAsync("device-1", "child", NewJobInfo(), "parent", continueOnParentFailure: false);

        var child = await _jobStore.GetJob("child");
        child!.State.Should().Be(JobState.Skipped);
    }

    [Fact]
    public async Task MultipleContinuationsOnSameParent_AllResolveIndependently()
    {
        await _sut.EnqueueAsync("device-1", "parent", NewJobInfo(), scheduledFor: null);
        await _sut.EnqueueContinuationAsync("device-1", "child-run", NewJobInfo(), "parent", continueOnParentFailure: true);
        await _sut.EnqueueContinuationAsync("device-1", "child-skip", NewJobInfo(), "parent", continueOnParentFailure: false);

        for (var i = 0; i < 3; i++)
        {
            await _sut.MarkFailedAsync("parent", "boom");
        }

        (await _jobStore.GetJob("child-run"))!.State.Should().Be(JobState.Enqueued);
        (await _jobStore.GetJob("child-skip"))!.State.Should().Be(JobState.Skipped);
    }

    [Fact]
    public async Task ContinuationChain_PromotesSecondContinuationWhenFirstSucceeds()
    {
        // A -> B -> C: C only becomes runnable once B succeeds, which itself only happens once A
        // succeeds.
        await _sut.EnqueueAsync("device-1", "a", NewJobInfo(), scheduledFor: null);
        await _sut.EnqueueContinuationAsync("device-1", "b", NewJobInfo(), "a", continueOnParentFailure: false);
        await _sut.EnqueueContinuationAsync("device-1", "c", NewJobInfo(), "b", continueOnParentFailure: false);

        (await _jobStore.GetJob("c"))!.State.Should().Be(JobState.AwaitingParent);

        await _sut.MarkSucceededAsync("a");
        (await _jobStore.GetJob("b"))!.State.Should().Be(JobState.Enqueued);
        (await _jobStore.GetJob("c"))!.State.Should().Be(JobState.AwaitingParent, "b hasn't succeeded yet");

        await _sut.MarkSucceededAsync("b");
        (await _jobStore.GetJob("c"))!.State.Should().Be(JobState.Enqueued);
    }

    [Fact]
    public async Task ContinuationRetryBeforeTerminalFailure_DoesNotPrematurelyResolveContinuations()
    {
        await _sut.EnqueueAsync("device-1", "parent", NewJobInfo(), scheduledFor: null);
        await _sut.EnqueueContinuationAsync("device-1", "child", NewJobInfo(), "parent", continueOnParentFailure: false);

        // First failure retries (MaxAttempts=3), not yet terminal.
        await _sut.MarkFailedAsync("parent", "boom");

        (await _jobStore.GetJob("parent"))!.State.Should().Be(JobState.Scheduled);
        (await _jobStore.GetJob("child"))!.State.Should().Be(JobState.AwaitingParent);
    }
}
