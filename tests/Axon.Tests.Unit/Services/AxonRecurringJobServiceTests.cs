using Axon.Core.Models;
using Axon.Server.Interfaces;
using Axon.Server.Services;
using FluentAssertions;
using NSubstitute;

namespace Axon.Tests.Unit.Services;

public class AxonRecurringJobServiceTests
{
    private readonly IAxonRecurringJobStore _store = Substitute.For<IAxonRecurringJobStore>();
    private readonly IAxonDashboardNotifier _notifier = Substitute.For<IAxonDashboardNotifier>();
    private readonly AxonRecurringJobService _sut;

    public AxonRecurringJobServiceTests()
    {
        _sut = new AxonRecurringJobService(_store, _notifier);
    }

    [Fact]
    public async Task AddOrUpdateAsync_ValidCron_AddsWithComputedNextRun()
    {
        var jobInfo = new JobInfo { MethodName = "M", Assembly = "A", DeclaringType = "T", Arguments = [] };
        var before = DateTimeOffset.UtcNow;

        await _sut.AddOrUpdateAsync("device-1", "recurring-1", jobInfo, "0 * * * *");

        await _store.Received(1).AddOrUpdate(Arg.Is<RecurringJob>(r =>
            r.RecurringJobId == "recurring-1" &&
            r.DeviceName == "device-1" &&
            r.CronExpression == "0 * * * *" &&
            r.NextRunAt > before.UtcTicks));
        await _notifier.Received(1).RecurringJobsChanged();
    }

    [Fact]
    public async Task AddOrUpdateAsync_InvalidCron_ThrowsAndDoesNotPersist()
    {
        var jobInfo = new JobInfo { MethodName = "M", Assembly = "A", DeclaringType = "T", Arguments = [] };

        var act = () => _sut.AddOrUpdateAsync("device-1", "recurring-1", jobInfo, "not a cron expression");

        await act.Should().ThrowAsync<Exception>();
        await _store.DidNotReceive().AddOrUpdate(Arg.Any<RecurringJob>());
        await _notifier.DidNotReceive().RecurringJobsChanged();
    }

    [Fact]
    public async Task RemoveAsync_RemovesAndNotifies()
    {
        await _sut.RemoveAsync("recurring-1");

        await _store.Received(1).Remove("recurring-1");
        await _notifier.Received(1).RecurringJobsChanged();
    }
}
