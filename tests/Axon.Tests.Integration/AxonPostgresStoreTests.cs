using System.Text.Json;
using Axon.Core.Enums;
using Axon.Core.Models;
using Axon.Postgres;
using Axon.Tests.Integration.TestHelpers;
using FluentAssertions;

namespace Axon.Tests.Integration;

[Collection(PostgresCollection.Name)]
public class AxonPostgresStoreTests(PostgresFixture fixture)
{
    private AxonPostgresStore CreateSut() => new(fixture.ConnectionString);

    [Fact]
    public async Task AddJob_ThenGetJob_RoundTripsAllFields()
    {
        var sut = CreateSut();
        var job = JobFactory.CreateJob();

        await sut.AddJob(job);
        var result = await sut.GetJob(job.JobId);

        result.Should().NotBeNull();
        result!.DeviceName.Should().Be(job.DeviceName);
        result.MethodName.Should().Be(job.MethodName);
        result.Assembly.Should().Be(job.Assembly);
        result.DeclaringType.Should().Be(job.DeclaringType);
        // JobArgumentsTypeHandler round-trips arguments through JSON, so they come back as
        // JsonElement rather than the original CLR types - compare via their JSON representation.
        JsonSerializer.Serialize(result.Arguments).Should().Be(JsonSerializer.Serialize(job.Arguments));
        result.State.Should().Be(JobState.Enqueued);
    }

    [Fact]
    public async Task AddJob_WithRetryPolicy_RoundTripsThroughPostgres()
    {
        var sut = CreateSut();
        var job = JobFactory.CreateJob(retryPolicy: new AxonRetryPolicy { MaxAttempts = 9, RetryDelaysSeconds = [5, 15, 45] });

        await sut.AddJob(job);
        var result = await sut.GetJob(job.JobId);

        result!.RetryPolicy.Should().NotBeNull();
        result.RetryPolicy!.MaxAttempts.Should().Be(9);
        result.RetryPolicy.RetryDelaysSeconds.Should().Equal(5, 15, 45);
    }

    [Fact]
    public async Task AddJob_WithoutRetryPolicy_RetryPolicyIsNullAfterRoundTrip()
    {
        var sut = CreateSut();
        var job = JobFactory.CreateJob();

        await sut.AddJob(job);
        var result = await sut.GetJob(job.JobId);

        result!.RetryPolicy.Should().BeNull();
    }

    [Fact]
    public async Task AddJob_WithContinuationFields_RoundTripsThroughPostgres()
    {
        var sut = CreateSut();
        var job = JobFactory.CreateJob(state: JobState.AwaitingParent, parentJobId: "parent-123", continueOnParentFailure: true);

        await sut.AddJob(job);
        var result = await sut.GetJob(job.JobId);

        result!.State.Should().Be(JobState.AwaitingParent);
        result.ParentJobId.Should().Be("parent-123");
        result.ContinueOnParentFailure.Should().BeTrue();
    }

    [Fact]
    public async Task GetContinuationsWaitingOn_ReturnsOnlyAwaitingParentJobsForThatParent()
    {
        var sut = CreateSut();
        var parentId = Guid.NewGuid().ToString();
        var otherParentId = Guid.NewGuid().ToString();
        var waiting = JobFactory.CreateJob(state: JobState.AwaitingParent, parentJobId: parentId);
        var alreadyPromoted = JobFactory.CreateJob(state: JobState.Enqueued, parentJobId: parentId);
        var waitingOnOther = JobFactory.CreateJob(state: JobState.AwaitingParent, parentJobId: otherParentId);
        await sut.AddJob(waiting);
        await sut.AddJob(alreadyPromoted);
        await sut.AddJob(waitingOnOther);

        var result = await sut.GetContinuationsWaitingOn(parentId);

        result.Select(j => j.JobId).Should().BeEquivalentTo([waiting.JobId]);
    }

    [Fact]
    public async Task AddJob_AlsoWritesInitialHistoryEntry()
    {
        var sut = CreateSut();
        var job = JobFactory.CreateJob(state: JobState.Enqueued);

        await sut.AddJob(job);
        var history = await sut.GetHistory(job.JobId);

        history.Should().ContainSingle(h => h.State == JobState.Enqueued);
    }

    [Fact]
    public async Task DeleteJob_SoftDeletes_ExcludedFromGetJobAndGetJobs()
    {
        var sut = CreateSut();
        var job = JobFactory.CreateJob();
        await sut.AddJob(job);

        await sut.DeleteJob(job.JobId);

        (await sut.GetJob(job.JobId)).Should().BeNull();
        var jobs = await sut.GetJobs(take: 1000);
        jobs.Should().NotContain(j => j.JobId == job.JobId);
    }

