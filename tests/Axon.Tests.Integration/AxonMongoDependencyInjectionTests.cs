using Axon.MongoDb;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Axon.Tests.Integration;

public class AxonMongoDependencyInjectionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddAxonMongoDbStore_InvalidConnectionString_ThrowsWithClearMessage(string? connectionString)
    {
        var services = new ServiceCollection();

        var act = () => services.AddAxonMongoDbStore(connectionString!, "axon");

        act.Should().Throw<ArgumentException>().WithMessage("*connection string*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddAxonMongoDbStore_InvalidDatabaseName_ThrowsWithClearMessage(string? databaseName)
    {
        var services = new ServiceCollection();

        var act = () => services.AddAxonMongoDbStore("mongodb://localhost:27017", databaseName!);

        act.Should().Throw<ArgumentException>().WithMessage("*database name*");
    }
}
