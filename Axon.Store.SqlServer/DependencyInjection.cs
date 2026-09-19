// ReSharper disable CheckNamespace

using Axon.Server.Interfaces;
using Axon.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Axon.SqlServer;

public static class DependencyInjection
{
    public static void AddAxonSqlServerStore(this IServiceCollection services, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A SQL Server connection string must be provided.", nameof(connectionString));

        services.Replace(ServiceDescriptor.Singleton<IAxonJobStore>(_ => new AxonSqlServerStore(connectionString)));
        services.Replace(ServiceDescriptor.Singleton<IAxonRecurringJobStore>(_ => new AxonSqlServerRecurringJobStore(connectionString)));
        services.Replace(ServiceDescriptor.Singleton<IAxonServerInstanceStore>(_ => new AxonSqlServerInstanceStore(connectionString)));
        services.Replace(ServiceDescriptor.Singleton<IDeviceConnectionRegistry>(_ => new AxonSqlServerDeviceConnectionStore(connectionString)));
    }
}
