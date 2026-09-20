// ReSharper disable CheckNamespace

using Axon.Server.Interfaces;
using Axon.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Axon.MySql;

public static class DependencyInjection
{
    /// <summary>
    /// Replaces Axon.Server's default in-memory job store, recurring-job store, server-instance
    /// registry, and device-connection registry with MySQL-backed implementations sharing
    /// <paramref name="connectionString"/>, so multiple <c>Axon.Server</c> instances can safely
    /// share one job queue (atomic claim via <c>TryClaimJob</c> - see
    /// docs/architecture.md#multi-instance-dispatch-safety) and the dashboard's Servers/Clients
    /// tabs show every instance's data rather than only what's local to whichever instance
    /// answers a given request.
    /// <para/>
    /// The target database must already have Axon's schema applied - run
    /// <c>Axon.Store.MySql/Schema.sql</c> against it first. A store method throws
    /// <see cref="Axon.Server.Exceptions.AxonSchemaNotProvisionedException"/> if it hasn't been,
    /// or <see cref="Axon.Server.Exceptions.AxonStoreException"/> for any other MySQL
    /// connectivity failure (both carry the original exception as <c>InnerException</c>).
    /// <para/>
    /// Call this after <c>AddAxonServer()</c>/<c>AddAxonDashboard()</c> - it replaces services
    /// those calls registered, so it must run after them to take effect.
    /// </summary>
    public static void AddAxonMySqlStore(this IServiceCollection services, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A MySQL connection string must be provided.", nameof(connectionString));

        services.Replace(ServiceDescriptor.Singleton<IAxonJobStore>(_ => new AxonMySqlStore(connectionString)));
        services.Replace(ServiceDescriptor.Singleton<IAxonRecurringJobStore>(_ => new AxonMySqlRecurringJobStore(connectionString)));
        services.Replace(ServiceDescriptor.Singleton<IAxonServerInstanceStore>(_ => new AxonMySqlInstanceStore(connectionString)));
        services.Replace(ServiceDescriptor.Singleton<IDeviceConnectionRegistry>(_ => new AxonMySqlDeviceConnectionStore(connectionString)));
    }
}
