using Axon.Client.DependencyInjection;
using Axon.Client.Services;
using Axon.Example.Controllers;
using Axon.Server.DependencyInjection;
using Microsoft.AspNetCore.SignalR.Client;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddAxonServer(auth =>
{
    auth.Password = builder.Configuration["Axon:DashboardPassword"]
        ?? throw new InvalidOperationException("Axon:DashboardPassword is not configured.");
});

// This example hosts the Axon client and server in the same process, so it points the client
// back at whichever address Kestrel is actually configured to bind for this run (the "urls"
// config Kestrel itself reads) rather than a hardcoded port that only matches one launch profile.
var urls = builder.Configuration["urls"]
    ?? throw new InvalidOperationException("No server URLs configured (ASPNETCORE_URLS / launch profile applicationUrl).");
var axonBaseUrl = urls.Split(';')[0];
builder.Services.AddAxonClient(axonBaseUrl);



var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.MapControllers();

app.UseAxonServer();

app.Lifetime.ApplicationStarted.Register(() =>
{
    _ = Task.Run(async () =>
    {
        var axonClient = app.Services.GetRequiredService<IAxonClient>();
        var hubConnection = app.Services.GetRequiredService<HubConnection>();

        while (hubConnection.State != HubConnectionState.Connected)
            await Task.Delay(200);

        await axonClient.AddOrUpdateRecurringAsync<MyClass>(
            "hourly-hello",
            "0 * * * *",
            x => x.WriteHelloWorld("Recurring tick"));
    });
});

app.Run();