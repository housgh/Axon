using Axon.Server.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Axon.Server.Services;

/// <summary>
/// The default dispatcher: pushes a job over SignalR to the device that enqueued it. A job whose
/// device isn't currently connected can't be dispatched, and waits for a later poll.
/// </summary>
public class SignalRJobDispatcher(IHubContext<AxonHub> hubContext, IDeviceConnectionRegistry deviceRegistry) : IAxonJobDispatcher
{
    public async ValueTask<bool> CanDispatchAsync(Job job) =>
        await deviceRegistry.GetConnectionId(job.DeviceName) is not null;

    public async Task DispatchAsync(Job job, CancellationToken cancellationToken)
    {
        var connectionId = await deviceRegistry.GetConnectionId(job.DeviceName)
            ?? throw new InvalidOperationException($"Device {job.DeviceName} disconnected before job {job.JobId} could be dispatched.");

        await hubContext.Clients.Client(connectionId)
            .SendCoreAsync("Invoke", [job.JobId, job], cancellationToken);
    }
}
