using Axon.Client.DependencyInjection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Axon.Tests.Unit;

public class AxonClientDependencyInjectionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddAxonClient_InvalidBaseUrl_ThrowsWithClearMessage(string? baseUrl)
    {
        var services = new ServiceCollection();

        var act = () => services.AddAxonClient(baseUrl!);

        act.Should().Throw<ArgumentException>().WithMessage("*base URL*");
    }
}
