using Axon.Core.Enums;
using Axon.Core.Models;
using Axon.Server.Interfaces;
using Axon.Server.Services;
using Axon.Tests.Unit.TestHelpers;
using FluentAssertions;
using NSubstitute;

namespace Axon.Tests.Unit.Services;

public class AxonJobServiceTests
{
    private readonly IAxonJobStore _jobStore = Substitute.For<IAxonJobStore>();
    private readonly IAxonDashboardNotifier _notifier = Substitute.For<IAxonDashboardNotifier>();
    private readonly AxonJobService _sut;

    public AxonJobServiceTests()
    {
        _sut = new AxonJobService(_jobStore, _notifier);
        // Every terminal-state transition (success, or the final failure) now checks for waiting
        // continuations; default to none so tests unrelated to continuations don't each need to
        // stub this individually.
        _jobStore.GetContinuationsWaitingOn(Arg.Any<string>()).Returns([]);
    }

    [Fact]
    public async Task EnqueueAsync_WithoutSchedule_AddsJobAsEnqueued()
    {
        var jobInfo = new JobInfo { MethodName = "M", Assembly = "A", DeclaringType = "T", Arguments = [] };

        await _sut.EnqueueAsync("device-1", "job-1", jobInfo, scheduledFor: null);

        await _jobStore.Received(1).AddJob(Arg.Is<Job>(j =>
            j.JobId == "job-1" &&
            j.DeviceName == "device-1" &&
            j.State == JobState.Enqueued &&
            j.ScheduledFor == null));
        await _notifier.Received(1).JobsChanged();
    }

    [Fact]
    public async Task EnqueueAsync_SetsEnqueuedAtToNow()
    {
        var jobInfo = new JobInfo { MethodName = "M", Assembly = "A", DeclaringType = "T", Arguments = [] };
        var before = DateTime.UtcNow.Ticks;

        await _sut.EnqueueAsync("device-1", "job-1", jobInfo, scheduledFor: null);

        var after = DateTime.UtcNow.Ticks;
        await _jobStore.Received(1).AddJob(Arg.Is<Job>(j => j.EnqueuedAt >= before && j.EnqueuedAt <= after));
    }

    [Fact]
    public async Task EnqueueAsync_WithSchedule_AddsJobAsScheduled()
    {
        var jobInfo = new JobInfo { MethodName = "M", Assembly = "A", DeclaringType = "T", Arguments = [] };
        var scheduledFor = DateTime.UtcNow.AddMinutes(5).Ticks;

        await _sut.EnqueueAsync("device-1", "job-1", jobInfo, scheduledFor);

        await _jobStore.Received(1).AddJob(Arg.Is<Job>(j =>
            j.State == JobState.Scheduled &&
            j.ScheduledFor == scheduledFor));
    }

    [Fact]
    public async Task MarkSucceededAsync_UpdatesStateAndNotifies()
    {
        await _sut.MarkSucceededAsync("job-1");

        await _jobStore.Received(1).UpdateState("job-1", JobState.Succeeded, Arg.Any<string?>());
        await _notifier.Received(1).JobsChanged();
    }

    [Fact]
    public async Task MarkFailedAsync_JobNotFound_DoesNothing()
    {
        _jobStore.GetJob("missing").Returns((Job?)null);

        await _sut.MarkFailedAsync("missing", "boom");

        await _jobStore.DidNotReceive().RecordFailure(Arg.Any<string>(), Arg.Any<string?>());
        await _notifier.DidNotReceive().JobsChanged();
    }

    [Fact]
    public async Task MarkFailedAsync_BelowMaxAttempts_RequeuesForRetry()
    {
        var job = JobFactory.CreateJob("job-1", state: JobState.Processing, attempts: 0, maxAttempts: 3);
        _jobStore.GetJob("job-1").Returns(job);

        await _sut.MarkFailedAsync("job-1", "boom");

        await _jobStore.Received(1).RecordFailure("job-1", "boom");
        await _jobStore.Received(1).RequeueForRetry("job-1", Arg.Any<long>(), Arg.Any<string?>());
        await _jobStore.DidNotReceive().UpdateState(Arg.Any<string>(), JobState.Failed, Arg.Any<string?>());
        await _notifier.Received(1).JobsChanged();
    }

    [Fact]
    public async Task MarkFailedAsync_AtMaxAttempts_MarksFailed()
    {
        // Attempts already reflects this failed attempt (matching how RequeueForRetry increments
        // it), so Attempts == MaxAttempts - 1 is the last one that still fits under MaxAttempts.
        var job = JobFactory.CreateJob("job-1", state: JobState.Processing, attempts: 2, maxAttempts: 3);
        _jobStore.GetJob("job-1").Returns(job);

        await _sut.MarkFailedAsync("job-1", "boom");

        await _jobStore.Received(1).UpdateState("job-1", JobState.Failed, Arg.Any<string?>());
        await _jobStore.DidNotReceive().RequeueForRetry(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<string?>());
        await _notifier.Received(1).JobsChanged();
    }

