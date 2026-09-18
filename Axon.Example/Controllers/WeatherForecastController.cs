using Axon.Client.Services;
using Microsoft.AspNetCore.Mvc;

namespace Axon.Example.Controllers;

[ApiController]
[Route("[controller]")]
public class WeatherForecastController(IAxonClient axonClient) : ControllerBase
{

    [HttpGet(Name = "GetWeatherForecast")]
    public async Task<IActionResult> Get()
    {
        var jobId = await axonClient.EnqueueAsync<MyClass>(x => x.WriteHelloWorld("Hello World"));
        return Ok(new {JobId = jobId});
    }

    [HttpGet("schedule")]
    public async Task<IActionResult> Schedule()
    {
        var jobId = await axonClient.ScheduleAsync<MyClass>(TimeSpan.FromMinutes(1), x => x.WriteHelloWorld("Delayed Hello"));
        return Ok(new {JobId = jobId});
    }

    [HttpGet("fail-test")]
    public async Task<IActionResult> FailTest()
    {
        var jobId = await axonClient.EnqueueAsync<MyClass>(x => x.AlwaysThrows());
        return Ok(new { JobId = jobId });
    }

}

public class MyClass
{
    public void WriteHelloWorld(string message)
    {
        Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] {message}");
    }

    public void AlwaysThrows()
    {
        throw new InvalidOperationException("boom");
    }
}
