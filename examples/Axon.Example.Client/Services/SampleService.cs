namespace Axon.Example.Client.Services;

public interface ISampleService
{
    Task RunAsync();
}

public class SampleService : ISampleService
{
    public Task RunAsync()
    {
        Console.WriteLine("Running Sample Service!");
        return Task.CompletedTask;
    }
}