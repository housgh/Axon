using Axon.SqlServer;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Axon.Tests.Integration;

public class AxonSqlServerDependencyInjectionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddAxonSqlServerStore_InvalidConnectionString_ThrowsWithClearMessage(string? connectionString)
    {
        var services = new ServiceCollection();

        var act = () => services.AddAxonSqlServerStore(connectionString!);

        act.Should().Throw<ArgumentException>().WithMessage("*connection string*");
    }
}
