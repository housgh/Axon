using Axon.Server.Exceptions;
using Npgsql;

namespace Axon.Postgres;

/// <summary>
/// Every AxonPostgres*Store method runs its Dapper call through here, so a down/unreachable
/// Postgres or an unprovisioned database surfaces as an actionable AxonStoreException (with the
/// original NpgsqlException preserved as InnerException) instead of a raw ADO.NET exception with
/// no indication of what to check or fix.
/// </summary>
internal static class PostgresExceptionTranslator
{
    // https://www.postgresql.org/docs/current/errcodes-appendix.html
    private const string UndefinedTableSqlState = "42P01";

    private static readonly HashSet<string> ConnectivityErrorSqlStates = new()
    {
        "08000", // connection_exception
        "08001", // sqlclient_unable_to_establish_sqlconnection
        "08003", // connection_does_not_exist
        "08004", // sqlserver_rejected_establishment_of_sqlconnection
        "08006", // connection_failure
        "28000", // invalid_authorization_specification
        "28P01", // invalid_password
        "3D000"  // invalid_catalog_name (database does not exist)
    };

    public static async Task Run(string connectionString, Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (NpgsqlException e)
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
        catch (NpgsqlException e)
        {
            throw Translate(connectionString, e);
        }
    }

    private static AxonStoreException Translate(string connectionString, NpgsqlException e)
    {
        var dataSource = GetDataSourceForMessage(connectionString);

        if (e is PostgresException pgEx)
        {
            if (pgEx.SqlState == UndefinedTableSqlState)
            {
                return new AxonSchemaNotProvisionedException(
                    $"The Axon schema is missing on the database at '{dataSource}' ({pgEx.Message.Trim()}). " +
                    "Run Axon.Store.Postgres/Schema.sql against it before using AddAxonPostgresStore.",
                    e);
            }

            if (ConnectivityErrorSqlStates.Contains(pgEx.SqlState ?? string.Empty))
            {
                return new AxonStoreException(
                    $"Could not reach PostgreSQL at '{dataSource}'. Check that it is running, " +
                    $"reachable from this process, and that the configured credentials are correct. " +
                    $"({pgEx.Message.Trim()})",
                    e);
            }

            return new AxonStoreException($"PostgreSQL operation against '{dataSource}' failed. ({pgEx.Message.Trim()})", e);
        }

        // A non-PostgresException NpgsqlException (e.g. NpgsqlTimeoutException, or a socket
        // failure the client-side driver raised before the server ever responded) is always a
        // connectivity-shaped failure - there was no query response to classify.
        return new AxonStoreException(
            $"Could not reach PostgreSQL at '{dataSource}'. Check that it is running, " +
            $"reachable from this process, and that the configured credentials are correct. " +
            $"({e.Message.Trim()})",
            e);
    }

    // Only the host/port, never credentials, so this is always safe to put in an exception
    // message that might end up in logs.
    private static string GetDataSourceForMessage(string connectionString)
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            return $"{builder.Host}:{builder.Port}";
        }
        catch
        {
            return "<unparseable connection string>";
        }
    }
}
