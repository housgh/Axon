namespace Axon.Core.Models;

public class JobInfo
{
    public List<object?> Arguments { get; set; } = [];
    public string MethodName { get; set; } = null!;
    public string Assembly { get; set; } = null!;
    public string DeclaringType { get; set; } = null!;

    /// <summary>Overrides Axon.Server's default retry behavior for this job. Null uses the default.</summary>
    public AxonRetryPolicy? RetryPolicy { get; set; }
}