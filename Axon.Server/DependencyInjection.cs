// ReSharper disable CheckNamespace

using Axon.Server.Hubs;
using Axon.Server.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Axon.Server.DependencyInjection;

public static class DependencyInjection
{
    public static void AddAxonServer(this IServiceCollection services)
    {
        services.AddSignalR();
        services.AddScoped<IAxonJobService, AxonJobService>();
        services.AddHostedService<AxonJobProcessor>();
    }

    public static void UseAxonServer(this WebApplication app)
    {
        app.MapHub<AxonHub>("/hubs/axon");
    }
}