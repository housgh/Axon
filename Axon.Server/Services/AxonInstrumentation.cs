using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Axon.Server.Services;

/// <summary>
/// Central <see cref="Meter"/>/<see cref="ActivitySource"/> for Axon.Server, used from
/// AxonJobService/AxonJobProcessor/AxonHub/AxonRecurringJobProcessor. These are plain
/// System.Diagnostics primitives with no OpenTelemetry dependency, so they're always emitted
/// regardless of whether anything is listening - <c>Axon.Server.OpenTelemetry</c> is what wires an
/// OTel SDK/exporter to actually collect them, but any listener (including a host app's own OTel
/// setup) can attach to <see cref="MeterName"/>/<see cref="ActivitySourceName"/> directly.
/// </summary>
public static class AxonInstrumentation
{
    public const string MeterName = "Axon.Server";
    public const string ActivitySourceName = "Axon.Server";

    private static readonly Meter Meter = new(MeterName);
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    public static readonly Counter<long> JobsEnqueued =
        Meter.CreateCounter<long>("axon.jobs.enqueued", unit: "{job}", description: "Jobs added to the queue (enqueued or scheduled).");

    public static readonly Counter<long> JobsDispatched =
        Meter.CreateCounter<long>("axon.jobs.dispatched", unit: "{job}", description: "Jobs successfully claimed and sent to a client.");

    public static readonly Counter<long> JobsClaimFailed =
        Meter.CreateCounter<long>("axon.jobs.claim_failed", unit: "{job}", description: "TryClaimJob calls that lost the race (another instance/poll cycle already claimed the job).");

    public static readonly Counter<long> JobsSucceeded =
        Meter.CreateCounter<long>("axon.jobs.succeeded", unit: "{job}", description: "Jobs acknowledged as succeeded by a client.");

    public static readonly Counter<long> JobsFailed =
        Meter.CreateCounter<long>("axon.jobs.failed", unit: "{job}", description: "Jobs that reached the terminal Failed state (retries exhausted).");

    public static readonly Counter<long> JobsRetried =
        Meter.CreateCounter<long>("axon.jobs.retried", unit: "{job}", description: "Jobs requeued for another attempt after a failure or reclaim.");

    public static readonly Counter<long> JobsOrphanedReclaimed =
        Meter.CreateCounter<long>("axon.jobs.orphaned_reclaimed", unit: "{job}", description: "Jobs reclaimed after being dispatched but never acknowledged (disconnect or deadline sweep).");

    public static readonly Histogram<double> DispatchLatency =
        Meter.CreateHistogram<double>("axon.jobs.dispatch_latency", unit: "ms", description: "Time from a job becoming due (or being enqueued for immediate run) to being claimed for dispatch.");

    public static readonly Histogram<double> ExecutionDuration =
        Meter.CreateHistogram<double>("axon.jobs.execution_duration", unit: "ms", description: "Time from dispatch (claim) to the client acknowledging success or failure.");

    public static readonly Counter<long> RecurringJobsTriggered =
        Meter.CreateCounter<long>("axon.recurring_jobs.triggered", unit: "{job}", description: "Recurring job occurrences enqueued as a new job instance.");

    /// <summary>
    /// Queue depth as an observable gauge would require a live store reference at meter-creation
    /// time, which AxonInstrumentation (a static class with no DI) doesn't have. Queue depth is
    /// instead reported by AxonJobProcessor at the end of each poll cycle via this counter-like
    /// UpDownCounter, which callers Add() the current count to on each report (see
    /// AxonJobProcessor.ReportQueueDepth) rather than incrementing/decrementing per-event.
    /// </summary>
    public static readonly ObservableGauge<int> QueueDepth = Meter.CreateObservableGauge(
        "axon.jobs.queue_depth",
        observeValue: () => _queueDepth,
        unit: "{job}",
        description: "Number of jobs currently Enqueued or Scheduled, as of the most recent poll cycle.");

    private static volatile int _queueDepth;
    public static void SetQueueDepth(int depth) => _queueDepth = depth;
}
