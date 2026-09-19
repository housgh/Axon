using Axon.Server.Interfaces;
using Microsoft.Extensions.Hosting;

namespace Axon.Server.Services;

public class AxonServerInstanceHeartbeat(IAxonServerInstanceStore instanceStore, IAxonDashboardNotifier notifier) : BackgroundService
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
                LastSeenAt = DateTime.UtcNow.Ticks
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
