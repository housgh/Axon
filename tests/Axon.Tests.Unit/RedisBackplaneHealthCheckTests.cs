using Axon.Server.Redis;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Axon.Tests.Unit;

public class RedisBackplaneHealthCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_RedisUnreachable_ReturnsUnhealthyWithActionableMessage()
    {
        // Port 0 is never a real listening Redis instance, and AbortOnConnectFail/a short
        // ConnectTimeout inside RedisBackplaneHealthCheck keep this from hanging - proving the
        // check reports Unhealthy rather than letting ConnectionMultiplexer's own exception
        // propagate uncaught.
        var sut = new RedisBackplaneHealthCheck("localhost:0");

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("Could not reach the Redis backplane");
        result.Exception.Should().NotBeNull();
    }
}
