using Axon.Core.Models;
using Axon.Postgres;
using Axon.Server.Services;
using FluentAssertions;

namespace Axon.Tests.Integration;

[Collection(PostgresCollection.Name)]
public class AxonPostgresRecurringJobStoreTests(PostgresFixture fixture)
{
    private AxonPostgresRecurringJobStore CreateSut() => new(fixture.ConnectionString);

    private static RecurringJob CreateRecurringJob(string? id = null) =>
        new(new JobInfo { MethodName = "M", Assembly = "A", DeclaringType = "T", Arguments = [] })
        {
            RecurringJobId = id ?? Guid.NewGuid().ToString(),
            DeviceName = "device-1",
            CronExpression = "0 * * * *",
            NextRunAt = 1000
        };

    [Fact]
    public async Task AddOrUpdate_NewRecurringJob_Inserts()
    {
        var sut = CreateSut();
        var job = CreateRecurringJob();

        await sut.AddOrUpdate(job);
        var all = await sut.GetAll();

        all.Should().Contain(j => j.RecurringJobId == job.RecurringJobId && j.CronExpression == "0 * * * *");
    }

    [Fact]
    public async Task AddOrUpdate_ExistingId_UpdatesInPlaceRatherThanDuplicating()
    {
        var sut = CreateSut();
        var job = CreateRecurringJob();
        await sut.AddOrUpdate(job);

        job.CronExpression = "*/5 * * * *";
        job.NextRunAt = 2000;
        await sut.AddOrUpdate(job);

        var all = await sut.GetAll();
        all.Count(j => j.RecurringJobId == job.RecurringJobId).Should().Be(1);
        all.Single(j => j.RecurringJobId == job.RecurringJobId).CronExpression.Should().Be("*/5 * * * *");
    }

    [Fact]
    public async Task UpdateNextRun_UpdatesNextAndLastRun()
    {
        var sut = CreateSut();
        var job = CreateRecurringJob();
        await sut.AddOrUpdate(job);

        await sut.UpdateNextRun(job.RecurringJobId, nextRunAt: 3000, lastRunAt: 1500);

        var all = await sut.GetAll();
        var result = all.Single(j => j.RecurringJobId == job.RecurringJobId);
        result.NextRunAt.Should().Be(3000);
        result.LastRunAt.Should().Be(1500);
    }

    [Fact]
    public async Task Remove_DeletesRecurringJob()
    {
        var sut = CreateSut();
        var job = CreateRecurringJob();
        await sut.AddOrUpdate(job);

        await sut.Remove(job.RecurringJobId);

        var all = await sut.GetAll();
        all.Should().NotContain(j => j.RecurringJobId == job.RecurringJobId);
    }

    [Fact]
    public async Task GetById_ExistingId_ReturnsIt()
    {
        var sut = CreateSut();
        var job = CreateRecurringJob();
        await sut.AddOrUpdate(job);

        var result = await sut.GetById(job.RecurringJobId);

        result.Should().NotBeNull();
        result!.RecurringJobId.Should().Be(job.RecurringJobId);
        result.CronExpression.Should().Be(job.CronExpression);
    }

    [Fact]
    public async Task GetById_UnknownId_ReturnsNull()
    {
        var sut = CreateSut();

        var result = await sut.GetById(Guid.NewGuid().ToString());

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAll_Take_LimitsResultCount()
    {
        // Distinguishable NextRunAt values placed far apart (and randomized within a wide range)
        // so this test's own 3 rows sort predictably relative to each other even though the table
        // is shared with concurrently-running tests in this collection that may add other rows.
        var sut = CreateSut();
        var baseTicks = Random.Shared.NextInt64(10_000_000, 20_000_000);
        var jobs = new[]
        {
            CreateRecurringJob(),
            CreateRecurringJob(),
            CreateRecurringJob()
        };
        for (var i = 0; i < jobs.Length; i++) jobs[i].NextRunAt = baseTicks + i;
        foreach (var job in jobs) await sut.AddOrUpdate(job);

        var page = await sut.GetAll(skip: 0, take: 2);
        var ourIdsInPage = page.Select(j => j.RecurringJobId)
            .Intersect(jobs.Select(j => j.RecurringJobId))
            .ToList();

        // At most 2 of our 3 rows can appear in a take:2 page - proves take is actually enforced
        // rather than GetAll silently returning everything.
        ourIdsInPage.Count.Should().BeLessThanOrEqualTo(2);
    }

    [Fact]
    public async Task AddOrUpdate_NewRecurringJob_DefaultsToNotPaused()
    {
        var sut = CreateSut();
        var job = CreateRecurringJob();

        await sut.AddOrUpdate(job);

        (await sut.GetById(job.RecurringJobId))!.IsPaused.Should().BeFalse();
    }

    [Fact]
    public async Task SetPaused_True_PausesTheJob()
    {
        var sut = CreateSut();
        var job = CreateRecurringJob();
        await sut.AddOrUpdate(job);

        await sut.SetPaused(job.RecurringJobId, isPaused: true);

        (await sut.GetById(job.RecurringJobId))!.IsPaused.Should().BeTrue();
    }

    [Fact]
    public async Task SetPaused_False_ResumesTheJob()
    {
        var sut = CreateSut();
        var job = CreateRecurringJob();
        await sut.AddOrUpdate(job);
        await sut.SetPaused(job.RecurringJobId, isPaused: true);

        await sut.SetPaused(job.RecurringJobId, isPaused: false);

        (await sut.GetById(job.RecurringJobId))!.IsPaused.Should().BeFalse();
    }

    [Fact]
    public async Task AddOrUpdate_ExistingPausedJob_PreservesPausedState()
    {
        // A client re-registering an existing recurring job (AddOrUpdateRecurringAsync is
        // idempotent by id, and runs on every client startup) has no isPaused concept of its own
        // to declare - an operator's pause (set via SetPaused) must survive that re-registration.
        var sut = CreateSut();
        var job = CreateRecurringJob();
        await sut.AddOrUpdate(job);
        await sut.SetPaused(job.RecurringJobId, isPaused: true);

        job.CronExpression = "*/5 * * * *";
        await sut.AddOrUpdate(job);

        (await sut.GetById(job.RecurringJobId))!.IsPaused.Should().BeTrue();
    }

    [Fact]
    public async Task SkipNext_AdvancesNextRunAt()
    {
        var sut = CreateSut();
        var job = CreateRecurringJob();
        await sut.AddOrUpdate(job);

        await sut.SkipNext(job.RecurringJobId, newNextRunAt: 9999);

        (await sut.GetById(job.RecurringJobId))!.NextRunAt.Should().Be(9999);
    }

    [Fact]
    public async Task SkipNext_DoesNotChangePausedState()
    {
        var sut = CreateSut();
        var job = CreateRecurringJob();
        await sut.AddOrUpdate(job);
        await sut.SetPaused(job.RecurringJobId, isPaused: true);

        await sut.SkipNext(job.RecurringJobId, newNextRunAt: 9999);

        (await sut.GetById(job.RecurringJobId))!.IsPaused.Should().BeTrue();
    }
}
