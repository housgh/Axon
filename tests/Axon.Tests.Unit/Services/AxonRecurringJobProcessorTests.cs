using System.Reflection;
using Axon.Core.Enums;
using Axon.Core.Models;
using Axon.Server.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Axon.Tests.Unit.Services;

public class AxonRecurringJobProcessorTests
{
    private readonly InMemoryAxonRecurringJobStore _recurringJobStore = new();
    private readonly InMemoryAxonJobStore _jobStore = new();
    private readonly IAxonDashboardNotifier _notifier = Substitute.For<IAxonDashboardNotifier>();
    private readonly AxonRecurringJobProcessor _sut;

    public AxonRecurringJobProcessorTests()
    {
        _sut = new AxonRecurringJobProcessor(_recurringJobStore, _jobStore, _notifier, Substitute.For<ILogger<AxonRecurringJobProcessor>>());
    }

    private Task TriggerDueRecurringJobsAsync()
    {
        // TriggerDueRecurringJobsAsync is private; invoked via reflection so the test exercises
        // the actual poll-cycle logic (paused check, due check, next-occurrence computation)
        // rather than reimplementing it, matching the pattern AxonJobProcessorTests already uses.
        var method = typeof(AxonRecurringJobProcessor).GetMethod("TriggerDueRecurringJobsAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Task)method.Invoke(_sut, [])!;
    }

    private static RecurringJob CreateRecurringJob(string id, long nextRunAt, bool isPaused = false) =>
        new(new JobInfo { MethodName = "M", Assembly = "A", DeclaringType = "T", Arguments = [] })
        {
            RecurringJobId = id,
            DeviceName = "device-1",
            CronExpression = "0 * * * *",
            NextRunAt = nextRunAt,
            IsPaused = isPaused
        };

    [Fact]
    public async Task TriggerDueRecurringJobsAsync_DueAndNotPaused_EnqueuesJob()
    {
        await _recurringJobStore.AddOrUpdate(CreateRecurringJob("r1", DateTimeOffset.UtcNow.AddMinutes(-1).UtcTicks));

        await TriggerDueRecurringJobsAsync();

        var jobs = await _jobStore.GetJobs(take: int.MaxValue);
        jobs.Should().ContainSingle(j => j.DeviceName == "device-1" && j.State == JobState.Enqueued);
    }

    [Fact]
    public async Task TriggerDueRecurringJobsAsync_DueButPaused_DoesNotEnqueueJob()
    {
        await _recurringJobStore.AddOrUpdate(CreateRecurringJob("r1", DateTimeOffset.UtcNow.AddMinutes(-1).UtcTicks, isPaused: true));

        await TriggerDueRecurringJobsAsync();

        var jobs = await _jobStore.GetJobs(take: int.MaxValue);
        jobs.Should().BeEmpty();
    }

    [Fact]
    public async Task TriggerDueRecurringJobsAsync_PausedJob_LeavesNextRunAtUntouched()
    {
        var dueAt = DateTimeOffset.UtcNow.AddMinutes(-1).UtcTicks;
        await _recurringJobStore.AddOrUpdate(CreateRecurringJob("r1", dueAt, isPaused: true));

        await TriggerDueRecurringJobsAsync();

        (await _recurringJobStore.GetById("r1"))!.NextRunAt.Should().Be(dueAt);
    }

    [Fact]
    public async Task TriggerDueRecurringJobsAsync_NotYetDue_DoesNotEnqueueJob()
    {
        await _recurringJobStore.AddOrUpdate(CreateRecurringJob("r1", DateTimeOffset.UtcNow.AddHours(1).UtcTicks));

        await TriggerDueRecurringJobsAsync();

        var jobs = await _jobStore.GetJobs(take: int.MaxValue);
        jobs.Should().BeEmpty();
    }

    [Fact]
    public async Task TriggerDueRecurringJobsAsync_Fires_AdvancesNextRunAt()
    {
        var dueAt = DateTimeOffset.UtcNow.AddMinutes(-1).UtcTicks;
        await _recurringJobStore.AddOrUpdate(CreateRecurringJob("r1", dueAt));

        await TriggerDueRecurringJobsAsync();

        (await _recurringJobStore.GetById("r1"))!.NextRunAt.Should().BeGreaterThan(dueAt);
    }
}
