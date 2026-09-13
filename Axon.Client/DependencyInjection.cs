// ReSharper disable CheckNamespace

using Axon.Client.Services;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Axon.Client.DependencyInjection;

public static class DependencyInjection
{
    public static void AddAxonClient(this IServiceCollection services, string baseUrl)
    {
        baseUrl = baseUrl.TrimEnd('/');
        var connection = new HubConnectionBuilder()
            .WithUrl($"{baseUrl}/hubs/axon")
            .WithAutomaticReconnect()
            .Build();

        services.AddSingleton(connection);
        services.AddSingleton<IAxonClient, AxonClient>();
    }
}