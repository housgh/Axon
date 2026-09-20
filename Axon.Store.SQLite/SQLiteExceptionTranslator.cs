using Axon.Server.Exceptions;
using Microsoft.Data.Sqlite;

namespace Axon.SQLite;

/// <summary>
/// Every AxonSQLite*Store method runs its Dapper call through here, so an unprovisioned database
/// or an unreachable file surfaces as an actionable AxonStoreException (with the original
/// SqliteException preserved as InnerException) instead of a raw ADO.NET exception with no
/// indication of what to check or fix.
/// </summary>
internal static class SQLiteExceptionTranslator
{
    // https://www.sqlite.org/rescode.html
    private const int SqliteErrorGeneric = 1; // SQLITE_ERROR, e.g. "no such table"
    private const int SqliteCantOpen = 14;    // SQLITE_CANTOPEN - can't open the database file

    public static async Task Run(string connectionString, Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (SqliteException e)
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
        catch (SqliteException e)
        {
            throw Translate(connectionString, e);
        }
    }

    private static AxonStoreException Translate(string connectionString, SqliteException e)
    {
        var dataSource = GetDataSourceForMessage(connectionString);

        if (e.SqliteErrorCode == SqliteErrorGeneric && e.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
        {
            return new AxonSchemaNotProvisionedException(
                $"The Axon schema is missing on the database at '{dataSource}' ({e.Message.Trim()}). " +
                "Run Axon.Store.SQLite/Schema.sql against it before using AddAxonSQLiteStore.",
                e);
        }

        if (e.SqliteErrorCode == SqliteCantOpen)
        {
            return new AxonStoreException(
                $"Could not open the SQLite database at '{dataSource}'. Check that the file path is " +
                $"valid and writable by this process. ({e.Message.Trim()})",
                e);
        }

        return new AxonStoreException($"SQLite operation against '{dataSource}' failed. ({e.Message.Trim()})", e);
    }

    // Only the file path, never any embedded credentials (SQLite connection strings don't
    // usually carry any, but this stays consistent with the other backends' translators).
    private static string GetDataSourceForMessage(string connectionString)
    {
        try
        {
            return new SqliteConnectionStringBuilder(connectionString).DataSource;
        }
        catch
        {
            return "<unparseable connection string>";
        }
    }
}
