using Axon.Server.Exceptions;
using Microsoft.Data.SqlClient;

namespace Axon.SqlServer;

/// <summary>
/// Every AxonSqlServer*Store method runs its Dapper call through here, so a down/unreachable SQL
/// Server or an unprovisioned database surfaces as an actionable AxonStoreException (with the
/// original SqlException preserved as InnerException) instead of a raw ADO.NET exception with no
/// indication of what to check or fix.
/// </summary>
internal static class SqlExceptionTranslator
{
    // Connectivity/auth failures - the server itself was unreachable, or refused the login -
    // as opposed to a query-level failure against a server that did respond.
    private static readonly HashSet<int> ConnectivityErrorNumbers = new()
    {
        -2,     // Timeout expired
        2,      // A network-related or instance-specific error (server not found/not accessible)
        53,     // Named Pipes / TCP provider: could not open a connection
        4060,   // Cannot open database "X" requested by the login
        18456,  // Login failed for user
        18452,  // Login failed (not associated with a trusted SQL Server connection)
        10060,  // Winsock: connection timed out
        10061,  // Winsock: connection refused (nothing listening on the target host/port)
        10054   // Winsock: connection reset by peer
    };

    // Invalid object name 'X' - the connection succeeded, but a table this store expects
    // doesn't exist, meaning Schema.sql was never run against this database.
    private const int InvalidObjectNameErrorNumber = 208;

    public static async Task Run(string connectionString, Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (SqlException e)
        {
            throw Translate(connectionString, e);
        }
    }

    public static async Task<T> Run<T>(string connectionString, Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (SqlException e)
        {
            throw Translate(connectionString, e);
        }
    }

    private static AxonStoreException Translate(string connectionString, SqlException e)
    {
        var dataSource = GetDataSourceForMessage(connectionString);

        if (e.Number == InvalidObjectNameErrorNumber)
        {
            return new AxonSchemaNotProvisionedException(
                $"The Axon schema is missing on the database at '{dataSource}' ({e.Message.Trim()}). " +
                "Run Axon.Store.SqlServer/Schema.sql against it before using AddAxonSqlServerStore.",
                e);
        }

        if (IsConnectivityFailure(e))
        {
            return new AxonStoreException(
                $"Could not reach SQL Server at '{dataSource}'. Check that it is running, " +
                $"reachable from this process, and that the configured credentials are correct. " +
                $"({e.Message.Trim()})",
                e);
        }

        return new AxonStoreException($"SQL Server operation against '{dataSource}' failed. ({e.Message.Trim()})", e);
    }

    // A connection-level failure's exact SqlException.Number varies by platform/transport (the
    // underlying socket error is wrapped differently on Windows vs. Linux, and even the same
    // socket error can surface under different numbers across TCP provider versions), so an
    // error-number allowlist alone isn't reliable across environments - SQL Server's own
    // canonical "couldn't connect at all" message text is more stable than its wrapped error
    // number, so it's checked as a fallback alongside the known numbers.
    private static bool IsConnectivityFailure(SqlException e) =>
        ConnectivityErrorNumbers.Contains(e.Number) ||
        e.Message.Contains("A network-related or instance-specific error occurred", StringComparison.OrdinalIgnoreCase) ||
        e.Message.Contains("Connection Timeout Expired", StringComparison.OrdinalIgnoreCase);

    // Only the host/port, never credentials, so this is always safe to put in an exception
    // message that might end up in logs.
    private static string GetDataSourceForMessage(string connectionString)
    {
        try
        {
            return new SqlConnectionStringBuilder(connectionString).DataSource;
        }
        catch
        {
            return "<unparseable connection string>";
        }
    }
}
