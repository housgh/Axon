using System.Diagnostics.Metrics;
using Axon.Core.Enums;
using Axon.Server.DependencyInjection;
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
    private readonly IDeviceConnectionRegistry _deviceRegistry = new InMemoryDeviceConnectionRegistry();
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

    private Task DispatchDueJobsAsync(CancellationToken ct = default) => DispatchDueJobsAsync(_sut, ct);

    private static Task DispatchDueJobsAsync(AxonJobProcessor processor, CancellationToken ct = default)
    {
        // DispatchDueJobsAsync is private; invoked via reflection so the test exercises the
        // actual poll-cycle dispatch logic (claim-then-send ordering, connectivity check) rather
        // than reimplementing it.
        var method = typeof(AxonJobProcessor).GetMethod("DispatchDueJobsAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (Task)method.Invoke(processor, [ct])!;
    }

    [Fact]
    public async Task DispatchDueJobsAsync_DeviceConnected_ClaimsAndDispatches()
    {
        await _jobStore.AddJob(JobFactory.CreateJob("job-1", deviceName: "device-1", state: JobState.Enqueued));
        await _deviceRegistry.Register("device-1", "conn-1");

        await DispatchDueJobsAsync();

        var job = await _jobStore.GetJob("job-1");
        job!.State.Should().Be(JobState.Processing);
        await _clientProxy.Received(1).SendCoreAsync("Invoke", Arg.Is<object?[]>(a => (string)a[0]! == "job-1"), Arg.Any<CancellationToken>());
        await _notifier.Received(1).JobsChanged();
    }

    [Fact]
    public async Task DispatchDueJobsAsync_JobOnUnservedQueue_NeverDispatches()
    {
        // The device is connected (so the existing connectivity check alone wouldn't skip it) -
        // only the queue guard should stop this dispatch.
        var sut = new AxonJobProcessor(
            _hubContext, _jobStore, _jobService, _deviceRegistry, _notifier,
            Substitute.For<ILogger<AxonJobProcessor>>(),
            new AxonServerFeatures { ServedQueues = new HashSet<string> { "default" } });
        await _jobStore.AddJob(JobFactory.CreateJob("job-1", deviceName: "device-1", state: JobState.Enqueued, queueName: "billing"));
        await _deviceRegistry.Register("device-1", "conn-1");

        await DispatchDueJobsAsync(sut);

        var job = await _jobStore.GetJob("job-1");
        job!.State.Should().Be(JobState.Enqueued);
        await _clientProxy.DidNotReceive().SendCoreAsync(Arg.Any<string>(), Arg.Any<object?[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchDueJobsAsync_JobOnServedQueue_DispatchesNormally()
    {
        var sut = new AxonJobProcessor(
            _hubContext, _jobStore, _jobService, _deviceRegistry, _notifier,
            Substitute.For<ILogger<AxonJobProcessor>>(),
            new AxonServerFeatures { ServedQueues = new HashSet<string> { "default", "billing" } });
        await _jobStore.AddJob(JobFactory.CreateJob("job-1", deviceName: "device-1", state: JobState.Enqueued, queueName: "billing"));
        await _deviceRegistry.Register("device-1", "conn-1");

        await DispatchDueJobsAsync(sut);

        var job = await _jobStore.GetJob("job-1");
        job!.State.Should().Be(JobState.Processing);
        await _clientProxy.Received(1).SendCoreAsync("Invoke", Arg.Is<object?[]>(a => (string)a[0]! == "job-1"), Arg.Any<CancellationToken>());
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
        await _deviceRegistry.Register("device-1", "conn-1");

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
        await _deviceRegistry.Register("device-1", "conn-1");
        await _jobStore.TryClaimJob("job-1", processingDeadline: 999);

        await DispatchDueJobsAsync();

        await _clientProxy.DidNotReceive().SendCoreAsync(Arg.Any<string>(), Arg.Any<object?[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchDueJobsAsync_ReportsQueueDepthAsCountOfDueJobs()
    {
        await _jobStore.AddJob(JobFactory.CreateJob("job-1", deviceName: "offline-device", state: JobState.Enqueued));
        await _jobStore.AddJob(JobFactory.CreateJob("job-2", deviceName: "offline-device", state: JobState.Enqueued));

        var values = CollectGaugeValues<int>(AxonInstrumentation.QueueDepth, () => DispatchDueJobsAsync());

        values.Should().Contain(2);
    }

    [Fact]
    public async Task DispatchDueJobsAsync_OnSuccessfulClaim_RecordsDispatchedCounter()
    {
        await _jobStore.AddJob(JobFactory.CreateJob("job-1", deviceName: "device-1", state: JobState.Enqueued));
        await _deviceRegistry.Register("device-1", "conn-1");

        var dispatchedCounts = CollectCounterValues<long>(AxonInstrumentation.JobsDispatched, () => DispatchDueJobsAsync());

        dispatchedCounts.Sum().Should().Be(1);
    }

    [Fact]
    public async Task DispatchDueJobsAsync_OnSuccessfulClaimWithEnqueuedAt_RecordsDispatchLatency()
    {
        var enqueuedAt = DateTime.UtcNow.AddSeconds(-1).Ticks;
        await _jobStore.AddJob(JobFactory.CreateJob("job-1", deviceName: "device-1", state: JobState.Enqueued, enqueuedAt: enqueuedAt));
        await _deviceRegistry.Register("device-1", "conn-1");

        var latencies = CollectHistogramValues<double>(AxonInstrumentation.DispatchLatency, () => DispatchDueJobsAsync());

        latencies.Should().ContainSingle();
        latencies[0].Should().BeGreaterThan(500);
    }

    [Fact]
    public async Task DispatchDueJobsAsync_OnFailedClaim_RecordsClaimFailedCounter()
    {
        // A claim only fails when another poll cycle (or, in production, another Axon.Server
        // instance) claims the job in the window between this cycle's GetJobs snapshot and its
        // TryClaimJob call - a pre-claimed job wouldn't even appear in that snapshot, and
        // InMemoryAxonJobStore's synchronous lock means two real DispatchDueJobsAsync calls never
        // actually interleave in-process (that race is already covered against real concurrency by
        // InMemoryAxonJobStoreTests and the SQL Server integration tests). Use a store double whose
        // TryClaimJob always loses, to isolate just the processor's counter-recording behavior.
        var jobStore = Substitute.For<IAxonJobStore>();
        var job = JobFactory.CreateJob("job-1", deviceName: "device-1", state: JobState.Enqueued);
        jobStore.GetJobs(0, int.MaxValue, Arg.Any<JobState[]>()).Returns([job]);
        jobStore.TryClaimJob(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<string?>()).Returns(false);
        jobStore.GetOrphanedProcessingJobs(Arg.Any<long>()).Returns([]);
        await _deviceRegistry.Register("device-1", "conn-1");
        var sut = new AxonJobProcessor(_hubContext, jobStore, _jobService, _deviceRegistry, _notifier, Substitute.For<ILogger<AxonJobProcessor>>());
        var method = typeof(AxonJobProcessor).GetMethod("DispatchDueJobsAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        var claimFailedCounts = CollectCounterValues<long>(AxonInstrumentation.JobsClaimFailed,
            () => (Task)method.Invoke(sut, [CancellationToken.None])!);

        claimFailedCounts.Sum().Should().Be(1);
    }

    private static List<T> CollectCounterValues<T>(Counter<T> counter, Func<Task> action) where T : struct
    {
        var values = new List<T>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == counter.Meter.Name && instrument.Name == counter.Name)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<T>((_, measurement, _, _) => values.Add(measurement));
        listener.Start();
        action().GetAwaiter().GetResult();
        return values;
    }

    private static List<T> CollectHistogramValues<T>(Histogram<T> histogram, Func<Task> action) where T : struct
    {
        var values = new List<T>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == histogram.Meter.Name && instrument.Name == histogram.Name)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<T>((_, measurement, _, _) => values.Add(measurement));
        listener.Start();
        action().GetAwaiter().GetResult();
        return values;
    }

    private static List<T> CollectGaugeValues<T>(ObservableGauge<T> gauge, Func<Task> action) where T : struct
    {
        var values = new List<T>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == gauge.Meter.Name && instrument.Name == gauge.Name)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<T>((_, measurement, _, _) => values.Add(measurement));
        listener.Start();
        action().GetAwaiter().GetResult();
        listener.RecordObservableInstruments();
        return values;
    }
}
