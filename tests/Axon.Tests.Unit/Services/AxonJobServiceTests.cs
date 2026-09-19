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
}
