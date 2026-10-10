using System.Text.Json;
using Axon.Core.Models;
using Axon.Server.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Axon.Tests.Unit.InProcess;

public class InProcessAxonClientTests
{
    public class TestJobs
    {
        public void Run(string message, int count) { }
    }

    private readonly IAxonJobService _jobService = Substitute.For<IAxonJobService>();
    private readonly IAxonRecurringJobService _recurringJobService = Substitute.For<IAxonRecurringJobService>();
    private readonly InProcessAxonClient _sut;

    public InProcessAxonClientTests()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _recurringJobService);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        _sut = new InProcessAxonClient(_jobService, scopeFactory, new AxonInProcessClientOptions { DeviceName = "monolith-1" });
    }

    [Fact]
    public async Task EnqueueAsync_WritesJobDirectlyWithJsonElementArguments()
    {
        JobInfo? captured = null;
        await _jobService.EnqueueAsync("monolith-1", Arg.Any<string>(), Arg.Do<JobInfo>(j => captured = j), null);

        var jobId = await _sut.EnqueueAsync<TestJobs>(x => x.Run("hello", 3));

        await _jobService.Received(1).EnqueueAsync("monolith-1", jobId, Arg.Any<JobInfo>(), null);
        captured!.MethodName.Should().Be(nameof(TestJobs.Run));
        captured.DeclaringType.Should().Be(typeof(TestJobs).FullName);
        captured.Arguments.Should().AllBeOfType<JsonElement>();
        ((JsonElement)captured.Arguments[0]!).GetString().Should().Be("hello");
        ((JsonElement)captured.Arguments[1]!).GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task ScheduleAsync_PassesScheduledForTicks()
    {
        var scheduledFor = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var jobId = await _sut.ScheduleAsync<TestJobs>(scheduledFor, x => x.Run("x", 1));

        await _jobService.Received(1).EnqueueAsync("monolith-1", jobId, Arg.Any<JobInfo>(), scheduledFor.UtcTicks);
    }

    [Fact]
    public async Task ContinueWithAsync_EnqueuesContinuation()
    {
        var jobId = await _sut.ContinueWithAsync<TestJobs>("parent-1", x => x.Run("x", 1), continueOnParentFailure: true);

        await _jobService.Received(1).EnqueueContinuationAsync("monolith-1", jobId, Arg.Any<JobInfo>(), "parent-1", true);
    }

    [Fact]
    public async Task AddOrUpdateRecurringAsync_And_RemoveRecurringAsync_GoThroughRecurringJobService()
    {
        await _sut.AddOrUpdateRecurringAsync<TestJobs>("nightly", "0 0 * * *", x => x.Run("x", 1));
        await _sut.RemoveRecurringAsync("nightly");

        await _recurringJobService.Received(1).AddOrUpdateAsync("monolith-1", "nightly", Arg.Any<JobInfo>(), "0 0 * * *");
        await _recurringJobService.Received(1).RemoveAsync("nightly");
    }

    [Fact]
    public async Task EnqueueAsync_NotAMethodCall_Throws()
    {
        var act = () => _sut.EnqueueAsync<TestJobs>(x => new object());

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
