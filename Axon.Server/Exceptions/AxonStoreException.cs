namespace Axon.Server.Exceptions;

/// <summary>
/// Wraps a storage-backend failure (e.g. a <c>Microsoft.Data.SqlClient.SqlException</c> from
/// <c>Axon.Store.SqlServer</c>, or a <c>StackExchange.Redis.RedisException</c>) behind an
/// Axon-specific exception with an actionable message, so callers see "SQL Server at ... is
/// unreachable" instead of a raw ADO.NET/Redis stack trace with no indication of what to check.
/// The original exception is always preserved as <see cref="Exception.InnerException"/>.
/// </summary>
public class AxonStoreException : Exception
{
    public AxonStoreException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// A more specific <see cref="AxonStoreException"/> for the case where the connection to the
/// backing store succeeded but the Axon schema hasn't been provisioned there yet (e.g. a fresh
/// database that <c>Axon.Store.SqlServer/Schema.sql</c> was never run against). Distinguished
/// from a general connectivity failure so callers/logs can point directly at the fix.
/// </summary>
public class AxonSchemaNotProvisionedException : AxonStoreException
{
    public AxonSchemaNotProvisionedException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
