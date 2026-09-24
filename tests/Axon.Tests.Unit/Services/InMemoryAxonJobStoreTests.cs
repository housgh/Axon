using Axon.Core.Enums;
using Axon.Server.Services;
using Axon.Tests.Unit.TestHelpers;
using FluentAssertions;

namespace Axon.Tests.Unit.Services;

public class InMemoryAxonJobStoreTests
{
    private readonly InMemoryAxonJobStore _sut = new();

    [Fact]
    public async Task AddJob_ThenGetJob_ReturnsIt()
    {
        var job = JobFactory.CreateJob("job-1");

        await _sut.AddJob(job);
        var result = await _sut.GetJob("job-1");

        result.Should().BeSameAs(job);
    }

    [Fact]
    public async Task GetJob_Missing_ReturnsNull()
    {
        var result = await _sut.GetJob("missing");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetJobs_FiltersByState()
    {
        await _sut.AddJob(JobFactory.CreateJob("e1", state: JobState.Enqueued));
        await _sut.AddJob(JobFactory.CreateJob("p1", state: JobState.Processing));
        await _sut.AddJob(JobFactory.CreateJob("s1", state: JobState.Succeeded));

        var result = await _sut.GetJobs(take: 20, states: [JobState.Enqueued, JobState.Processing]);

        result.Select(j => j.JobId).Should().BeEquivalentTo(["e1", "p1"]);
    }

    [Fact]
    public async Task GetJobs_RecentHighPriorityJob_DispatchesBeforeRecentLowPriorityJob()
    {
        var now = DateTime.UtcNow.Ticks;
        await _sut.AddJob(JobFactory.CreateJob("low", state: JobState.Enqueued, priority: JobPriority.Low, enqueuedAt: now - TimeSpan.FromMinutes(1).Ticks));
        await _sut.AddJob(JobFactory.CreateJob("high", state: JobState.Enqueued, priority: JobPriority.High, enqueuedAt: now - TimeSpan.FromMinutes(1).Ticks));

        var result = await _sut.GetJobs(take: 20, states: [JobState.Enqueued]);

        var indexOfHigh = result.FindIndex(j => j.JobId == "high");
        var indexOfLow = result.FindIndex(j => j.JobId == "low");
        indexOfHigh.Should().BeLessThan(indexOfLow, "a 1-minute-old High job should dispatch before a 1-minute-old Low job");
    }

    [Fact]
    public async Task GetJobs_OldLowPriorityJob_StillDispatchesBeforeRecentHighPriorityJob()
    {
        // High's boost is 15 min, so a Low job needs to be more than 15 min older than the High
        // job (not exactly 15 min - that's the exact tie point) to win on score; 20 min clears
        // that margin comfortably.
        var now = DateTime.UtcNow.Ticks;
        await _sut.AddJob(JobFactory.CreateJob("recent-high", state: JobState.Enqueued, priority: JobPriority.High, enqueuedAt: now - TimeSpan.FromMinutes(1).Ticks));
        await _sut.AddJob(JobFactory.CreateJob("old-low", state: JobState.Enqueued, priority: JobPriority.Low, enqueuedAt: now - TimeSpan.FromMinutes(20).Ticks));

        var result = await _sut.GetJobs(take: 20, states: [JobState.Enqueued]);

        var indexOfOldLow = result.FindIndex(j => j.JobId == "old-low");
        var indexOfRecentHigh = result.FindIndex(j => j.JobId == "recent-high");
        indexOfOldLow.Should().BeLessThan(indexOfRecentHigh, "a 20-minute-old Low job should still dispatch before a 1-minute-old High job");
    }

    [Fact]
    public async Task TryClaimJob_WhenEnqueued_ClaimsAndSetsDeadline()
    {
        await _sut.AddJob(JobFactory.CreateJob("job-1", state: JobState.Enqueued));

        var claimed = await _sut.TryClaimJob("job-1", processingDeadline: 12345, note: "dispatched");

        claimed.Should().BeTrue();
        var job = await _sut.GetJob("job-1");
        job!.State.Should().Be(JobState.Processing);
        job.ProcessingDeadline.Should().Be(12345);
    }

    [Fact]
    public async Task TryClaimJob_WhenAlreadyProcessing_ReturnsFalseAndLeavesJobUnchanged()
    {
        await _sut.AddJob(JobFactory.CreateJob("job-1", state: JobState.Processing, processingDeadline: 999));

        var claimed = await _sut.TryClaimJob("job-1", processingDeadline: 12345, note: "dispatched");

        claimed.Should().BeFalse();
        var job = await _sut.GetJob("job-1");
        job!.ProcessingDeadline.Should().Be(999);
    }

    [Fact]
    public async Task TryClaimJob_Missing_ReturnsFalse()
    {
        var claimed = await _sut.TryClaimJob("missing", processingDeadline: 12345);

        claimed.Should().BeFalse();
    }

    [Fact]
    public async Task TryClaimJob_ConcurrentCallers_ExactlyOneWins()
    {
        await _sut.AddJob(JobFactory.CreateJob("job-1", state: JobState.Enqueued));

        var results = await Task.WhenAll(Enumerable.Range(0, 50)
            .Select(i => _sut.TryClaimJob("job-1", processingDeadline: i)));

        results.Count(r => r).Should().Be(1);
        var job = await _sut.GetJob("job-1");
        job!.State.Should().Be(JobState.Processing);

        // Exactly two history rows: the initial AddJob entry plus the single winning claim - no
        // torn writes from the losing callers.
        var history = await _sut.GetHistory("job-1");
        history.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetOrphanedProcessingJobs_ReturnsOnlyPastDeadlineProcessingJobs()
    {
        await _sut.AddJob(JobFactory.CreateJob("expired", state: JobState.Processing, processingDeadline: 100));
        await _sut.AddJob(JobFactory.CreateJob("future", state: JobState.Processing, processingDeadline: 999_999));
        await _sut.AddJob(JobFactory.CreateJob("no-deadline", state: JobState.Processing, processingDeadline: null));
        await _sut.AddJob(JobFactory.CreateJob("not-processing", state: JobState.Enqueued, processingDeadline: 50));

        var result = await _sut.GetOrphanedProcessingJobs(asOf: 500);

        result.Select(j => j.JobId).Should().BeEquivalentTo(["expired"]);
    }

    [Fact]
    public async Task RequeueForRetry_IncrementsAttemptsAndSetsScheduled()
    {
        await _sut.AddJob(JobFactory.CreateJob("job-1", state: JobState.Processing, attempts: 1));

        await _sut.RequeueForRetry("job-1", scheduledFor: 555, note: "retry");

        var job = await _sut.GetJob("job-1");
        job!.Attempts.Should().Be(2);
        job.State.Should().Be(JobState.Scheduled);
        job.ScheduledFor.Should().Be(555);
    }

    [Fact]
    public async Task DeleteJob_SoftDeletesJobButKeepsHistory()
    {
        // Soft delete, matching every real (SQL/Mongo) backend's DeleteJob: the job disappears
        // from normal lookups, but its history is left alone (only DeleteCompletedJobsOlderThan's
        // cleanup sweep removes history rows).
        await _sut.AddJob(JobFactory.CreateJob("job-1"));

        await _sut.DeleteJob("job-1");

        (await _sut.GetJob("job-1")).Should().BeNull();
        (await _sut.GetHistory("job-1")).Should().NotBeEmpty();
    }

    [Fact]
    public async Task GetHistory_ReturnsEntriesInTimestampOrder()
    {
        await _sut.AddJob(JobFactory.CreateJob("job-1", state: JobState.Enqueued));
        await _sut.TryClaimJob("job-1", processingDeadline: 1);
        await _sut.UpdateState("job-1", JobState.Succeeded);

        var history = await _sut.GetHistory("job-1");

        history.Select(h => h.State).Should().Equal(JobState.Enqueued, JobState.Processing, JobState.Succeeded);
    }

    [Fact]
    public async Task TryClaimJob_ConcurrencyLimitNotReached_Claims()
    {
        await _sut.AddJob(JobFactory.CreateJob("job-1", state: JobState.Enqueued, concurrencyKey: "email", maxConcurrent: 2));
        await _sut.AddJob(JobFactory.CreateJob("job-2", state: JobState.Processing, concurrencyKey: "email", maxConcurrent: 2));

        var claimed = await _sut.TryClaimJob("job-1", processingDeadline: 999);

        claimed.Should().BeTrue();
    }

    [Fact]
    public async Task TryClaimJob_ConcurrencyLimitReached_ReturnsFalseAndLeavesJobUnclaimed()
    {
        await _sut.AddJob(JobFactory.CreateJob("job-1", state: JobState.Enqueued, concurrencyKey: "email", maxConcurrent: 1));
        await _sut.AddJob(JobFactory.CreateJob("job-2", state: JobState.Processing, concurrencyKey: "email", maxConcurrent: 1));

        var claimed = await _sut.TryClaimJob("job-1", processingDeadline: 999);

        claimed.Should().BeFalse();
        var job = await _sut.GetJob("job-1");
        job!.State.Should().Be(JobState.Enqueued);
    }

    [Fact]
    public async Task TryClaimJob_ConcurrencyLimitOnlyCountsSameKey()
    {
        await _sut.AddJob(JobFactory.CreateJob("job-1", state: JobState.Enqueued, concurrencyKey: "email", maxConcurrent: 1));
        await _sut.AddJob(JobFactory.CreateJob("job-2", state: JobState.Processing, concurrencyKey: "sms", maxConcurrent: 1));

        var claimed = await _sut.TryClaimJob("job-1", processingDeadline: 999);

        claimed.Should().BeTrue();
    }

    [Fact]
    public async Task TryClaimJob_NoConcurrencyKey_IsUnaffectedByOtherProcessingJobs()
    {
        await _sut.AddJob(JobFactory.CreateJob("job-1", state: JobState.Enqueued));
        for (var i = 0; i < 10; i++)
        {
            await _sut.AddJob(JobFactory.CreateJob($"other-{i}", state: JobState.Processing));
        }

        var claimed = await _sut.TryClaimJob("job-1", processingDeadline: 999);

        claimed.Should().BeTrue();
    }

    [Fact]
    public async Task TryClaimJob_ConcurrencyLimitedJobs_ConcurrentCallers_NeverExceedLimit()
    {
        // 10 jobs share a concurrency key with MaxConcurrent=3; racing all 10 claims at once must
        // never let more than 3 end up Processing, mirroring the same race-safety shape as the
        // existing TryClaimJob_ConcurrentCallers_ExactlyOneWins test but for the concurrency guard.
        for (var i = 0; i < 10; i++)
        {
            await _sut.AddJob(JobFactory.CreateJob($"job-{i}", state: JobState.Enqueued, concurrencyKey: "email", maxConcurrent: 3));
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(i => _sut.TryClaimJob($"job-{i}", processingDeadline: i)));

        results.Count(r => r).Should().Be(3);
    }

    [Fact]
    public async Task DeleteCompletedJobsOlderThan_DeletesSucceededJobsPastCutoff()
    {
        await _sut.AddJob(JobFactory.CreateJob("job-1", state: JobState.Enqueued));
        await _sut.UpdateState("job-1", JobState.Succeeded);

        // A cutoff strictly after "now" guarantees the job's most recent history entry
        // (appended just above) is older than it.
        var cutoff = DateTime.UtcNow.AddSeconds(1).Ticks;
        var deleted = await _sut.DeleteCompletedJobsOlderThan(cutoff);

        deleted.Should().Be(1);
        (await _sut.GetJob("job-1")).Should().BeNull();
        (await _sut.GetHistory("job-1")).Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteCompletedJobsOlderThan_KeepsJobsNewerThanCutoff()
    {
        await _sut.AddJob(JobFactory.CreateJob("job-1", state: JobState.Enqueued));
        await _sut.UpdateState("job-1", JobState.Succeeded);

        // A cutoff in the distant past guarantees the job's history is newer than it.
        var deleted = await _sut.DeleteCompletedJobsOlderThan(cutoff: 0);

        deleted.Should().Be(0);
        (await _sut.GetJob("job-1")).Should().NotBeNull();
    }

    [Theory]
    [InlineData(JobState.Enqueued)]
    [InlineData(JobState.Scheduled)]
    [InlineData(JobState.Processing)]
    [InlineData(JobState.AwaitingParent)]
    public async Task DeleteCompletedJobsOlderThan_NeverDeletesNonTerminalJobs(JobState nonTerminalState)
    {
        await _sut.AddJob(JobFactory.CreateJob("job-1", state: nonTerminalState));

        var deleted = await _sut.DeleteCompletedJobsOlderThan(cutoff: DateTime.UtcNow.AddYears(1).Ticks);

        deleted.Should().Be(0);
        (await _sut.GetJob("job-1")).Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteCompletedJobsOlderThan_NeverDeletesSkippedJobs()
    {
        await _sut.AddJob(JobFactory.CreateJob("job-1", state: JobState.AwaitingParent));
        await _sut.UpdateState("job-1", JobState.Skipped);

        var deleted = await _sut.DeleteCompletedJobsOlderThan(DateTime.UtcNow.AddYears(1).Ticks);

        deleted.Should().Be(0);
        (await _sut.GetJob("job-1")).Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteCompletedJobsOlderThan_NeverDeletesFailedJobs()
    {
        await _sut.AddJob(JobFactory.CreateJob("job-1", state: JobState.Enqueued));
        await _sut.UpdateState("job-1", JobState.Failed);

        var deleted = await _sut.DeleteCompletedJobsOlderThan(DateTime.UtcNow.AddYears(1).Ticks);

        deleted.Should().Be(0);
        (await _sut.GetJob("job-1")).Should().NotBeNull();
    }

    [Fact]
    public async Task CountJobsByState_CountsSoftDeletedSucceededJobs()
    {
        await _sut.AddJob(JobFactory.CreateJob("job-1", state: JobState.Enqueued));
        await _sut.UpdateState("job-1", JobState.Succeeded);

        var deleted = await _sut.DeleteCompletedJobsOlderThan(DateTime.UtcNow.AddSeconds(1).Ticks);
        var counts = await _sut.CountJobsByState();

        deleted.Should().Be(1);
        counts[JobState.Succeeded].Should().Be(1);
        (await _sut.GetJob("job-1")).Should().BeNull();
    }

    [Fact]
    public async Task CountJobsByState_CountsLiveJobsPerState()
    {
        await _sut.AddJob(JobFactory.CreateJob("job-1", state: JobState.Enqueued));
        await _sut.AddJob(JobFactory.CreateJob("job-2", state: JobState.Enqueued));
        await _sut.UpdateState("job-2", JobState.Succeeded);

        var counts = await _sut.CountJobsByState();

        counts[JobState.Enqueued].Should().Be(1);
        counts[JobState.Succeeded].Should().Be(1);
    }

    [Fact]
    public async Task DeleteJob_SoftDeletesButStillCountsForStats()
    {
        await _sut.AddJob(JobFactory.CreateJob("job-1", state: JobState.Enqueued));
        await _sut.UpdateState("job-1", JobState.Succeeded);

        await _sut.DeleteJob("job-1");
        var counts = await _sut.CountJobsByState();

        (await _sut.GetJob("job-1")).Should().BeNull();
        counts[JobState.Succeeded].Should().Be(1);
    }
}
