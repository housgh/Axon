using Axon.Core.Models;
using Axon.Server.Services;
using FluentAssertions;

namespace Axon.Tests.Unit.Services;

public class InMemoryAxonRecurringJobStoreTests
{
    private readonly InMemoryAxonRecurringJobStore _sut = new();

    private static RecurringJob CreateRecurringJob(string id = "r1") =>
        new(new JobInfo { MethodName = "M", Assembly = "A", DeclaringType = "T", Arguments = [] })
        {
            RecurringJobId = id,
            DeviceName = "device-1",
            CronExpression = "0 * * * *",
            NextRunAt = 1000
        };

    [Fact]
    public async Task AddOrUpdate_NewRecurringJob_DefaultsToNotPaused()
    {
        await _sut.AddOrUpdate(CreateRecurringJob());

        (await _sut.GetById("r1"))!.IsPaused.Should().BeFalse();
    }

    [Fact]
    public async Task SetPaused_True_PausesTheJob()
    {
        await _sut.AddOrUpdate(CreateRecurringJob());

        await _sut.SetPaused("r1", isPaused: true);

        (await _sut.GetById("r1"))!.IsPaused.Should().BeTrue();
    }

    [Fact]
    public async Task AddOrUpdate_ExistingPausedJob_PreservesPausedState()
    {
        // A client re-registering an existing recurring job (AddOrUpdateRecurringAsync is
        // idempotent by id, and runs on every client startup) has no isPaused concept of its own
        // to declare - an operator's pause (set via SetPaused) must survive that re-registration
        // rather than being silently undone by the incoming RecurringJob's default IsPaused = false.
        await _sut.AddOrUpdate(CreateRecurringJob());
        await _sut.SetPaused("r1", isPaused: true);

        var updated = CreateRecurringJob();
        updated.CronExpression = "*/5 * * * *";
        await _sut.AddOrUpdate(updated);

        (await _sut.GetById("r1"))!.IsPaused.Should().BeTrue();
        (await _sut.GetById("r1"))!.CronExpression.Should().Be("*/5 * * * *");
    }

    [Fact]
    public async Task SkipNext_AdvancesNextRunAt_WithoutChangingPausedState()
    {
        await _sut.AddOrUpdate(CreateRecurringJob());
        await _sut.SetPaused("r1", isPaused: true);

        await _sut.SkipNext("r1", newNextRunAt: 9999);

        var result = await _sut.GetById("r1");
        result!.NextRunAt.Should().Be(9999);
        result.IsPaused.Should().BeTrue();
    }
}
