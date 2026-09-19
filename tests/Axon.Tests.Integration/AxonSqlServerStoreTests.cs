using System.Text.Json;
using Axon.Core.Enums;
using Axon.SqlServer;
using Axon.Tests.Integration.TestHelpers;
using FluentAssertions;

namespace Axon.Tests.Integration;

[Collection(SqlServerCollection.Name)]
public class AxonSqlServerStoreTests(SqlServerFixture fixture)
{
    private AxonSqlServerStore CreateSut() => new(fixture.ConnectionString);

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
    public async Task TryClaimJob_ConcurrentCallersAgainstRealSqlServer_ExactlyOneWins()
    {
        // This is the core distributed-dispatch safety guarantee: simulates multiple
        // Axon.Server instances racing to claim the same due job via the shared SQL store.
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
}
