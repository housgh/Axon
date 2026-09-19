using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace Axon.Server.Redis;

/// <summary>
/// Readiness check for the Redis backplane: pings the configured Redis instance directly, so a
/// down/unreachable Redis is reported unhealthy rather than only surfacing the first time a
/// dashboard push or cross-instance dispatch relay silently fails to fan out. Mirrors
/// Axon.Server's AxonJobStoreHealthCheck for the job store.
/// <para/>
/// AddStackExchangeRedis manages its own internal connection privately (not exposed via DI for
/// reuse), so this opens its own lightweight ConnectionMultiplexer against the same connection
/// string rather than trying to share SignalR's.
/// </summary>
public class RedisBackplaneHealthCheck(string connectionString) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var options = ConfigurationOptions.Parse(connectionString);
            options.ConnectTimeout = 3000;
            options.AbortOnConnectFail = true;

            await using var connection = await ConnectionMultiplexer.ConnectAsync(options);
            await connection.GetDatabase().PingAsync();
            return HealthCheckResult.Healthy();
        }
        catch (Exception e)
        {
            return HealthCheckResult.Unhealthy($"Could not reach the Redis backplane. ({e.Message.Trim()})", e);
        }
    }
}
