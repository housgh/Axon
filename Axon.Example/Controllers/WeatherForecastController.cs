using Axon.Client.Services;
using Axon.Core.Models;
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

    [HttpGet("fail-test-custom-retry")]
    public async Task<IActionResult> FailTestCustomRetry()
    {
        var jobId = await axonClient.EnqueueAsync<MyClass>(x => x.AlwaysThrows(),
            retryPolicy: new AxonRetryPolicy { MaxAttempts = 2, RetryDelaysSeconds = [2] });
        return Ok(new { JobId = jobId });
    }

    [HttpGet("concurrency-limited")]
    public async Task<IActionResult> ConcurrencyLimited()
    {
        // At most 2 jobs sharing the "email-sender" key may be Processing across the whole
        // fleet at once - useful for throttling against a rate-limited downstream dependency,
        // for example. With only one client connected here, dispatch is already serialized by
        // there being a single worker, so this endpoint demonstrates the API rather than visibly
        // observable throttling (that needs multiple connected clients to see).
        var jobId = await axonClient.EnqueueAsync<MyClass>(x => x.WriteHelloWorld("Hello World"),
            concurrencyKey: "email-sender", maxConcurrent: 2);
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
