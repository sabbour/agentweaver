using Agentweaver.Cli;

namespace Agentweaver.Cli.Tests;

public sealed class CliApplicationTests
{
    [Fact]
    public async Task HelpAndVersionWorkWithoutTargetConfiguration()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var helpCode = await CliApplication.RunAsync(
            ["--help"],
            new CliRuntimeOptions(Output: output, Error: error));
        var versionCode = await CliApplication.RunAsync(
            ["--version"],
            new CliRuntimeOptions(Output: output, Error: error));

        Assert.Equal(0, helpCode);
        Assert.Equal(0, versionCode);
        Assert.Contains("agentweaver api operations", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Agentweaver 1.x CLI 1.0.0 (source unbound, tree unbound)", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("1.0.0+", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("source unbound", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("tree unbound", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task TestCommandPassesExplicitInteractivePolicyAndPreservesRunnerExitCode()
    {
        var runner = new RecordingRunner(2);
        var output = new StringWriter();
        var result = await CliApplication.RunAsync(
            ["test", "--input", "manifest.json", "--output", "evidence.jsonl", "--dry-run", "--interactive"],
            new CliRuntimeOptions(AcceptanceRunner: runner, Output: output));

        Assert.Equal(2, result);
        Assert.NotNull(runner.Options);
        Assert.True(runner.Options!.DryRun);
        Assert.Equal(InteractionPolicy.Allow, runner.Options.Interaction);
        Assert.Equal("manifest.json", runner.Options.InputPath);
        Assert.Equal("evidence.jsonl", runner.Options.OutputPath);
    }

    [Fact]
    public async Task TestCommandDefaultsToNoninteractiveAuthentication()
    {
        var runner = new RecordingRunner(0);
        var result = await CliApplication.RunAsync(
            ["test", "--input", "manifest.json", "--output", "evidence.jsonl"],
            new CliRuntimeOptions(AcceptanceRunner: runner));

        Assert.Equal(0, result);
        Assert.Equal(InteractionPolicy.Never, runner.Options?.Interaction);
    }

    [Fact]
    public async Task TestCommandRejectsOutputThatWouldOverwriteManifestOrProof()
    {
        var runner = new RecordingRunner(0);
        var output = new StringWriter();
        var error = new StringWriter();
        var path = Path.GetFullPath("same.json");

        var result = await CliApplication.RunAsync(
            ["test", "--input", path, "--output", path],
            new CliRuntimeOptions(AcceptanceRunner: runner, Output: output, Error: error));

        Assert.Equal(1, result);
        Assert.Null(runner.Options);
        Assert.Contains("cannot overwrite", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TargetFlagsOverrideEnvironmentForAuthentication()
    {
        var names = new[]
        {
            "AGENTWEAVER_GATEWAY_URL",
            "AGENTWEAVER_BROKER_ISSUER",
            "AGENTWEAVER_CLIENT_ID",
            "AGENTWEAVER_API_AUDIENCE",
            "AGENTWEAVER_TENANT_ID",
            "AGENTWEAVER_PROJECT_ID",
            "AGENTWEAVER_RUN_ID",
        };
        var previous = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var authentication = new RecordingAuthentication();
        try
        {
            Environment.SetEnvironmentVariable("AGENTWEAVER_GATEWAY_URL", "https://env.gateway.example/");
            Environment.SetEnvironmentVariable("AGENTWEAVER_BROKER_ISSUER", "https://env.identity.example/");
            Environment.SetEnvironmentVariable("AGENTWEAVER_CLIENT_ID", "env-client");
            Environment.SetEnvironmentVariable("AGENTWEAVER_API_AUDIENCE", "https://env.api.example");
            Environment.SetEnvironmentVariable("AGENTWEAVER_TENANT_ID", "env-tenant");
            Environment.SetEnvironmentVariable("AGENTWEAVER_PROJECT_ID", "env-project");
            Environment.SetEnvironmentVariable("AGENTWEAVER_RUN_ID", "env-run");

            var result = await CliApplication.RunAsync(
            [
                "auth", "status",
                "--gateway-url", "https://flag.gateway.example/",
                "--broker-issuer", "https://flag.identity.example/",
                "--client-id", "flag-client",
                "--api-audience", "https://flag.api.example",
                "--tenant-id", "flag-tenant",
                "--project-id", "flag-project",
                "--run-id", "flag-run",
                "--scope", "projects.orchestrator",
            ],
            new CliRuntimeOptions(Authentication: authentication));

            Assert.Equal(0, result);
            Assert.NotNull(authentication.Identity);
            Assert.Equal("https://flag.gateway.example/", authentication.Identity!.Target.Gateway.AbsoluteUri);
            Assert.Equal("https://flag.identity.example/", authentication.Identity.Target.Issuer.AbsoluteUri);
            Assert.Equal("flag-client", authentication.Identity.Target.ClientId);
            Assert.Equal("https://flag.api.example", authentication.Identity.Target.Audience);
            Assert.Equal("flag-tenant", authentication.Identity.Target.TenantId);
            Assert.Equal("flag-project", authentication.Identity.ProjectId);
            Assert.Equal("flag-run", authentication.Identity.RunId);
        }
        finally
        {
            foreach (var (name, value) in previous)
                Environment.SetEnvironmentVariable(name, value);
        }
    }

    [Fact]
    public async Task TargetOptionsRejectUnknownAndDuplicateValues()
    {
        var authentication = new RecordingAuthentication();
        var error = new StringWriter();
        var unknown = await CliApplication.RunAsync(
            ["auth", "status", "--unknown-target", "value"],
            new CliRuntimeOptions(Authentication: authentication, Error: error));
        Assert.Equal(1, unknown);
        Assert.Contains("Unknown option", error.ToString(), StringComparison.Ordinal);
        Assert.Null(authentication.Identity);

        error.GetStringBuilder().Clear();
        var duplicate = await CliApplication.RunAsync(
            ["auth", "status", "--gateway-url", "https://one.example/", "--gateway-url", "https://two.example/"],
            new CliRuntimeOptions(Authentication: authentication, Error: error));
        Assert.Equal(1, duplicate);
        Assert.Contains("only once", error.ToString(), StringComparison.Ordinal);
        Assert.Null(authentication.Identity);
    }

    private sealed class RecordingRunner(int exitCode) : IApiAcceptanceRunner
    {
        public ApiTestOptions? Options { get; private set; }

        public Task<int> RunAsync(ApiTestOptions options, CancellationToken ct)
        {
            Options = options;
            return Task.FromResult(exitCode);
        }
    }

    private sealed class RecordingAuthentication : ICliAuthentication
    {
        public CliIdentity? Identity { get; private set; }

        public Task<CliAuthStatus> LoginAsync(CliIdentity identity, CancellationToken ct) =>
            Task.FromResult(Record(identity));

        public Task<CliAuthStatus> StatusAsync(CliIdentity identity, CancellationToken ct) =>
            Task.FromResult(Record(identity));

        public Task LogoutAsync(CliIdentity identity, CancellationToken ct)
        {
            Identity = identity;
            return Task.CompletedTask;
        }

        public Task<string> GetBearerAsync(CliIdentity identity, InteractionPolicy interaction, CancellationToken ct) =>
            throw new NotSupportedException();

        private CliAuthStatus Record(CliIdentity identity)
        {
            Identity = identity;
            return new CliAuthStatus(true, "actor-1", identity);
        }
    }
}