    [Fact]
    public async Task ReclaimOrphanedAsync_BelowMaxAttempts_RecordsOrphanNoteAndRetries()
    {
        var job = JobFactory.CreateJob("job-1", state: JobState.Processing, attempts: 0, maxAttempts: 3);

        await _sut.ReclaimOrphanedAsync(job);

        await _jobStore.Received(1).RecordFailure("job-1", Arg.Is<string>(s => s.Contains("orphaned")));
        await _jobStore.Received(1).RequeueForRetry("job-1", Arg.Any<long>(), Arg.Any<string?>());
        await _notifier.Received(1).JobsChanged();
    }

    [Fact]
    public async Task ReclaimOrphanedAsync_AtMaxAttempts_MarksFailed()
    {
        var job = JobFactory.CreateJob("job-1", state: JobState.Processing, attempts: 2, maxAttempts: 3);

        await _sut.ReclaimOrphanedAsync(job);

        await _jobStore.Received(1).UpdateState("job-1", JobState.Failed, Arg.Any<string?>());
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    public async Task MarkFailedAsync_RetryBackoff_GrowsWithAttempts(int attemptsSoFar, int _)
    {
        // MaxAttempts high enough that every case in this theory retries rather than fails,
        // so we can observe RequeueForRetry's scheduledFor growing with each attempt.
        var job = JobFactory.CreateJob("job-1", state: JobState.Processing, attempts: attemptsSoFar, maxAttempts: 10);
        _jobStore.GetJob("job-1").Returns(job);
        var before = DateTime.UtcNow.Ticks;

        await _sut.MarkFailedAsync("job-1", "boom");

        await _jobStore.Received(1).RequeueForRetry("job-1", Arg.Is<long>(scheduledFor => scheduledFor > before), Arg.Any<string?>());
    }

    [Fact]
    public async Task EnqueueAsync_WithRetryPolicy_OverridesDefaultMaxAttempts()
    {
        var jobInfo = new JobInfo
        {
            MethodName = "M", Assembly = "A", DeclaringType = "T", Arguments = [],
            RetryPolicy = new AxonRetryPolicy { MaxAttempts = 7, RetryDelaysSeconds = [1, 2, 3] }
        };

        await _sut.EnqueueAsync("device-1", "job-1", jobInfo, scheduledFor: null);

        await _jobStore.Received(1).AddJob(Arg.Is<Job>(j => j.MaxAttempts == 7));
    }

    [Fact]
    public async Task EnqueueAsync_WithoutRetryPolicy_KeepsDefaultMaxAttempts()
    {
        var jobInfo = new JobInfo { MethodName = "M", Assembly = "A", DeclaringType = "T", Arguments = [] };

        await _sut.EnqueueAsync("device-1", "job-1", jobInfo, scheduledFor: null);

        await _jobStore.Received(1).AddJob(Arg.Is<Job>(j => j.MaxAttempts == 3));
    }

    [Fact]
    public async Task MarkFailedAsync_WithCustomRetryDelays_UsesConfiguredDelayForAttempt()
    {
        var job = JobFactory.CreateJob("job-1", state: JobState.Processing, attempts: 0, maxAttempts: 10,
            retryPolicy: new AxonRetryPolicy { MaxAttempts = 10, RetryDelaysSeconds = [5, 50, 500] });
        _jobStore.GetJob("job-1").Returns(job);
        var before = DateTime.UtcNow;

        await _sut.MarkFailedAsync("job-1", "boom");

        await _jobStore.Received(1).RequeueForRetry("job-1",
            Arg.Is<long>(scheduledFor => new DateTime(scheduledFor) >= before.AddSeconds(5) && new DateTime(scheduledFor) < before.AddSeconds(50)),
            Arg.Any<string?>());
    }

    [Fact]
    public async Task MarkFailedAsync_CustomRetryDelaysExhausted_ReusesLastDelay()
    {
        // Attempts=2 is past the 2 entries in RetryDelaysSeconds (indices 0,1), so it should
        // fall back to the last entry (index 1 = 50s) rather than throwing or using the default.
        var job = JobFactory.CreateJob("job-1", state: JobState.Processing, attempts: 2, maxAttempts: 10,
            retryPolicy: new AxonRetryPolicy { MaxAttempts = 10, RetryDelaysSeconds = [5, 50] });
        _jobStore.GetJob("job-1").Returns(job);
        var before = DateTime.UtcNow;

        await _sut.MarkFailedAsync("job-1", "boom");

        await _jobStore.Received(1).RequeueForRetry("job-1",
            Arg.Is<long>(scheduledFor => new DateTime(scheduledFor) >= before.AddSeconds(50) && new DateTime(scheduledFor) < before.AddSeconds(60)),
            Arg.Any<string?>());
    }

    [Fact]
    public async Task EnqueueContinuationAsync_AddsJobInAwaitingParentState()
    {
        var jobInfo = new JobInfo { MethodName = "M", Assembly = "A", DeclaringType = "T", Arguments = [] };

        await _sut.EnqueueContinuationAsync("device-1", "job-2", jobInfo, "job-1", continueOnParentFailure: false);

        await _jobStore.Received(1).AddJob(Arg.Is<Job>(j =>
            j.JobId == "job-2" &&
            j.State == JobState.AwaitingParent &&
            j.ParentJobId == "job-1" &&
            j.ContinueOnParentFailure == false));
    }

    [Fact]
    public async Task EnqueueContinuationAsync_ParentAlreadySucceeded_PromotesImmediately()
    {
        // The parent may finish before the continuation is even created; it must not be left
        // waiting forever for a transition that already happened. In a real store, GetJob and
        // GetContinuationsWaitingOn would both reflect the AddJob call above; stub both here since
        // the mock doesn't persist state between calls.
        var jobInfo = new JobInfo { MethodName = "M", Assembly = "A", DeclaringType = "T", Arguments = [] };
        _jobStore.GetJob("job-1").Returns(JobFactory.CreateJob("job-1", state: JobState.Succeeded));
        var continuation = JobFactory.CreateJob("job-2", state: JobState.AwaitingParent);
        continuation.ParentJobId = "job-1";
        _jobStore.GetContinuationsWaitingOn("job-1").Returns([continuation]);

        await _sut.EnqueueContinuationAsync("device-1", "job-2", jobInfo, "job-1", continueOnParentFailure: false);

        await _jobStore.Received(1).Requeue("job-2", Arg.Any<string>());
    }

    [Fact]
    public async Task EnqueueContinuationAsync_ParentStillRunning_LeavesContinuationWaiting()
    {
        var jobInfo = new JobInfo { MethodName = "M", Assembly = "A", DeclaringType = "T", Arguments = [] };
        _jobStore.GetJob("job-1").Returns(JobFactory.CreateJob("job-1", state: JobState.Processing));

        await _sut.EnqueueContinuationAsync("device-1", "job-2", jobInfo, "job-1", continueOnParentFailure: false);

        await _jobStore.DidNotReceive().Requeue(Arg.Any<string>(), Arg.Any<string>());
        await _jobStore.DidNotReceive().UpdateState("job-2", JobState.Skipped, Arg.Any<string?>());
    }

    [Fact]
    public async Task MarkSucceededAsync_PromotesWaitingContinuation()
    {
        _jobStore.GetJob("job-1").Returns(JobFactory.CreateJob("job-1", state: JobState.Succeeded));
        var continuation = JobFactory.CreateJob("job-2", state: JobState.AwaitingParent);
        continuation.ParentJobId = "job-1";
        _jobStore.GetContinuationsWaitingOn("job-1").Returns([continuation]);

        await _sut.MarkSucceededAsync("job-1");

        await _jobStore.Received(1).Requeue("job-2", Arg.Any<string>());
    }

    [Fact]
    public async Task MarkFailedAsync_TerminalFailure_SkipsContinuationByDefault()
    {
        var job = JobFactory.CreateJob("job-1", state: JobState.Processing, attempts: 2, maxAttempts: 3);
        _jobStore.GetJob("job-1").Returns(job);
        var continuation = JobFactory.CreateJob("job-2", state: JobState.AwaitingParent);
        continuation.ParentJobId = "job-1";
        continuation.ContinueOnParentFailure = false;
        _jobStore.GetContinuationsWaitingOn("job-1").Returns([continuation]);

        await _sut.MarkFailedAsync("job-1", "boom");

        await _jobStore.Received(1).UpdateState("job-2", JobState.Skipped, Arg.Any<string?>());
        await _jobStore.DidNotReceive().Requeue("job-2", Arg.Any<string>());
    }

    [Fact]
    public async Task MarkFailedAsync_TerminalFailure_RunsContinuationWhenConfiguredToContinueOnFailure()
    {
        var job = JobFactory.CreateJob("job-1", state: JobState.Processing, attempts: 2, maxAttempts: 3);
        _jobStore.GetJob("job-1").Returns(job);
        var continuation = JobFactory.CreateJob("job-2", state: JobState.AwaitingParent);
        continuation.ParentJobId = "job-1";
        continuation.ContinueOnParentFailure = true;
        _jobStore.GetContinuationsWaitingOn("job-1").Returns([continuation]);

        await _sut.MarkFailedAsync("job-1", "boom");

        await _jobStore.Received(1).Requeue("job-2", Arg.Any<string>());
        await _jobStore.DidNotReceive().UpdateState("job-2", JobState.Skipped, Arg.Any<string?>());
    }

    [Fact]
    public async Task MarkFailedAsync_RetryNotYetExhausted_DoesNotResolveContinuationsYet()
    {
        // Continuations only resolve once the parent reaches an actual terminal state - a retry
        // (not yet the final failure) must not prematurely promote or skip anything.
        var job = JobFactory.CreateJob("job-1", state: JobState.Processing, attempts: 0, maxAttempts: 3);
        _jobStore.GetJob("job-1").Returns(job);

        await _sut.MarkFailedAsync("job-1", "boom");

        await _jobStore.DidNotReceive().GetContinuationsWaitingOn(Arg.Any<string>());
    }
}
