using Axon.MySql;
using Axon.Server.Services;
using FluentAssertions;

namespace Axon.Tests.Integration;

[Collection(MySqlCollection.Name)]
public class AxonMySqlDeviceConnectionStoreTests(MySqlFixture fixture)
{
    // GetConnectionId/GetAll join against ServerInstances and filter by heartbeat freshness, and
    // AxonMySqlDeviceConnectionStore.Register always stamps rows with the current process's
    // static AxonServerInstanceHeartbeat.InstanceId - so every row in these tests is "owned" by
    // that one instance, and we heartbeat/age it directly via AxonMySqlInstanceStore to
    // control whether it reads as online or stale. Device names and connection ids are
    // randomized per test since tests in this class run concurrently against the same table.
    private readonly string _instanceId = AxonServerInstanceHeartbeat.InstanceId;

    private AxonMySqlDeviceConnectionStore CreateSut() => new(fixture.ConnectionString);
    private AxonMySqlInstanceStore CreateInstanceStore() => new(fixture.ConnectionString);

    private Task Heartbeat(long lastSeenAt) =>
        CreateInstanceStore().Heartbeat(new ServerInstance
        {
            InstanceId = _instanceId,
            MachineName = "test-machine",
            StartedAt = lastSeenAt,
            LastSeenAt = lastSeenAt
        });

    [Fact]
    public async Task Register_ThenGetConnectionId_ReturnsIt_WhenInstanceIsOnline()
    {
        await Heartbeat(DateTime.UtcNow.Ticks);
        var sut = CreateSut();
        var deviceName = Guid.NewGuid().ToString();
        var connectionId = Guid.NewGuid().ToString();

        await sut.Register(deviceName, connectionId);

        (await sut.GetConnectionId(deviceName)).Should().Be(connectionId);
    }

    [Fact]
    public async Task Register_SameDeviceTwice_UpdatesConnectionId()
    {
        await Heartbeat(DateTime.UtcNow.Ticks);
        var sut = CreateSut();
        var deviceName = Guid.NewGuid().ToString();
        var firstConnectionId = Guid.NewGuid().ToString();
        var secondConnectionId = Guid.NewGuid().ToString();
        await sut.Register(deviceName, firstConnectionId);

        await sut.Register(deviceName, secondConnectionId);

        (await sut.GetConnectionId(deviceName)).Should().Be(secondConnectionId);
    }

    [Fact]
    public async Task GetDeviceName_ReturnsRegisteredName_RegardlessOfStaleness()
    {
        // GetDeviceName backs OnDisconnectedAsync's stranded-job reclaim path, which must still
        // find the device even if the owning instance's heartbeat has since gone stale.
        await Heartbeat(DateTime.UtcNow.AddDays(-1).Ticks);
        var sut = CreateSut();
        var deviceName = Guid.NewGuid().ToString();
        var connectionId = Guid.NewGuid().ToString();
        await sut.Register(deviceName, connectionId);

        (await sut.GetDeviceName(connectionId)).Should().Be(deviceName);
    }

    [Fact]
    public async Task Unregister_RemovesConnection()
    {
        await Heartbeat(DateTime.UtcNow.Ticks);
        var sut = CreateSut();
        var deviceName = Guid.NewGuid().ToString();
        var connectionId = Guid.NewGuid().ToString();
        await sut.Register(deviceName, connectionId);

        await sut.Unregister(connectionId);

        (await sut.GetConnectionId(deviceName)).Should().BeNull();
        (await sut.GetDeviceName(connectionId)).Should().BeNull();
    }

    [Fact]
    public async Task GetConnectionId_OwningInstanceStale_ReturnsNull()
    {
        await Heartbeat(DateTime.UtcNow.AddDays(-1).Ticks);
        var sut = CreateSut();
        var deviceName = Guid.NewGuid().ToString();
        var connectionId = Guid.NewGuid().ToString();
        await sut.Register(deviceName, connectionId);

        (await sut.GetConnectionId(deviceName)).Should().BeNull();
    }

    [Fact]
    public async Task GetAll_OwningInstanceStale_ExcludesConnection()
    {
        await Heartbeat(DateTime.UtcNow.AddDays(-1).Ticks);
        var sut = CreateSut();
        var deviceName = Guid.NewGuid().ToString();
        var connectionId = Guid.NewGuid().ToString();
        await sut.Register(deviceName, connectionId);

        var all = await sut.GetAll();

        all.Should().NotContain(d => d.DeviceName == deviceName);
    }

    [Fact]
    public async Task GetAll_OwningInstanceOnline_IncludesConnection()
    {
        await Heartbeat(DateTime.UtcNow.Ticks);
        var sut = CreateSut();
        var deviceName = Guid.NewGuid().ToString();
        var connectionId = Guid.NewGuid().ToString();
        await sut.Register(deviceName, connectionId);

        var all = await sut.GetAll();

        all.Should().Contain(d => d.DeviceName == deviceName && d.ConnectionId == connectionId);
    }
}
