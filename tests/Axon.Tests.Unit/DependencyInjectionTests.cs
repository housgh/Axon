using Axon.Server.DependencyInjection;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;

namespace Axon.Tests.Unit;

public class DependencyInjectionTests
{
    [Fact]
    public async Task UseAxonServer_WithoutAuthentication_StartsAndServesRequests()
    {
        // Regression test: UseAxonServer() used to call app.UseAuthentication()/UseAuthorization()
        // unconditionally, which throws at startup ("Unable to resolve service for type
        // IAuthenticationSchemeProvider") unless .AddAuthentication(...) was also chained - even
        // though auth is documented as fully opt-in. This reproduces the documented "API only, no
        // auth" configuration and asserts the app actually starts and serves a request.
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAxonServer().AddAxonApiEndpoints();

        await using var app = builder.Build();
        app.UseAxonServer();
        await app.StartAsync();

        using var client = app.GetTestClient();
        var response = await client.GetAsync("/axon/jobs?skip=0&take=20");

        response.IsSuccessStatusCode.Should().BeTrue();
    }

    [Fact]
    public async Task UseAxonServer_WithAuthentication_StartsAndRedirectsUnauthenticatedDashboardRequests()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAxonServer()
            .AddAxonDashboard()
            .AddAuthentication(auth => auth.Users.Add(new Axon.Server.Services.DashboardUser
            {
                Username = "admin",
                Password = "password",
                Role = Axon.Server.Services.DashboardRole.Admin
            }));

        await using var app = builder.Build();
        app.UseAxonServer();
        await app.StartAsync();

        using var client = app.GetTestClient();
        var response = await client.GetAsync("/axon/dashboard");

        // The key assertion is that the app started at all (auth middleware wired up correctly);
        // an unauthenticated dashboard request should redirect to login rather than error out.
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Redirect);
    }
}
