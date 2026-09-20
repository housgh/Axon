using Axon.SQLite;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Axon.Tests.Integration;

public class AxonSQLiteDependencyInjectionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddAxonSQLiteStore_InvalidConnectionString_ThrowsWithClearMessage(string? connectionString)
    {
        var services = new ServiceCollection();

        var act = () => services.AddAxonSQLiteStore(connectionString!);

        act.Should().Throw<ArgumentException>().WithMessage("*connection string*");
    }
}
