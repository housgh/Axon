using Axon.Server.Interfaces;
using Axon.Server.Services;
using Dapper;
using Npgsql;

namespace Axon.Postgres;

public class AxonPostgresInstanceStore(string connectionString) : IAxonServerInstanceStore
{
    private NpgsqlConnection CreateConnection() =>
        new(connectionString);

    public Task Heartbeat(ServerInstance instance) => PostgresExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        const string sql = @"
            INSERT INTO ""ServerInstances"" (""InstanceId"", ""MachineName"", ""StartedAt"", ""LastSeenAt"")
            VALUES (@InstanceId, @MachineName, @StartedAt, @LastSeenAt)
            ON CONFLICT (""InstanceId"") DO UPDATE SET ""LastSeenAt"" = @LastSeenAt";
        await conn.ExecuteAsync(sql, new
        {
            instance.InstanceId,
            instance.MachineName,
            instance.StartedAt,
            instance.LastSeenAt
        });
    });

    public Task<List<ServerInstance>> GetAll() => PostgresExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        const string sql = @"SELECT * FROM ""ServerInstances"" ORDER BY ""LastSeenAt"" DESC";
        return (await conn.QueryAsync<ServerInstance>(sql)).ToList();
    });

    public Task Remove(string instanceId) => PostgresExceptionTranslator.Run(connectionString, async () =>
    {
        await using var conn = CreateConnection();
        const string sql = @"DELETE FROM ""ServerInstances"" WHERE ""InstanceId"" = @InstanceId";
        await conn.ExecuteAsync(sql, new { InstanceId = instanceId });
    });
}
