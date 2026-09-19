using Axon.Server.Interfaces;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Axon.Server.Services;

/// <summary>
/// Readiness check for the configured <see cref="IAxonJobStore"/>: runs a minimal read (take: 1,
/// no filter) against it, so a broken SQL Server connection (or any other store backend failure)
/// is reported unhealthy rather than only surfacing on the next dispatch poll or dashboard
/// request. There's no dedicated "ping" on IAxonJobStore, so a cheap real read stands in for one
/// rather than adding interface surface used only by this check.
/// </summary>
public class AxonJobStoreHealthCheck(IAxonJobStore jobStore) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await jobStore.GetJobs(take: 1);
            return HealthCheckResult.Healthy();
        }
        catch (Exception e)
        {
            return HealthCheckResult.Unhealthy("Job store is unreachable.", e);
        }
    }
}
