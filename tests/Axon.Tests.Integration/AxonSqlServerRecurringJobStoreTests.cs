using Axon.Core.Models;
using Axon.SqlServer;
using Axon.Server.Services;
using FluentAssertions;

namespace Axon.Tests.Integration;

[Collection(SqlServerCollection.Name)]
public class AxonSqlServerRecurringJobStoreTests(SqlServerFixture fixture)
{
    private AxonSqlServerRecurringJobStore CreateSut() => new(fixture.ConnectionString);

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
}
