// ReSharper disable CheckNamespace

using Axon.Client.Services;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Axon.Client.DependencyInjection;

public static class DependencyInjection
{
    public static void AddAxonClient(this IServiceCollection services, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new ArgumentException("An Axon.Server base URL must be provided.", nameof(baseUrl));

        baseUrl = baseUrl.TrimEnd('/');
        var connection = new HubConnectionBuilder()
            .WithUrl($"{baseUrl}/hubs/axon")
            .WithAutomaticReconnect()
            .Build();

        services.AddSingleton(connection);
        services.AddSingleton<IAxonClient, AxonClient>();
        services.AddHostedService<AxonClientStarter>();
    }
}