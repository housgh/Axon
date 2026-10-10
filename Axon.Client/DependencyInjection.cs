// ReSharper disable CheckNamespace

using Axon.Client.Services;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Axon.Client.DependencyInjection;

public static class DependencyInjection
{
    public static void AddAxonClient(this IServiceCollection services, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new ArgumentException("An Axon.Server base URL must be provided.", nameof(baseUrl));

        if (services.Any(d => d.ServiceType == typeof(IAxonClient)))
            throw new InvalidOperationException(
                "An IAxonClient is already registered (did you also call AddInProcessClient?). " +
                "Register either the SignalR client or the in-process client, not both.");

        baseUrl = baseUrl.TrimEnd('/');
        var connection = new HubConnectionBuilder()
            .WithUrl($"{baseUrl}/hubs/axon")
            .WithAutomaticReconnect()
            .Build();

        // AxonClient takes an ILogger<AxonClient> - AddLogging() is a no-op if the host already
        // configured logging (e.g. any ASP.NET Core/Generic Host app), but without this call here
        // a bare ServiceCollection consumer would fail to resolve IAxonClient entirely.
        services.AddLogging();
        services.AddSingleton(connection);
        services.AddSingleton<IAxonClient, AxonClient>();
        services.AddHostedService<AxonClientStarter>();
    }
}