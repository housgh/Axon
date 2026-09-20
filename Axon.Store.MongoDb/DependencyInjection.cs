// ReSharper disable CheckNamespace

using Axon.Server.Interfaces;
using Axon.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Axon.MongoDb;

public static class DependencyInjection
{
    /// <summary>
    /// Replaces Axon.Server's default in-memory job store, recurring-job store, server-instance
    /// registry, and device-connection registry with MongoDB-backed implementations sharing
    /// <paramref name="connectionString"/>/<paramref name="databaseName"/>, so multiple
    /// <c>Axon.Server</c> instances can safely share one job queue (atomic claim via
    /// <c>TryClaimJob</c> - see docs/architecture.md#multi-instance-dispatch-safety) and the
    /// dashboard's Servers/Clients tabs show every instance's data rather than only what's local
    /// to whichever instance answers a given request.
    /// <para/>
    /// <paramref name="connectionString"/> must point at a replica set (even a single-node one -
    /// see the README) - <c>TryClaimJob</c>'s <c>ConcurrencyKey</c>/<c>MaxConcurrent</c> check
    /// runs inside a multi-document transaction, which a standalone MongoDB server cannot open.
    /// <para/>
    /// Unlike the SQL backends, there is no schema to provision first - collections are created
    /// implicitly on first write. Call <see cref="Indexes.EnsureIndexesAsync"/> once against the
    /// target database if you want the indexes AxonMongo*Store's queries rely on for performance
    /// at scale (optional - queries still work without them).
    /// <para/>
    /// A store method throws <see cref="Axon.Server.Exceptions.AxonStoreException"/> for a
    /// MongoDB connectivity failure (carrying the original exception as <c>InnerException</c>).
    /// <para/>
    /// Call this after <c>AddAxonServer()</c>/<c>AddAxonDashboard()</c> - it replaces services
    /// those calls registered, so it must run after them to take effect.
    /// </summary>
    public static void AddAxonMongoDbStore(this IServiceCollection services, string connectionString, string databaseName)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A MongoDB connection string must be provided.", nameof(connectionString));

        if (string.IsNullOrWhiteSpace(databaseName))
            throw new ArgumentException("A MongoDB database name must be provided.", nameof(databaseName));

        services.Replace(ServiceDescriptor.Singleton<IAxonJobStore>(_ => new AxonMongoStore(connectionString, databaseName)));
        services.Replace(ServiceDescriptor.Singleton<IAxonRecurringJobStore>(_ => new AxonMongoRecurringJobStore(connectionString, databaseName)));
        services.Replace(ServiceDescriptor.Singleton<IAxonServerInstanceStore>(_ => new AxonMongoInstanceStore(connectionString, databaseName)));
        services.Replace(ServiceDescriptor.Singleton<IDeviceConnectionRegistry>(_ => new AxonMongoDeviceConnectionStore(connectionString, databaseName)));
    }
}
