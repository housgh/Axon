// ReSharper disable CheckNamespace

using Axon.Server.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Axon.SqlServer;

public static class DependencyInjection
{
    public static void AddAxonSqlServerStore(this IServiceCollection services, string connectionString)
    {
        services.Replace(ServiceDescriptor.Singleton<IAxonJobStore>(_ => new AxonSqlServerStore(connectionString)));
    }
}
