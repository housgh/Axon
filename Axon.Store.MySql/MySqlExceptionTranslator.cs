using Axon.Server.Exceptions;
using MySqlConnector;

namespace Axon.MySql;

/// <summary>
/// Every AxonMySql*Store method runs its Dapper call through here, so a down/unreachable MySQL
/// or an unprovisioned database surfaces as an actionable AxonStoreException (with the original
/// MySqlException preserved as InnerException) instead of a raw ADO.NET exception with no
/// indication of what to check or fix.
/// </summary>
internal static class MySqlExceptionTranslator
{
    // https://mariadb.com/kb/en/mariadb-error-codes/ / MySqlErrorCode enum
    private const int TableDoesNotExistErrorNumber = 1146; // ER_NO_SUCH_TABLE

    private static readonly HashSet<int> ConnectivityErrorNumbers = new()
    {
        1042, // ER_BAD_HOST_ERROR - unable to connect to host
        1043, // ER_HANDSHAKE_ERROR
        1044, // ER_DBACCESS_DENIED_ERROR
        1045, // ER_ACCESS_DENIED_ERROR - login failed
        1049, // ER_BAD_DB_ERROR - unknown database
        1129, // ER_HOST_IS_BLOCKED
        1130, // ER_HOST_NOT_PRIVILEGED
        2002, // CR_CONNECTION_ERROR - can't connect (socket)
        2003, // CR_CONN_HOST_ERROR - can't connect to host
        2005, // CR_UNKNOWN_HOST
        2013  // CR_SERVER_LOST
    };

    public static async Task Run(string connectionString, Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (MySqlException e)
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
        catch (MySqlException e)
        {
            throw Translate(connectionString, e);
        }
    }

    private static AxonStoreException Translate(string connectionString, MySqlException e)
    {
        var dataSource = GetDataSourceForMessage(connectionString);

        if (e.ErrorCode == (MySqlErrorCode)TableDoesNotExistErrorNumber)
        {
            return new AxonSchemaNotProvisionedException(
                $"The Axon schema is missing on the database at '{dataSource}' ({e.Message.Trim()}). " +
                "Run Axon.Store.MySql/Schema.sql against it before using AddAxonMySqlStore.",
                e);
        }

        if (ConnectivityErrorNumbers.Contains((int)e.ErrorCode) || e.InnerException is System.Net.Sockets.SocketException)
        {
            return new AxonStoreException(
                $"Could not reach MySQL at '{dataSource}'. Check that it is running, " +
                $"reachable from this process, and that the configured credentials are correct. " +
                $"({e.Message.Trim()})",
                e);
        }

        return new AxonStoreException($"MySQL operation against '{dataSource}' failed. ({e.Message.Trim()})", e);
    }

    // Only the host/port, never credentials, so this is always safe to put in an exception
    // message that might end up in logs.
    private static string GetDataSourceForMessage(string connectionString)
    {
        try
        {
            var builder = new MySqlConnectionStringBuilder(connectionString);
            return $"{builder.Server}:{builder.Port}";
        }
        catch
        {
            return "<unparseable connection string>";
        }
    }
}
