using Axon.MySql;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Axon.Tests.Integration;

public class AxonMySqlDependencyInjectionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddAxonMySqlStore_InvalidConnectionString_ThrowsWithClearMessage(string? connectionString)
    {
        var services = new ServiceCollection();

        var act = () => services.AddAxonMySqlStore(connectionString!);

        act.Should().Throw<ArgumentException>().WithMessage("*connection string*");
    }
}
