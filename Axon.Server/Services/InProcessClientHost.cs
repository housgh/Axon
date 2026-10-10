using Axon.Client.Services;
using Microsoft.Extensions.Hosting;

namespace Axon.Server.Services;

/// <summary>
/// Lifecycle for monolith mode (<c>AddInProcessClient</c>): points <see cref="JobActivator.Current"/>
/// at the host's DI container (the same thing <c>AddAxonClient</c>'s starter does), shows this
/// process in the dashboard's Clients tab, and on shutdown lets in-flight jobs finish.
/// </summary>
internal sealed class InProcessClientHost(
    IServiceProvider serviceProvider,
    InProcessJobDispatcher dispatcher,
    IDeviceConnectionRegistry deviceRegistry,
    AxonInProcessClientOptions options) : IHostedService
{
    private string ConnectionId => $"in-process:{options.DeviceName}";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        JobActivator.Current = new ServiceProviderJobActivator(serviceProvider);
        return deviceRegistry.Register(options.DeviceName, ConnectionId);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await dispatcher.StopAsync(cancellationToken);
        await deviceRegistry.Unregister(ConnectionId);
    }
}
