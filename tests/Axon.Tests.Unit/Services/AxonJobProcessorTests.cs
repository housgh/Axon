using Axon.Core.Enums;
using Axon.Server.Hubs;
using Axon.Server.Interfaces;
using Axon.Server.Services;
using Axon.Tests.Unit.TestHelpers;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Axon.Tests.Unit.Services;

public class AxonJobProcessorTests
{
    private readonly InMemoryAxonJobStore _jobStore = new();
    private readonly IAxonJobService _jobService = Substitute.For<IAxonJobService>();
    private readonly IDeviceConnectionRegistry _deviceRegistry = new DeviceConnectionRegistry();
    private readonly IAxonDashboardNotifier _notifier = Substitute.For<IAxonDashboardNotifier>();
    private readonly IHubContext<AxonHub> _hubContext = Substitute.For<IHubContext<AxonHub>>();
    private readonly IHubClients _hubClients = Substitute.For<IHubClients>();
    private readonly ISingleClientProxy _clientProxy = Substitute.For<ISingleClientProxy>();
    private readonly AxonJobProcessor _sut;

    public AxonJobProcessorTests()
    {
        _hubContext.Clients.Returns(_hubClients);
        _hubClients.Client(Arg.Any<string>()).Returns(_clientProxy);

        _sut = new AxonJobProcessor(
            _hubContext,
            _jobStore,
            _jobService,
            _deviceRegistry,
            _notifier,
            Substitute.For<ILogger<AxonJobProcessor>>());
    }

    private Task DispatchDueJobsAsync(CancellationToken ct = default)
    {
        // DispatchDueJobsAsync is private; invoked via reflection so the test exercises the
        // actual poll-cycle dispatch logic (claim-then-send ordering, connectivity check) rather
        // than reimplementing it.
        var method = typeof(AxonJobProcessor).GetMethod("DispatchDueJobsAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (Task)method.Invoke(_sut, [ct])!;
    }

    [Fact]
    public async Task DispatchDueJobsAsync_DeviceConnected_ClaimsAndDispatches()
    {
        await _jobStore.AddJob(JobFactory.CreateJob("job-1", deviceName: "device-1", state: JobState.Enqueued));
        _deviceRegistry.Register("device-1", "conn-1");

        await DispatchDueJobsAsync();

        var job = await _jobStore.GetJob("job-1");
        job!.State.Should().Be(JobState.Processing);
        await _clientProxy.Received(1).SendCoreAsync("Invoke", Arg.Is<object?[]>(a => (string)a[0]! == "job-1"), Arg.Any<CancellationToken>());
        await _notifier.Received(1).JobsChanged();
    }

    [Fact]
    public async Task DispatchDueJobsAsync_DeviceNotConnected_SkipsAndLeavesJobEnqueued()
    {
        await _jobStore.AddJob(JobFactory.CreateJob("job-1", deviceName: "offline-device", state: JobState.Enqueued));

        await DispatchDueJobsAsync();

        var job = await _jobStore.GetJob("job-1");
        job!.State.Should().Be(JobState.Enqueued);
        await _clientProxy.DidNotReceive().SendCoreAsync(Arg.Any<string>(), Arg.Any<object?[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchDueJobsAsync_ScheduledJobNotYetDue_IsSkipped()
    {
        var future = DateTime.UtcNow.AddHours(1).Ticks;
        await _jobStore.AddJob(JobFactory.CreateJob("job-1", deviceName: "device-1", state: JobState.Scheduled, scheduledFor: future));
        _deviceRegistry.Register("device-1", "conn-1");

        await DispatchDueJobsAsync();

        var job = await _jobStore.GetJob("job-1");
        job!.State.Should().Be(JobState.Scheduled);
        await _clientProxy.DidNotReceive().SendCoreAsync(Arg.Any<string>(), Arg.Any<object?[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchDueJobsAsync_JobAlreadyClaimedByAnotherInstance_DoesNotDispatch()
    {
        // Simulates another Axon.Server instance winning the race: the job is Processing by the
        // time this instance's poll cycle looks at it (GetJobs still returned it as due a moment
        // earlier), so TryClaimJob must fail and dispatch must not happen.
        await _jobStore.AddJob(JobFactory.CreateJob("job-1", deviceName: "device-1", state: JobState.Enqueued));
        _deviceRegistry.Register("device-1", "conn-1");
        await _jobStore.TryClaimJob("job-1", processingDeadline: 999);

        await DispatchDueJobsAsync();

        await _clientProxy.DidNotReceive().SendCoreAsync(Arg.Any<string>(), Arg.Any<object?[]>(), Arg.Any<CancellationToken>());
    }
}
