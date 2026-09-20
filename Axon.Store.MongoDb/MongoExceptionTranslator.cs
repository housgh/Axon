using Axon.Server.Exceptions;
using MongoDB.Driver;

namespace Axon.MongoDb;

/// <summary>
/// Every AxonMongo*Store method runs its driver call through here, so a down/unreachable MongoDB
/// surfaces as an actionable AxonStoreException (with the original MongoException preserved as
/// InnerException) instead of a raw driver exception with no indication of what to check or fix.
/// <para/>
/// Unlike the SQL backends' translators, there is no "schema not provisioned" case to detect:
/// MongoDB collections are created implicitly on first write, so
/// <see cref="AxonSchemaNotProvisionedException"/> is never thrown here - Axon.Store.MongoDb has
/// no Schema.sql to run first (see EnsureIndexesAsync for the one setup step it does have,
/// which is optional).
/// </summary>
internal static class MongoExceptionTranslator
{
    public static async Task Run(string connectionString, Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (MongoException e)
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
        catch (MongoException e)
        {
            throw Translate(connectionString, e);
        }
    }

    private static AxonStoreException Translate(string connectionString, MongoException e)
    {
        var dataSource = GetDataSourceForMessage(connectionString);

        if (e is MongoConnectionException or MongoConnectionPoolPausedException or MongoExecutionTimeoutException ||
            (e is MongoCommandException cmdEx && cmdEx.Code is 13 or 18)) // 13 = Unauthorized, 18 = AuthenticationFailed
        {
            return new AxonStoreException(
                $"Could not reach MongoDB at '{dataSource}'. Check that it is running, " +
                $"reachable from this process, and that the configured credentials are correct. " +
                $"({e.Message.Trim()})",
                e);
        }

        return new AxonStoreException($"MongoDB operation against '{dataSource}' failed. ({e.Message.Trim()})", e);
    }

    // Only the hosts, never credentials, so this is always safe to put in an exception message
    // that might end up in logs.
    private static string GetDataSourceForMessage(string connectionString)
    {
        try
        {
            var url = new MongoUrl(connectionString);
            return string.Join(",", url.Servers.Select(s => $"{s.Host}:{s.Port}"));
        }
        catch
        {
            return "<unparseable connection string>";
        }
    }
}
