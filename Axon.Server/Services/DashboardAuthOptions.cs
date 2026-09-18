namespace Axon.Server.Services;

public enum DashboardRole
{
    ReadOnly,
    Admin,
}

public class DashboardUser
{
    public string Username { get; set; } = null!;
    public string Password { get; set; } = null!;
    public DashboardRole Role { get; set; } = DashboardRole.Admin;
}

public class DashboardAuthOptions
{
    public List<DashboardUser> Users { get; set; } = [];
}
