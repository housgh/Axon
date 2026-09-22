using Axon.Server.DependencyInjection;
using Axon.Server.Interfaces;
using Microsoft.Extensions.Hosting;

namespace Axon.Server.Services;

public class AxonServerInstanceHeartbeat(IAxonServerInstanceStore instanceStore, IAxonDashboardNotifier notifier, AxonServerFeatures? features = null) : BackgroundService
{
    private const int HeartbeatInterval = 15000;

    public static readonly string InstanceId = Guid.NewGuid().ToString();
    private static readonly long StartedAt = DateTime.UtcNow.Ticks;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await instanceStore.Heartbeat(new ServerInstance
            {
                InstanceId = InstanceId,
                MachineName = Environment.MachineName,
                StartedAt = StartedAt,
                LastSeenAt = DateTime.UtcNow.Ticks,
                ServedQueues = string.Join(",", features?.ServedQueues ?? new HashSet<string> { "default" })
            });
            await notifier.ServersChanged();

            try
            {
                await Task.Delay(HeartbeatInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await instanceStore.Remove(InstanceId);
        await notifier.ServersChanged();
        await base.StopAsync(cancellationToken);
    }
}
