namespace Axon.Server.Services;

/// <summary>
/// Holds the PBKDF2 hash of each configured dashboard user's password, computed once at startup
/// from the plaintext <see cref="DashboardUser.Password"/> values in <see cref="DashboardAuthOptions"/>.
/// Login verification goes through here (<see cref="Verify"/>) rather than comparing against
/// DashboardAuthOptions.Users directly, so the plaintext password is never what's compared
/// against on each request.
/// </summary>
internal class DashboardUserCredentialStore
{
    private readonly Dictionary<string, string> _hashesByUsername;

    public DashboardUserCredentialStore(DashboardAuthOptions options)
    {
        _hashesByUsername = options.Users.ToDictionary(
            u => u.Username,
            u => PasswordHasher.Hash(u.Password),
            StringComparer.OrdinalIgnoreCase);
    }

    public bool Verify(string username, string password)
    {
        return _hashesByUsername.TryGetValue(username, out var hash) && PasswordHasher.Verify(password, hash);
    }
}
