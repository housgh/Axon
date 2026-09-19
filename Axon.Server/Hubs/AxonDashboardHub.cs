using Microsoft.AspNetCore.SignalR;

namespace Axon.Server.Hubs;

/// <summary>
/// Push channel for the dashboard UI. Viewers connect here to receive change notifications
/// instead of polling; this hub carries no job-dispatch traffic (see AxonHub for that) so its
/// auth requirements (dashboard cookie, when configured) can differ from the client-facing hub.
/// </summary>
public class AxonDashboardHub : Hub;
