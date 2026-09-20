using Axon.Server.Interfaces;
using Axon.Server.Services;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Axon.MongoDb;

/// <summary>
/// Publishes this instance's device connections to a shared collection, so the dashboard's
/// Clients tab reflects every instance's connections rather than only whichever instance answers
/// a given request. A stale document - left behind if an instance died without a clean SignalR
/// disconnect (OnDisconnectedAsync never running to call Unregister) - is filtered out by
/// joining against ServerInstances and checking the owning instance's heartbeat, the same
/// staleness check the Servers tab already uses for IsOnline.
/// </summary>
public class AxonMongoDeviceConnectionStore(string connectionString, string databaseName) : IDeviceConnectionRegistry
{
    // Matches DependencyInjection.ServerInstanceOfflineTimeout: an instance (and therefore its
    // published connections) is considered gone once its heartbeat is older than this.
    private static readonly long OfflineTimeoutTicks = TimeSpan.FromSeconds(45).Ticks;

    private readonly MongoClient _client = new(connectionString);

    private IMongoDatabase Database => _client.GetDatabase(databaseName);
    private IMongoCollection<BsonDocument> DeviceConnections => Database.GetCollection<BsonDocument>(CollectionNames.DeviceConnections);
    private IMongoCollection<BsonDocument> ServerInstances => Database.GetCollection<BsonDocument>(CollectionNames.ServerInstances);

    public Task Register(string deviceName, string connectionId) => MongoExceptionTranslator.Run(connectionString, async () =>
    {
        // A re-Register (e.g. SignalR auto-reconnect) with the same connection id must be a
        // no-op - ConnectedAt only resets on a genuinely new connection id, mirroring
        // InMemoryDeviceConnectionRegistry's AddOrUpdate semantics - so this upserts only when
        // no document exists yet for this device, or an existing one has a different
        // ConnectionId; when a document exists with the same ConnectionId, the filter excludes
        // it and UpdateOneAsync leaves it untouched rather than upserting a duplicate.
        var filter = Builders<BsonDocument>.Filter.Eq("_id", deviceName)
            & Builders<BsonDocument>.Filter.Ne("ConnectionId", connectionId);
        var update = Builders<BsonDocument>.Update
            .Set("ConnectionId", connectionId)
            .Set("InstanceId", AxonServerInstanceHeartbeat.InstanceId)
            .Set("ConnectedAt", DateTime.UtcNow.Ticks);
        await DeviceConnections.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true });
    });

    public Task Unregister(string connectionId) => MongoExceptionTranslator.Run(connectionString, async () =>
    {
        await DeviceConnections.DeleteOneAsync(Builders<BsonDocument>.Filter.Eq("ConnectionId", connectionId));
    });

    public Task<string?> GetConnectionId(string deviceName) => MongoExceptionTranslator.Run(connectionString, async () =>
    {
        var cutoff = DateTime.UtcNow.Ticks - OfflineTimeoutTicks;
        var doc = await DeviceConnections.Find(Builders<BsonDocument>.Filter.Eq("_id", deviceName)).FirstOrDefaultAsync();
        if (doc is null) return null;

        var instance = await ServerInstances.Find(Builders<BsonDocument>.Filter.Eq("_id", doc["InstanceId"].AsString)).FirstOrDefaultAsync();
        if (instance is null || instance["LastSeenAt"].ToInt64() < cutoff) return null;

        return doc["ConnectionId"].AsString;
    });

    public Task<string?> GetDeviceName(string connectionId) => MongoExceptionTranslator.Run(connectionString, async () =>
    {
        var doc = await DeviceConnections.Find(Builders<BsonDocument>.Filter.Eq("ConnectionId", connectionId)).FirstOrDefaultAsync();
        return doc?["_id"].AsString;
    });

    public Task<List<ConnectedDevice>> GetAll() => MongoExceptionTranslator.Run(connectionString, async () =>
    {
        var cutoff = DateTime.UtcNow.Ticks - OfflineTimeoutTicks;
        var onlineInstanceIds = (await ServerInstances
                .Find(Builders<BsonDocument>.Filter.Gte("LastSeenAt", cutoff))
                .ToListAsync())
            .Select(d => d["_id"].AsString)
            .ToHashSet();

        var docs = await DeviceConnections.Find(FilterDefinition<BsonDocument>.Empty)
            .SortByDescending(d => d["ConnectedAt"])
            .ToListAsync();

        return docs
            .Where(d => onlineInstanceIds.Contains(d["InstanceId"].AsString))
            .Select(d => new ConnectedDevice
            {
                DeviceName = d["_id"].AsString,
                ConnectionId = d["ConnectionId"].AsString,
                ConnectedAt = d["ConnectedAt"].ToInt64()
            })
            .ToList();
    });
}
