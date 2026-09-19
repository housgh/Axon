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
    public async Task DeleteJob_RemovesJobAndHistory()
    {
        await _sut.AddJob(JobFactory.CreateJob("job-1"));

        await _sut.DeleteJob("job-1");

        (await _sut.GetJob("job-1")).Should().BeNull();
        (await _sut.GetHistory("job-1")).Should().BeEmpty();
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
    public async Task DeleteCompletedJobsOlderThan_DeletesTerminalJobsPastCutoff()
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
    public async Task DeleteCompletedJobsOlderThan_DeletesSkippedJobs()
    {
        await _sut.AddJob(JobFactory.CreateJob("job-1", state: JobState.AwaitingParent));
        await _sut.UpdateState("job-1", JobState.Skipped);

        var deleted = await _sut.DeleteCompletedJobsOlderThan(DateTime.UtcNow.AddSeconds(1).Ticks);

        deleted.Should().Be(1);
    }
}
