// ReSharper disable CheckNamespace

using Axon.Server.DependencyInjection;
using Axon.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Axon.Server.OpenTelemetry;

public static class AxonServerOpenTelemetryDependencyInjection
{
    /// <summary>
    /// Wires an OpenTelemetry SDK to Axon.Server's metrics (<see cref="AxonInstrumentation.MeterName"/>)
    /// and traces (<see cref="AxonInstrumentation.ActivitySourceName"/>): job counts by outcome
    /// (enqueued/dispatched/succeeded/failed/retried/orphaned-reclaimed), queue depth, dispatch
    /// latency, and execution duration as metrics; spans around enqueue, claim/dispatch, ack, and
    /// orphan reclaim as traces.
    /// <para/>
    /// This only registers the Axon meter/activity source with the SDK - it does not configure an
    /// exporter. Chain <c>.WithMetrics(...)</c>/<c>.WithTracing(...)</c> calls of your own (e.g.
    /// <c>AddOtlpExporter()</c>, <c>AddConsoleExporter()</c>) via <paramref name="configureMetrics"/>
    /// and <paramref name="configureTracing"/>, or configure the SDK separately as you normally
    /// would - Axon.Server's meter/activity source will already be included in what it collects.
    /// <para/>
    /// Defaults the sampler to <see cref="AlwaysOnSampler"/>: Axon's dispatch/enqueue/ack spans are
    /// created from a BackgroundService or a SignalR hub method, not an HTTP request, so the SDK's
    /// default <c>ParentBasedSampler</c> would otherwise inherit "don't record" from whatever
    /// ambient (and possibly unsampled) parent Activity happens to be current - silently dropping
    /// every Axon span with no error. Override via <paramref name="configureTracing"/> if you want
    /// different sampling behavior (e.g. to match a host app's own trace sampling).
    /// </summary>
    public static AxonServerBuilder AddOpenTelemetryObservability(
        this AxonServerBuilder builder,
        Action<MeterProviderBuilder>? configureMetrics = null,
        Action<TracerProviderBuilder>? configureTracing = null)
    {
        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(AxonInstrumentation.MeterName);
                configureMetrics?.Invoke(metrics);
            })
            .WithTracing(tracing =>
            {
                tracing.SetSampler(new AlwaysOnSampler());
                tracing.AddSource(AxonInstrumentation.ActivitySourceName);
                configureTracing?.Invoke(tracing);
            });

        return builder;
    }
}