    [Fact]
    public async Task GetJobs_FiltersByState()
    {
        var sut = CreateSut();
        var enqueued = JobFactory.CreateJob(state: JobState.Enqueued);
        var succeeded = JobFactory.CreateJob(state: JobState.Succeeded);
        await sut.AddJob(enqueued);
        await sut.AddJob(succeeded);

        var result = await sut.GetJobs(take: 1000, states: [JobState.Enqueued]);

        result.Select(j => j.JobId).Should().Contain(enqueued.JobId);
        result.Select(j => j.JobId).Should().NotContain(succeeded.JobId);
    }

    [Fact]
    public async Task GetJobs_RecentHighPriorityJob_DispatchesBeforeRecentLowPriorityJob()
    {
        var sut = CreateSut();
        var now = DateTime.UtcNow.Ticks;
        var high = JobFactory.CreateJob(priority: JobPriority.High, enqueuedAt: now - TimeSpan.FromMinutes(1).Ticks);
        var low = JobFactory.CreateJob(priority: JobPriority.Low, enqueuedAt: now - TimeSpan.FromMinutes(1).Ticks);
        await sut.AddJob(low);
        await sut.AddJob(high);

        var result = await sut.GetJobs(take: 1000, states: [JobState.Enqueued]);

        var indexOfHigh = result.FindIndex(j => j.JobId == high.JobId);
        var indexOfLow = result.FindIndex(j => j.JobId == low.JobId);
        indexOfHigh.Should().BeLessThan(indexOfLow, "a 1-minute-old High job should dispatch before a 1-minute-old Low job");
    }

    [Fact]
    public async Task GetJobs_OldLowPriorityJob_StillDispatchesBeforeRecentHighPriorityJob()
    {
        // High's boost is 15 min, so a Low job needs to be more than 15 min older than the High
        // job (not exactly 15 min - that's the exact tie point) to win on score; 20 min clears
        // that margin comfortably.
        var sut = CreateSut();
        var now = DateTime.UtcNow.Ticks;
        var recentHigh = JobFactory.CreateJob(priority: JobPriority.High, enqueuedAt: now - TimeSpan.FromMinutes(1).Ticks);
        var oldLow = JobFactory.CreateJob(priority: JobPriority.Low, enqueuedAt: now - TimeSpan.FromMinutes(20).Ticks);
        await sut.AddJob(recentHigh);
        await sut.AddJob(oldLow);

        var result = await sut.GetJobs(take: 1000, states: [JobState.Enqueued]);

        var indexOfOldLow = result.FindIndex(j => j.JobId == oldLow.JobId);
        var indexOfRecentHigh = result.FindIndex(j => j.JobId == recentHigh.JobId);
        indexOfOldLow.Should().BeLessThan(indexOfRecentHigh, "a 20-minute-old Low job should still dispatch before a 1-minute-old High job");
    }

    [Fact]
    public async Task UpdateState_ChangesStateAndAppendsHistory()
    {
        var sut = CreateSut();
        var job = JobFactory.CreateJob();
        await sut.AddJob(job);

        await sut.UpdateState(job.JobId, JobState.Succeeded, "done");

        var result = await sut.GetJob(job.JobId);
        result!.State.Should().Be(JobState.Succeeded);
        var history = await sut.GetHistory(job.JobId);
        history.Should().Contain(h => h.State == JobState.Succeeded && h.Note == "done");
    }

    [Fact]
    public async Task RequeueForRetry_IncrementsAttemptsSetsScheduledState()
    {
        var sut = CreateSut();
        var job = JobFactory.CreateJob(state: JobState.Processing, attempts: 1);
        await sut.AddJob(job);

        await sut.RequeueForRetry(job.JobId, scheduledFor: 999_999, note: "retry");

        var result = await sut.GetJob(job.JobId);
        result!.State.Should().Be(JobState.Scheduled);
        result.Attempts.Should().Be(2);
        result.ScheduledFor.Should().Be(999_999);
    }

    [Fact]
    public async Task TryClaimJob_WhenEnqueued_ClaimsAndAppendsHistory()
    {
        var sut = CreateSut();
        var job = JobFactory.CreateJob(state: JobState.Enqueued);
        await sut.AddJob(job);

        var claimed = await sut.TryClaimJob(job.JobId, processingDeadline: 123456, note: "dispatched");

        claimed.Should().BeTrue();
        var result = await sut.GetJob(job.JobId);
        result!.State.Should().Be(JobState.Processing);
        result.ProcessingDeadline.Should().Be(123456);
        var history = await sut.GetHistory(job.JobId);
        history.Should().Contain(h => h.State == JobState.Processing && h.Note == "dispatched");
    }

