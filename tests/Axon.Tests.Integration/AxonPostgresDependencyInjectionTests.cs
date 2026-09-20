using Axon.Postgres;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Axon.Tests.Integration;

public class AxonPostgresDependencyInjectionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddAxonPostgresStore_InvalidConnectionString_ThrowsWithClearMessage(string? connectionString)
    {
        var services = new ServiceCollection();

        var act = () => services.AddAxonPostgresStore(connectionString!);

        act.Should().Throw<ArgumentException>().WithMessage("*connection string*");
    }
}
