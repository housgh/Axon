using System.Text.Json;
using Axon.Core.Models;
using Axon.Server.Interfaces;
using Axon.Server.Services;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Axon.MongoDb;

public class AxonMongoRecurringJobStore(string connectionString, string databaseName) : IAxonRecurringJobStore
{
    private readonly MongoClient _client = new(connectionString);

    private IMongoCollection<BsonDocument> RecurringJobs => _client.GetDatabase(databaseName).GetCollection<BsonDocument>(CollectionNames.RecurringJobs);

    private static RecurringJob FromDocument(BsonDocument doc) => new(new JobInfo
    {
        Arguments = JsonSerializer.Deserialize<List<object?>>(doc["Arguments"].AsString) ?? [],
        MethodName = doc["MethodName"].AsString,
        Assembly = doc["Assembly"].AsString,
        DeclaringType = doc["DeclaringType"].AsString
    })
    {
        RecurringJobId = doc["_id"].AsString,
        DeviceName = doc["DeviceName"].AsString,
        CronExpression = doc["CronExpression"].AsString,
        NextRunAt = doc["NextRunAt"].ToInt64(),
        LastRunAt = doc["LastRunAt"].IsBsonNull ? null : doc["LastRunAt"].ToInt64(),
        IsPaused = doc["IsPaused"].AsBoolean
    };

    public Task AddOrUpdate(RecurringJob recurringJob) => MongoExceptionTranslator.Run(connectionString, async () =>
    {
        // IsPaused is deliberately absent from the $set, and only defaulted via $setOnInsert: a
        // client re-registering an existing recurring job (AddOrUpdateRecurringAsync is
        // idempotent by id, and this runs on every client startup) has no isPaused concept of
        // its own to declare - IsPaused is purely an operator-driven flag set via SetPaused
        // (e.g. from the dashboard), so a client restart must not silently undo an operator's
        // pause. New documents still default to unpaused.
        var filter = Builders<BsonDocument>.Filter.Eq("_id", recurringJob.RecurringJobId);
        var update = Builders<BsonDocument>.Update
            .Set("DeviceName", recurringJob.DeviceName)
            .Set("Arguments", JsonSerializer.Serialize(recurringJob.Arguments))
            .Set("MethodName", recurringJob.MethodName)
            .Set("Assembly", recurringJob.Assembly)
            .Set("DeclaringType", recurringJob.DeclaringType)
            .Set("CronExpression", recurringJob.CronExpression)
            .Set("NextRunAt", recurringJob.NextRunAt)
            .Set("LastRunAt", recurringJob.LastRunAt is null ? BsonNull.Value : (BsonValue)recurringJob.LastRunAt.Value)
            .SetOnInsert("IsPaused", false);
        await RecurringJobs.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true });
    });

    public Task<RecurringJob?> GetById(string recurringJobId) => MongoExceptionTranslator.Run(connectionString, async () =>
    {
        var doc = await RecurringJobs.Find(Builders<BsonDocument>.Filter.Eq("_id", recurringJobId)).FirstOrDefaultAsync();
        return doc is null ? null : FromDocument(doc);
    });

    public Task<List<RecurringJob>> GetAll(int skip = 0, int take = 20) => MongoExceptionTranslator.Run(connectionString, async () =>
    {
        var docs = await RecurringJobs.Find(FilterDefinition<BsonDocument>.Empty)
            .SortBy(d => d["NextRunAt"])
            .Skip(skip)
            .Limit(take)
            .ToListAsync();
        return docs.Select(FromDocument).ToList();
    });

    public Task UpdateNextRun(string recurringJobId, long nextRunAt, long lastRunAt) => MongoExceptionTranslator.Run(connectionString, async () =>
    {
        var filter = Builders<BsonDocument>.Filter.Eq("_id", recurringJobId);
        var update = Builders<BsonDocument>.Update.Set("NextRunAt", nextRunAt).Set("LastRunAt", lastRunAt);
        await RecurringJobs.UpdateOneAsync(filter, update);
    });

    public Task Remove(string recurringJobId) => MongoExceptionTranslator.Run(connectionString, async () =>
    {
        await RecurringJobs.DeleteOneAsync(Builders<BsonDocument>.Filter.Eq("_id", recurringJobId));
    });

    public Task SetPaused(string recurringJobId, bool isPaused) => MongoExceptionTranslator.Run(connectionString, async () =>
    {
        var filter = Builders<BsonDocument>.Filter.Eq("_id", recurringJobId);
        var update = Builders<BsonDocument>.Update.Set("IsPaused", isPaused);
        await RecurringJobs.UpdateOneAsync(filter, update);
    });

    public Task SkipNext(string recurringJobId, long newNextRunAt) => MongoExceptionTranslator.Run(connectionString, async () =>
    {
        var filter = Builders<BsonDocument>.Filter.Eq("_id", recurringJobId);
        var update = Builders<BsonDocument>.Update.Set("NextRunAt", newNextRunAt);
        await RecurringJobs.UpdateOneAsync(filter, update);
    });
}