    [Fact]
    public async Task TryClaimJob_WhenAlreadyProcessing_ReturnsFalseWithoutChangingJob()
    {
        var sut = CreateSut();
        var job = JobFactory.CreateJob(state: JobState.Processing);
        await sut.AddJob(job);
        // Job was inserted directly as Processing (no prior claim), so this call must be rejected
        // by the State IN (Enqueued, Scheduled) guard.
        var claimed = await sut.TryClaimJob(job.JobId, processingDeadline: 999);

        claimed.Should().BeFalse();
    }

    [Fact]
    public async Task TryClaimJob_ConcurrentCallersAgainstRealPostgres_ExactlyOneWins()
    {
        // This is the core distributed-dispatch safety guarantee: simulates multiple
        // Axon.Server instances racing to claim the same due job via the shared Postgres store.
        var sut = CreateSut();
        var job = JobFactory.CreateJob(state: JobState.Enqueued);
        await sut.AddJob(job);

        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(i => sut.TryClaimJob(job.JobId, processingDeadline: i)));

        results.Count(r => r).Should().Be(1, "only one instance's claim may succeed for a given job");

        var result = await sut.GetJob(job.JobId);
        result!.State.Should().Be(JobState.Processing);

        // Exactly two history rows: the initial AddJob entry plus the single winning claim - no
        // torn writes or double-appends from the 19 losing callers.
        var history = await sut.GetHistory(job.JobId);
        history.Should().HaveCount(2);
    }

    [Fact]
    public async Task TryClaimJob_ConcurrencyLimitNotReached_ClaimsAgainstRealPostgres()
    {
        // ConcurrencyKey must be unique per test: the limit is a COUNT(*) over every row sharing
        // the key, in a database shared across all tests in this fixture, so a fixed literal key
        // (unlike JobId, which is already a fresh GUID per test) would let one test's leftover
        // Processing rows pollute another test's count.
        var concurrencyKey = Guid.NewGuid().ToString();
        var sut = CreateSut();
        var limited = JobFactory.CreateJob(state: JobState.Enqueued, concurrencyKey: concurrencyKey, maxConcurrent: 2);
        var alreadyProcessing = JobFactory.CreateJob(state: JobState.Processing, concurrencyKey: concurrencyKey, maxConcurrent: 2);
        await sut.AddJob(limited);
        await sut.AddJob(alreadyProcessing);

        var claimed = await sut.TryClaimJob(limited.JobId, processingDeadline: 999);

        claimed.Should().BeTrue();
    }

    [Fact]
    public async Task TryClaimJob_ConcurrencyLimitReached_RejectsAgainstRealPostgres()
    {
        var concurrencyKey = Guid.NewGuid().ToString();
        var sut = CreateSut();
        var limited = JobFactory.CreateJob(state: JobState.Enqueued, concurrencyKey: concurrencyKey, maxConcurrent: 1);
        var alreadyProcessing = JobFactory.CreateJob(state: JobState.Processing, concurrencyKey: concurrencyKey, maxConcurrent: 1);
        await sut.AddJob(limited);
        await sut.AddJob(alreadyProcessing);

        var claimed = await sut.TryClaimJob(limited.JobId, processingDeadline: 999);

        claimed.Should().BeFalse();
        var result = await sut.GetJob(limited.JobId);
        result!.State.Should().Be(JobState.Enqueued);
    }

    [Fact]
    public async Task TryClaimJob_ConcurrencyLimitedJobs_ConcurrentCallersAgainstRealPostgres_NeverExceedLimit()
    {
        // The core correctness claim for this feature: racing 10 real claims against real
        // Postgres, all sharing a concurrency key with MaxConcurrent=3, must never let more than
        // 3 end up Processing - proving the FOR UPDATE count-then-update in
        // AxonPostgresStore.TryClaimJob actually serializes the count-check against real
        // concurrent transactions, not just against sequential/mocked calls.
        var concurrencyKey = Guid.NewGuid().ToString();
        var sut = CreateSut();
        var jobs = Enumerable.Range(0, 10)
            .Select(_ => JobFactory.CreateJob(state: JobState.Enqueued, concurrencyKey: concurrencyKey, maxConcurrent: 3))
            .ToList();
        foreach (var job in jobs)
        {
            await sut.AddJob(job);
        }

        var results = await Task.WhenAll(jobs.Select((job, i) => sut.TryClaimJob(job.JobId, processingDeadline: i)));

        results.Count(r => r).Should().Be(3, "MaxConcurrent=3 must never be exceeded even under real concurrent claims");
    }

    [Fact]
    public async Task GetOrphanedProcessingJobs_ReturnsOnlyPastDeadlineProcessingJobs()
    {
        var sut = CreateSut();
        var expired = JobFactory.CreateJob(state: JobState.Enqueued);
        var notYetDue = JobFactory.CreateJob(state: JobState.Enqueued);
        await sut.AddJob(expired);
        await sut.AddJob(notYetDue);
        await sut.TryClaimJob(expired.JobId, processingDeadline: 100);
        await sut.TryClaimJob(notYetDue.JobId, processingDeadline: 999_999);

        var result = await sut.GetOrphanedProcessingJobs(asOf: 500);

        result.Select(j => j.JobId).Should().Contain(expired.JobId);
        result.Select(j => j.JobId).Should().NotContain(notYetDue.JobId);
    }

    [Fact]
    public async Task GetProcessingJobsForDevice_ReturnsOnlyThatDevicesProcessingJobs()
    {
        var sut = CreateSut();
        var job = JobFactory.CreateJob(deviceName: "device-a", state: JobState.Processing);
        var otherDeviceJob = JobFactory.CreateJob(deviceName: "device-b", state: JobState.Processing);
        await sut.AddJob(job);
        await sut.AddJob(otherDeviceJob);

        var result = await sut.GetProcessingJobsForDevice("device-a");

        result.Select(j => j.JobId).Should().BeEquivalentTo([job.JobId]);
    }

    [Fact]
    public async Task DeleteCompletedJobsOlderThan_DeletesTerminalJobPastCutoffAndItsHistory()
    {
        // Asserts on this test's own job id, not the returned count: the fixture database is
        // shared across every test in this class (via the collection fixture), so a bare count
        // could include other tests' leftover terminal jobs.
        var sut = CreateSut();
        var job = JobFactory.CreateJob(state: JobState.Enqueued);
        await sut.AddJob(job);
        await sut.UpdateState(job.JobId, JobState.Succeeded);

        var cutoff = DateTime.UtcNow.AddSeconds(1).Ticks;
        await sut.DeleteCompletedJobsOlderThan(cutoff);

        (await sut.GetJob(job.JobId)).Should().BeNull();
        (await sut.GetHistory(job.JobId)).Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteCompletedJobsOlderThan_KeepsJobsNewerThanCutoff()
    {
        var sut = CreateSut();
        var job = JobFactory.CreateJob(state: JobState.Enqueued);
        await sut.AddJob(job);
        await sut.UpdateState(job.JobId, JobState.Succeeded);

        await sut.DeleteCompletedJobsOlderThan(cutoff: 0);

        (await sut.GetJob(job.JobId)).Should().NotBeNull();
    }

    [Theory]
    [InlineData(JobState.Enqueued)]
    [InlineData(JobState.Scheduled)]
    [InlineData(JobState.Processing)]
    [InlineData(JobState.AwaitingParent)]
    public async Task DeleteCompletedJobsOlderThan_NeverDeletesNonTerminalJobsAgainstRealPostgres(JobState nonTerminalState)
    {
        var sut = CreateSut();
        var job = JobFactory.CreateJob(state: nonTerminalState);
        await sut.AddJob(job);

        await sut.DeleteCompletedJobsOlderThan(DateTime.UtcNow.AddYears(1).Ticks);

        (await sut.GetJob(job.JobId)).Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteCompletedJobsOlderThan_OnlyDeletesJobsPastCutoff_LeavesOthersIntact()
    {
        // Asserts on specific job ids rather than a total deleted count: this fixture's database
        // is shared across every test in this class (via the collection fixture), and other tests
        // leave their own Succeeded/Failed jobs behind, so a bare count assertion here would be
        // polluted by unrelated tests' data rather than proving this test's own behavior.
        var sut = CreateSut();
        var old = JobFactory.CreateJob(state: JobState.Enqueued);
        await sut.AddJob(old);
        await sut.UpdateState(old.JobId, JobState.Succeeded);

        // A real gap between "old" finishing and the cutoff (rather than a cutoff set once for
        // both jobs) is what makes "old" provably older than the cutoff and "recent" provably
        // newer than it - both jobs otherwise settle within the same test-execution millisecond.
        await Task.Delay(50);
        var cutoff = DateTime.UtcNow.Ticks;
        await Task.Delay(50);

        var recent = JobFactory.CreateJob(state: JobState.Enqueued);
        await sut.AddJob(recent);
        await sut.UpdateState(recent.JobId, JobState.Succeeded);

        await sut.DeleteCompletedJobsOlderThan(cutoff);

        (await sut.GetJob(old.JobId)).Should().BeNull();
        (await sut.GetJob(recent.JobId)).Should().NotBeNull();
    }
}
