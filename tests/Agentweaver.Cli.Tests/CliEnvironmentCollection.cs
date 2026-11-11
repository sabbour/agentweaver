namespace Agentweaver.Cli.Tests;

// Tests that set or depend on process-wide AGENTWEAVER_* variables must not run beside each other or other tests.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CliEnvironmentCollection
{
    public const string Name = "CLI process environment";
}
