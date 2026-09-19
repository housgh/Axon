using Axon.Server.Exceptions;
using Axon.SqlServer;
using FluentAssertions;
using Microsoft.Data.SqlClient;

namespace Axon.Tests.Integration;

/// <summary>
/// Proves the actual failure modes SqlExceptionTranslator exists to handle - a down/unreachable
/// SQL Server, and a database that exists but was never provisioned with Axon's schema - rather
/// than just asserting the translator's error-number mapping in the abstract. Every
/// AxonSqlServer*Store method routes through the same shared translator (see
/// Axon.Store.SqlServer/SqlExceptionTranslator.cs), so exercising it once per failure mode here
/// is representative of all 4 stores rather than needing per-store repetition.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class SqlExceptionTranslationTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task UnreachableServer_ThrowsAxonStoreException_NotRawSqlException()
    {
        // A connection string with a valid format but pointing at a closed local port, so the
        // connection attempt fails fast (connection refused) rather than hanging on a DNS/routing
        // timeout - and a short Connect Timeout keeps this test itself fast.
        var unreachableConnectionString =
            "Server=127.0.0.1,1; Database=Axon; User Id=sa; Password=wrong; TrustServerCertificate=True; Connect Timeout=2;";
        var sut = new AxonSqlServerInstanceStore(unreachableConnectionString);

        var act = () => sut.GetAll();

        var exception = await act.Should().ThrowAsync<AxonStoreException>();
        exception.Which.Should().NotBeOfType<AxonSchemaNotProvisionedException>();
        exception.Which.InnerException.Should().BeOfType<SqlException>();
        exception.Which.Message.Should().Contain("Could not reach SQL Server");
    }

    [Fact]
    public async Task DatabaseWithoutSchema_ThrowsAxonSchemaNotProvisionedException()
    {
        // tempdb always exists on a SQL Server instance but was never provisioned with Axon's
        // schema (SqlServerFixture only runs Schema.sql against the database in
        // fixture.ConnectionString), so a query against it reliably reproduces "Invalid object
        // name" (error 208) without needing to spin up a second unprovisioned container.
        var builder = new SqlConnectionStringBuilder(fixture.ConnectionString) { InitialCatalog = "tempdb" };
        var sut = new AxonSqlServerInstanceStore(builder.ConnectionString);

        var act = () => sut.GetAll();

        var exception = await act.Should().ThrowAsync<AxonSchemaNotProvisionedException>();
        exception.Which.InnerException.Should().BeOfType<SqlException>();
        exception.Which.Message.Should().Contain("Schema.sql");
    }

    [Fact]
    public async Task UnreachableServer_DoesNotLeakCredentialsInMessage()
    {
        var unreachableConnectionString =
            "Server=127.0.0.1,1; Database=Axon; User Id=sa; Password=SuperSecretPassword123!; TrustServerCertificate=True; Connect Timeout=2;";
        var sut = new AxonSqlServerInstanceStore(unreachableConnectionString);

        var act = () => sut.GetAll();

        var exception = await act.Should().ThrowAsync<AxonStoreException>();
        exception.Which.Message.Should().NotContain("SuperSecretPassword123!");
    }
}
