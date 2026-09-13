namespace Axon.Core.Models;

public class JobInfo
{
    public List<object?> Arguments { get; set; } = [];
    public string MethodName { get; set; } = null!;
    public string Assembly { get; set; } = null!;
    public string DeclaringType { get; set; } = null!;
}