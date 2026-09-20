using Axon.MongoDb;
using Testcontainers.MongoDb;

namespace Axon.Tests.Integration;

/// <summary>
/// Spins up a real MongoDB container once per test collection, so integration tests exercise the
/// actual driver path (transaction/write-conflict semantics, BSON round-tripping) rather than
/// the in-memory store's behavior. WithReplicaSet is required explicitly - MongoDbBuilder starts
/// a plain standalone mongod by default, which rejects StartTransaction outright ("Standalone
/// servers do not support transactions"), so this must opt into single-node replica-set mode to
/// match what AxonMongoStore.TryClaimJob needs (see the README for why it requires one).
/// No Schema.sql to apply first - see Indexes.cs for the (optional) index-creation step,
/// exercised here via EnsureIndexesAsync to match how a real deployment would call it.
/// </summary>
public class MongoDbFixture : IAsyncLifetime
{
    private readonly MongoDbContainer _container = new MongoDbBuilder("mongo:7.0")
        .WithReplicaSet("rs0")
        .Build();

    public string ConnectionString { get; private set; } = null!;
    public string DatabaseName { get; } = "axon_test";

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();

        await Indexes.EnsureIndexesAsync(ConnectionString, DatabaseName);
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public class MongoDbCollection : ICollectionFixture<MongoDbFixture>
{
    public const string Name = "MongoDb";
}
