using System.Diagnostics;
using Axon.Server.DependencyInjection;
using Axon.Server.OpenTelemetry;
using Axon.Server.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Trace;

namespace Axon.Tests.Unit;

public class AxonServerOpenTelemetryDependencyInjectionTests
{
    [Fact]
    public void AddOpenTelemetryObservability_RecordsSpans_EvenUnderAnUnsampledAmbientParent()
    {
        // Regression test for a real bug found while verifying this package: OTel's default
        // ParentBasedSampler inherits "don't record" from whatever ambient Activity.Current
        // happens to be set when StartActivity is called - which silently drops every Axon span
        // with no error whenever the caller (e.g. a request pipeline, or another library's own
        // unsampled Activity) has an unsampled Activity already current. AddOpenTelemetryObservability
        // must default to AlwaysOnSampler so Axon's own spans are never dropped this way.
        var exportedActivities = new List<Activity>();
        var services = new ServiceCollection();
        services.AddAxonServer().AddOpenTelemetryObservability(
            configureTracing: t => t.AddInMemoryExporter(exportedActivities));
        var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<TracerProvider>();

        // Simulate an unsampled ambient parent - e.g. an ASP.NET Core request Activity that no
        // configured instrumentation is recording.
        using var unsampledParent = new Activity("unsampled-parent");
        unsampledParent.SetIdFormat(ActivityIdFormat.W3C);
        unsampledParent.Start();
        unsampledParent.ActivityTraceFlags = ActivityTraceFlags.None; // not sampled

        using (var span = AxonInstrumentation.ActivitySource.StartActivity("axon.job.enqueue"))
        {
            span.Should().NotBeNull("AddOpenTelemetryObservability should force AlwaysOnSampler regardless of ambient parent sampling state");
        }

        exportedActivities.Should().Contain(a => a.DisplayName == "axon.job.enqueue");
    }

    [Fact]
    public void AddOpenTelemetryObservability_ReturnsSameBuilderForChaining()
    {
        var services = new ServiceCollection();
        var original = services.AddAxonServer();

        var result = original.AddOpenTelemetryObservability();

        result.Should().BeSameAs(original);
    }
}
