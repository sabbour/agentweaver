namespace Agentweaver.Cli.Tests;

// Keep CLI tests that change process-wide AGENTWEAVER_* variables out of parallel runs.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CliEnvironmentCollection
{
    public const string Name = "CLI process environment";
}
