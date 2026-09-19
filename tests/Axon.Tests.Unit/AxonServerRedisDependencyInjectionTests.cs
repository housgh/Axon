using Axon.Server.DependencyInjection;
using Axon.Server.Hubs;
using Axon.Server.Redis;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace Axon.Tests.Unit;

public class AxonServerRedisDependencyInjectionTests
{
    [Fact]
    public void AddRedisBackplane_RegistersRedisHubLifetimeManager()
    {
        // AddStackExchangeRedis registers the backplane lazily - the actual connection attempt
        // happens when a hub is first used, not at DI registration time - so this can assert the
        // wiring succeeded without a live Redis instance, by checking the service descriptor for
        // the Redis-backed HubLifetimeManager<> (which replaces the default in-process one) got
        // registered, rather than fully resolving it (which needs ASP.NET Core hosting infra a
        // bare ServiceCollection doesn't provide).
        var services = new ServiceCollection();

        services.AddAxonServer().AddRedisBackplane("localhost:0");

        services.Should().Contain(d =>
            d.ServiceType.IsGenericType &&
            d.ServiceType.GetGenericTypeDefinition() == typeof(HubLifetimeManager<>) &&
            d.ImplementationType != null &&
            d.ImplementationType.FullName!.Contains("Redis"));
    }

    [Fact]
    public void AddRedisBackplane_ReturnsSameBuilderForChaining()
    {
        var services = new ServiceCollection();
        var original = services.AddAxonServer();

        var result = original.AddRedisBackplane("localhost:0");

        result.Should().BeSameAs(original);
    }
}
