// ReSharper disable CheckNamespace

using Axon.Server.Interfaces;
using Axon.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Axon.SQLite;

public static class DependencyInjection
{
    /// <summary>
    /// Replaces Axon.Server's default in-memory job store, recurring-job store, server-instance
    /// registry, and device-connection registry with SQLite-backed implementations sharing
    /// <paramref name="connectionString"/>, so job/recurring-job state survives a restart.
    /// <para/>
    /// Unlike <c>AddAxonSqlServerStore</c>/<c>AddAxonPostgresStore</c>/<c>AddAxonMySqlStore</c>,
    /// this does NOT make dispatch safe across multiple <c>Axon.Server</c> instances - SQLite's
    /// single-writer model means every write transaction against the database file is fully
    /// serialized, so it's suitable for a single-instance deployment or local development, not
    /// the multi-instance fleet scenario the other backends target (see
    /// docs/architecture.md#multi-instance-dispatch-safety).
    /// <para/>
    /// The target database must already have Axon's schema applied - run
    /// <c>Axon.Store.SQLite/Schema.sql</c> against it first. A store method throws
    /// <see cref="Axon.Server.Exceptions.AxonSchemaNotProvisionedException"/> if it hasn't been,
    /// or <see cref="Axon.Server.Exceptions.AxonStoreException"/> for any other SQLite failure
    /// (both carry the original exception as <c>InnerException</c>).
    /// <para/>
    /// Call this after <c>AddAxonServer()</c>/<c>AddAxonDashboard()</c> - it replaces services
    /// those calls registered, so it must run after them to take effect.
    /// </summary>
    public static void AddAxonSQLiteStore(this IServiceCollection services, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A SQLite connection string must be provided.", nameof(connectionString));

        services.Replace(ServiceDescriptor.Singleton<IAxonJobStore>(_ => new AxonSQLiteStore(connectionString)));
        services.Replace(ServiceDescriptor.Singleton<IAxonRecurringJobStore>(_ => new AxonSQLiteRecurringJobStore(connectionString)));
        services.Replace(ServiceDescriptor.Singleton<IAxonServerInstanceStore>(_ => new AxonSQLiteInstanceStore(connectionString)));
        services.Replace(ServiceDescriptor.Singleton<IDeviceConnectionRegistry>(_ => new AxonSQLiteDeviceConnectionStore(connectionString)));
    }
}
