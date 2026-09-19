// ReSharper disable CheckNamespace

using Axon.Server.DependencyInjection;
using Microsoft.AspNetCore.SignalR.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection;

namespace Axon.Server.Redis;

public static class AxonServerRedisDependencyInjection
{
    /// <summary>
    /// Wires a Redis backplane into both Axon hubs (the job-dispatch hub and the dashboard-push
    /// hub), so job dispatch and dashboard updates reach clients no matter which Axon.Server
    /// instance behind a load balancer they're connected to.
    /// <para/>
    /// This does NOT change dispatch correctness across instances - that's already guaranteed by
    /// <c>IAxonJobStore.TryClaimJob</c> when using <c>Axon.Store.SqlServer</c> (see
    /// docs/architecture.md#multi-instance-dispatch-safety). The backplane instead solves
    /// *routing*: without it, a SignalR connection is pinned to whichever instance accepted it, so
    /// a <c>Clients.Client(...)</c>/<c>Clients.All</c> call only ever reaches clients on the same
    /// process. With it, those calls are fanned out to every instance via Redis pub/sub, so a
    /// client connected to instance B still receives a job dispatched by instance A.
    /// </summary>
    public static AxonServerBuilder AddRedisBackplane(this AxonServerBuilder builder, string connectionString, Action<RedisOptions>? configure = null)
    {
        builder.Services.AddSignalR().AddStackExchangeRedis(connectionString, options => configure?.Invoke(options));
        return builder;
    }
}
