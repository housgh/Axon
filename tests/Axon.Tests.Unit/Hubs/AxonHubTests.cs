using Axon.Core.Enums;
using Axon.Server.Hubs;
using Axon.Server.Interfaces;
using Axon.Server.Services;
using Axon.Tests.Unit.TestHelpers;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Axon.Tests.Unit.Hubs;

public class AxonHubTests
{
    private readonly IAxonJobService _jobService = Substitute.For<IAxonJobService>();
    private readonly IAxonRecurringJobService _recurringJobService = Substitute.For<IAxonRecurringJobService>();
    private readonly IDeviceConnectionRegistry _deviceRegistry = Substitute.For<IDeviceConnectionRegistry>();
    private readonly IAxonJobStore _jobStore = Substitute.For<IAxonJobStore>();
    private readonly IAxonDashboardNotifier _notifier = Substitute.For<IAxonDashboardNotifier>();
    private readonly AxonHub _sut;

    public AxonHubTests()
    {
        _sut = new AxonHub(_jobService, _recurringJobService, _deviceRegistry, _jobStore, _notifier, Substitute.For<ILogger<AxonHub>>())
        {
            Context = Substitute.For<HubCallerContext>()
        };
        _sut.Context.ConnectionId.Returns("conn-1");
    }

    [Fact]
    public async Task Register_RegistersDeviceAndNotifies()
    {
        await _sut.Register("device-1");

        await _deviceRegistry.Received(1).Register("device-1", "conn-1");
        await _notifier.Received(1).ClientsChanged();
    }

    [Fact]
    public async Task OnSuccess_DelegatesToJobService()
    {
        await _sut.OnSuccess("job-1");

        await _jobService.Received(1).MarkSucceededAsync("job-1");
    }

    [Fact]
    public async Task OnFail_DelegatesToJobServiceWithError()
    {
        await _sut.OnFail("job-1", "boom");

        await _jobService.Received(1).MarkFailedAsync("job-1", "boom");
    }

    [Fact]
    public async Task OnDisconnectedAsync_UnregistersAndReclaimsStrandedJobs()
    {
        _deviceRegistry.GetDeviceName("conn-1").Returns("device-1");
        var stranded = JobFactory.CreateJob("job-1", deviceName: "device-1", state: JobState.Processing);
        _jobStore.GetProcessingJobsForDevice("device-1").Returns([stranded]);

        await _sut.OnDisconnectedAsync(null);

        await _deviceRegistry.Received(1).Unregister("conn-1");
        await _notifier.Received(1).ClientsChanged();
        await _jobService.Received(1).ReclaimOrphanedAsync(stranded);
    }

    [Fact]
    public async Task OnDisconnectedAsync_UnknownConnection_DoesNotAttemptReclaim()
    {
        _deviceRegistry.GetDeviceName("conn-1").Returns((string?)null);

        await _sut.OnDisconnectedAsync(null);

        await _jobStore.DidNotReceive().GetProcessingJobsForDevice(Arg.Any<string>());
        await _jobService.DidNotReceive().ReclaimOrphanedAsync(Arg.Any<Job>());
    }
}
