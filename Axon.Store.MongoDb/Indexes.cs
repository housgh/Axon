using MongoDB.Bson;
using MongoDB.Driver;

namespace Axon.MongoDb;

/// <summary>
/// MongoDB collections and their documents need no upfront schema - unlike the SQL backends,
/// there is no Schema.sql to run before <c>AddAxonMongoDbStore</c>. Indexes are optional (queries
/// work without them, just slower at scale), so this is offered as an explicit opt-in call
/// rather than something the store creates automatically on first use, matching how the SQL
/// backends require you to run their Schema.sql yourself rather than doing it implicitly.
/// </summary>
public static class Indexes
{
    /// <summary>
    /// Creates the indexes AxonMongo*Store's query patterns rely on for reasonable performance
    /// at scale (mirroring the SQL backends' Schema.sql indexes). Safe to call repeatedly -
    /// <c>CreateOneAsync</c> is a no-op if an equivalent index already exists.
    /// </summary>
    public static async Task EnsureIndexesAsync(string connectionString, string databaseName)
    {
        var database = new MongoClient(connectionString).GetDatabase(databaseName);

        var jobs = database.GetCollection<BsonDocument>(CollectionNames.Jobs);
        await jobs.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("State").Ascending("ScheduledFor")),
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("DeviceName")),
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("State").Ascending("ProcessingDeadline")),
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("ConcurrencyKey").Ascending("State")),
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("ParentJobId"))
        ]);

        var jobHistory = database.GetCollection<BsonDocument>(CollectionNames.JobHistory);
        await jobHistory.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("JobId").Ascending("Timestamp")));

        var recurringJobs = database.GetCollection<BsonDocument>(CollectionNames.RecurringJobs);
        await recurringJobs.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("NextRunAt")));

        var serverInstances = database.GetCollection<BsonDocument>(CollectionNames.ServerInstances);
        await serverInstances.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("LastSeenAt")));

        var deviceConnections = database.GetCollection<BsonDocument>(CollectionNames.DeviceConnections);
        await deviceConnections.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("ConnectionId")),
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("InstanceId"))
        ]);
    }
}

internal static class CollectionNames
{
    public const string Jobs = "Jobs";
    public const string JobHistory = "JobHistory";
    public const string RecurringJobs = "RecurringJobs";
    public const string ServerInstances = "ServerInstances";
    public const string DeviceConnections = "DeviceConnections";

    /// <summary>
    /// One document per <c>ConcurrencyKey</c>, written to (never meaningfully read) by every
    /// <see cref="AxonMongoStore.TryClaimJob"/> call for a job with that key - see
    /// AxonMongoStore.TryClaimJobCore for why this collection needs to exist at all (MongoDB's
    /// per-document write-conflict detection needs concurrent claims to collide on a shared
    /// document, since they otherwise write only to their own distinct job document).
    /// </summary>
    public const string ConcurrencyLocks = "ConcurrencyLocks";
}
