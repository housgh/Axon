using Axon.Server.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Axon.Server.Services;

public class AxonDashboardNotifier(IHubContext<AxonDashboardHub> hub) : IAxonDashboardNotifier
{
    public Task JobsChanged() => hub.Clients.All.SendAsync("JobsChanged");
    public Task RecurringJobsChanged() => hub.Clients.All.SendAsync("RecurringJobsChanged");
    public Task ServersChanged() => hub.Clients.All.SendAsync("ServersChanged");
    public Task ClientsChanged() => hub.Clients.All.SendAsync("ClientsChanged");
}
