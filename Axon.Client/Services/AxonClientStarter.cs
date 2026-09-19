using Microsoft.Extensions.Hosting;

namespace Axon.Client.Services;

// AddAxonClient registers IAxonClient as a lazy singleton, so without this its SignalR
// connection would only start (and the device only register itself with the server) the first
// time something actually injects IAxonClient - e.g. on the app's first EnqueueAsync call, not
// on app startup. Resolving it once here forces AxonClient's constructor to run eagerly, so the
// device shows up in the dashboard's Clients tab as soon as the host starts.
internal class AxonClientStarter(IAxonClient client) : IHostedService
{
    // Merely injecting IAxonClient above is what forces the DI container to construct the
    // AxonClient singleton (and start its connection) during app startup rather than on first
    // use - this field only exists so the constructor parameter isn't flagged as unused.
    private readonly IAxonClient _client = client;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
