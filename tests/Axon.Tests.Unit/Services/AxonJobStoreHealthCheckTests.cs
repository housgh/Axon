using Axon.Server.Services;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;

namespace Axon.Tests.Unit.Services;

public class AxonJobStoreHealthCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_StoreReachable_ReturnsHealthy()
    {
        var sut = new AxonJobStoreHealthCheck(new InMemoryAxonJobStore());

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task CheckHealthAsync_StoreThrows_ReturnsUnhealthy()
    {
        var jobStore = Substitute.For<Axon.Server.Interfaces.IAxonJobStore>();
        jobStore.GetJobs(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<Axon.Core.Enums.JobState[]?>())
            .Returns(Task.FromException<List<Job>>(new InvalidOperationException("connection refused")));
        var sut = new AxonJobStoreHealthCheck(jobStore);

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Exception.Should().BeOfType<InvalidOperationException>();
        result.Description.Should().Be("connection refused");
    }
}
