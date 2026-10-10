using System.Net;
using Axon.Client.DependencyInjection;
using Axon.Client.Services;
using Axon.Core.Enums;
using Axon.Server.DependencyInjection;
using Axon.Server.Interfaces;
using Axon.Server.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Axon.Tests.Unit.InProcess;

[Collection(JobActivatorCollection.Name)]
public class InProcessClientDependencyInjectionTests
{
    public class Greeter
    {
        public string Prefix => "Hello";
    }

    public class GreetingJob(Greeter greeter)
    {
        public static string? LastGreeting;
        public void Greet(string name) => LastGreeting = $"{greeter.Prefix} {name}";
    }

    private static async Task<WebApplication> StartMonolithAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<Greeter>();
        builder.Services.AddAxonServer()
            .AddAxonApiEndpoints()
            .AddInProcessClient(o => o.DeviceName = "monolith-test");

        var app = builder.Build();
        app.UseAxonServer();
        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task AddInProcessClient_EnqueuedJob_RunsInProcessAndSucceeds()
    {
        GreetingJob.LastGreeting = null;
        await using var app = await StartMonolithAsync();
        var client = app.Services.GetRequiredService<IAxonClient>();
        var jobStore = app.Services.GetRequiredService<IAxonJobStore>();

        var jobId = await client.EnqueueAsync<GreetingJob>(x => x.Greet("Axon"));

        // AxonJobProcessor polls every 5s, so allow a couple of cycles.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while ((await jobStore.GetJob(jobId))?.State != JobState.Succeeded && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        (await jobStore.GetJob(jobId))!.State.Should().Be(JobState.Succeeded);
        GreetingJob.LastGreeting.Should().Be("Hello Axon");
    }

    [Fact]
    public async Task AddInProcessClient_DoesNotMapJobHub_AndRegistersLocalDevice()
    {
        await using var app = await StartMonolithAsync();
        using var http = app.GetTestClient();

        var negotiate = await http.PostAsync("/hubs/axon/negotiate?negotiateVersion=1", null);
        var devices = await app.Services.GetRequiredService<IDeviceConnectionRegistry>().GetAll();

        negotiate.StatusCode.Should().Be(HttpStatusCode.NotFound);
        devices.Should().ContainSingle(d => d.DeviceName == "monolith-test");
    }

    [Fact]
    public async Task WithoutInProcessClient_JobHubIsStillMapped()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAxonServer();
        await using var app = builder.Build();
        app.UseAxonServer();
        await app.StartAsync();
        using var http = app.GetTestClient();

        var negotiate = await http.PostAsync("/hubs/axon/negotiate?negotiateVersion=1", null);

        negotiate.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public void AddInProcessClient_ReplacesSignalRDispatcher()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAxonServer().AddInProcessClient();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IAxonJobDispatcher>().Should().BeOfType<InProcessJobDispatcher>();
        provider.GetRequiredService<IAxonClient>().Should().BeOfType<InProcessAxonClient>();
    }

    [Fact]
    public void AddInProcessClient_AfterAddAxonClient_Throws()
    {
        var services = new ServiceCollection();
        services.AddAxonClient("https://localhost:5001");

        var act = () => services.AddAxonServer().AddInProcessClient();

        act.Should().Throw<InvalidOperationException>().WithMessage("*AddAxonClient*");
    }

    [Fact]
    public void AddAxonClient_AfterAddInProcessClient_Throws()
    {
        var services = new ServiceCollection();
        services.AddAxonServer().AddInProcessClient();

        var act = () => services.AddAxonClient("https://localhost:5001");

        act.Should().Throw<InvalidOperationException>().WithMessage("*AddInProcessClient*");
    }

    [Fact]
    public void AddInProcessClient_NonPositiveMaxConcurrentJobs_Throws()
    {
        var act = () => new ServiceCollection().AddAxonServer().AddInProcessClient(o => o.MaxConcurrentJobs = 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
