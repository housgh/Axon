namespace Axon.Tests.Unit.InProcess;

/// <summary>
/// Tests that read or set the process-wide <c>JobActivator.Current</c> run in this collection so
/// they never run in parallel with each other.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class JobActivatorCollection
{
    public const string Name = "JobActivator";
}
