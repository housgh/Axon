using Axon.Server.Interfaces;
using Axon.Server.Services;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Axon.MongoDb;

public class AxonMongoInstanceStore(string connectionString, string databaseName) : IAxonServerInstanceStore
{
    private readonly MongoClient _client = new(connectionString);

    private IMongoCollection<BsonDocument> ServerInstances => _client.GetDatabase(databaseName).GetCollection<BsonDocument>(CollectionNames.ServerInstances);

    private static ServerInstance FromDocument(BsonDocument doc) => new()
    {
        InstanceId = doc["_id"].AsString,
        MachineName = doc["MachineName"].AsString,
        StartedAt = doc["StartedAt"].ToInt64(),
        LastSeenAt = doc["LastSeenAt"].ToInt64()
    };

    public Task Heartbeat(ServerInstance instance) => MongoExceptionTranslator.Run(connectionString, async () =>
    {
        var filter = Builders<BsonDocument>.Filter.Eq("_id", instance.InstanceId);
        var update = Builders<BsonDocument>.Update
            .Set("LastSeenAt", instance.LastSeenAt)
            .SetOnInsert("MachineName", instance.MachineName)
            .SetOnInsert("StartedAt", instance.StartedAt);
        await ServerInstances.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true });
    });

    public Task<List<ServerInstance>> GetAll() => MongoExceptionTranslator.Run(connectionString, async () =>
    {
        var docs = await ServerInstances.Find(FilterDefinition<BsonDocument>.Empty)
            .SortByDescending(d => d["LastSeenAt"])
            .ToListAsync();
        return docs.Select(FromDocument).ToList();
    });

    public Task Remove(string instanceId) => MongoExceptionTranslator.Run(connectionString, async () =>
    {
        await ServerInstances.DeleteOneAsync(Builders<BsonDocument>.Filter.Eq("_id", instanceId));
    });
}
