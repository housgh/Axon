using Axon.MongoDb;
using Axon.Server.Services;
using FluentAssertions;

namespace Axon.Tests.Integration;

[Collection(MongoDbCollection.Name)]
public class AxonMongoInstanceStoreTests(MongoDbFixture fixture)
{
    private AxonMongoInstanceStore CreateSut() => new(fixture.ConnectionString, fixture.DatabaseName);

    [Fact]
    public async Task Heartbeat_NewInstance_Inserts()
    {
        var sut = CreateSut();
        var instance = new ServerInstance
        {
            InstanceId = Guid.NewGuid().ToString(),
            MachineName = "test-machine",
            StartedAt = 100,
            LastSeenAt = 100
        };

        await sut.Heartbeat(instance);
        var all = await sut.GetAll();

        all.Should().Contain(i => i.InstanceId == instance.InstanceId && i.MachineName == "test-machine");
    }

    [Fact]
    public async Task Heartbeat_ExistingInstance_UpdatesLastSeenAtOnly()
    {
        var sut = CreateSut();
        var id = Guid.NewGuid().ToString();
        await sut.Heartbeat(new ServerInstance { InstanceId = id, MachineName = "m1", StartedAt = 100, LastSeenAt = 100 });

        await sut.Heartbeat(new ServerInstance { InstanceId = id, MachineName = "m1", StartedAt = 100, LastSeenAt = 200 });

        var all = await sut.GetAll();
        all.Count(i => i.InstanceId == id).Should().Be(1);
        all.Single(i => i.InstanceId == id).LastSeenAt.Should().Be(200);
    }

    [Fact]
    public async Task Heartbeat_NewInstance_DefaultsServedQueuesToDefault()
    {
        var sut = CreateSut();
        var instance = new ServerInstance
        {
            InstanceId = Guid.NewGuid().ToString(),
            MachineName = "test-machine",
            StartedAt = 100,
            LastSeenAt = 100
        };

        await sut.Heartbeat(instance);
        var all = await sut.GetAll();

        all.Single(i => i.InstanceId == instance.InstanceId).ServedQueues.Should().Be("default");
    }

    [Fact]
    public async Task Heartbeat_ExistingInstance_OverwritesServedQueues()
    {
        var sut = CreateSut();
        var id = Guid.NewGuid().ToString();
        await sut.Heartbeat(new ServerInstance { InstanceId = id, MachineName = "m1", StartedAt = 100, LastSeenAt = 100, ServedQueues = "default" });

        await sut.Heartbeat(new ServerInstance { InstanceId = id, MachineName = "m1", StartedAt = 100, LastSeenAt = 200, ServedQueues = "default,billing" });

        var all = await sut.GetAll();
        all.Single(i => i.InstanceId == id).ServedQueues.Should().Be("default,billing");
    }

    [Fact]
    public async Task Remove_DeletesInstance()
    {
        var sut = CreateSut();
        var id = Guid.NewGuid().ToString();
        await sut.Heartbeat(new ServerInstance { InstanceId = id, MachineName = "m1", StartedAt = 100, LastSeenAt = 100 });

        await sut.Remove(id);

        var all = await sut.GetAll();
        all.Should().NotContain(i => i.InstanceId == id);
    }

    [Fact]
    public async Task GetAll_OrdersByLastSeenAtDescending()
    {
        var sut = CreateSut();
        var older = Guid.NewGuid().ToString();
        var newer = Guid.NewGuid().ToString();
        await sut.Heartbeat(new ServerInstance { InstanceId = older, MachineName = "m1", StartedAt = 100, LastSeenAt = 100 });
        await sut.Heartbeat(new ServerInstance { InstanceId = newer, MachineName = "m2", StartedAt = 200, LastSeenAt = 999 });

        var all = await sut.GetAll();

        var indexOfNewer = all.FindIndex(i => i.InstanceId == newer);
        var indexOfOlder = all.FindIndex(i => i.InstanceId == older);
        indexOfNewer.Should().BeLessThan(indexOfOlder);
    }
}
