extern alias agenthost;

using System.Diagnostics;
using System.Text.Json;
using Agentweaver.Domain;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

using AgentHostConfigurationException = agenthost::Agentweaver.AgentHost.AgentHostConfigurationException;
using AgentHostOptions = agenthost::Agentweaver.AgentHost.AgentHostOptions;
using AgentHostRunConfiguration = agenthost::Agentweaver.AgentHost.AgentHostRunConfiguration;
using AssemblyBuildCheckoutPreparer = agenthost::Agentweaver.AgentHost.AssemblyBuildCheckoutPreparer;
using ConfigureRequest = agenthost::ConfigureRequest;

namespace Agentweaver.Tests.AgentHost;

public sealed class AssemblyBuildCheckoutPreparerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        AppContext.BaseDirectory,
        ".assembly-checkout-tests",
        Guid.NewGuid().ToString("n"));

    public AssemblyBuildCheckoutPreparerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ConfigureRequest_defaults_to_existing_behavior_when_purpose_is_omitted()
    {
        var request = JsonSerializer.Deserialize<ConfigureRequest>(
            """{"runId":"run-1"}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        request.Should().NotBeNull();
        request!.RunId.Should().Be("run-1");
        request.Purpose.Should().Be(AgentHostPurpose.Default);
        request.ToRunConfiguration().Purpose.Should().Be(AgentHostPurpose.Default);
    }

    [Fact]
    public void ConfigureRequest_parses_explicit_assembly_source_contract()
    {
        var request = JsonSerializer.Deserialize<ConfigureRequest>(
            """
            {
              "runId": "run-1",
              "purpose": "AssemblyBuildTest",
              "sourceRepositoryPath": "/workspace/repo",
              "integrationRef": "agentweaver/integration/run-1",
              "commitSha": "1111111111111111111111111111111111111111",
              "expectedTreeHash": "2222222222222222222222222222222222222222",
              "localExecutionPath": "/local-workspace/abc/2222222222222222222222222222222222222222"
            }
            """,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        request.Should().NotBeNull();
        var configuration = request!.ToRunConfiguration();
        configuration.Purpose.Should().Be(AgentHostPurpose.AssemblyBuildTest);
        configuration.IntegrationRef.Should().Be("agentweaver/integration/run-1");
        configuration.CommitSha.Should().Be("1111111111111111111111111111111111111111");
        configuration.ExpectedTreeHash.Should().Be("2222222222222222222222222222222222222222");
    }

    [Fact]
    public async Task PrepareAsync_rejects_commit_mismatch_with_typed_reason()
    {
        var repository = CreateRepository();
        var treeHash = Git(repository, "rev-parse", "integration^{tree}");
        var configuration = Configuration(
            repository,
            commitSha: new string('0', 40),
            expectedTreeHash: treeHash);

        var act = () => Preparer().PrepareAsync(configuration, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<AgentHostConfigurationException>();
        exception.Which.Reason.Should().Be("assembly_checkout_commit_mismatch");
    }

    [Fact]
    public async Task PrepareAsync_checks_out_verified_commit_detached_on_build_scratch()
    {
        var repository = CreateRepository();
        var commitSha = Git(repository, "rev-parse", "integration");
        var treeHash = Git(repository, "rev-parse", "integration^{tree}");
        var configuration = Configuration(repository, commitSha, treeHash);
        var cacheVariables = new[]
        {
            "npm_config_cache",
            "YARN_CACHE_FOLDER",
            "PNPM_HOME",
            "PNPM_STORE_DIR",
            "npm_config_store_dir",
            "XDG_CACHE_HOME",
        };
        var originalValues = cacheVariables.ToDictionary(
            name => name,
            Environment.GetEnvironmentVariable);

        try
        {
            var checkout = await Preparer().PrepareAsync(configuration, CancellationToken.None);

            Git(checkout, "rev-parse", "HEAD").Should().Be(commitSha);
            Git(checkout, "rev-parse", "HEAD^{tree}").Should().Be(treeHash);
            Git(checkout, "branch", "--show-current").Should().BeEmpty("checkout must be detached");
            File.Exists(Path.Combine(checkout, "package.json")).Should().BeTrue();
            Environment.GetEnvironmentVariable("npm_config_cache").Should().StartWith(
                Directory.GetParent(checkout)!.FullName);
        }
        finally
        {
            foreach (var (name, value) in originalValues)
                Environment.SetEnvironmentVariable(name, value);
        }
    }

    [Fact]
    public async Task PrepareAsync_rejects_tree_mismatch_with_typed_reason()
    {
        var repository = CreateRepository();
        var commitSha = Git(repository, "rev-parse", "integration");
        var wrongTree = new string('f', 40);
        var configuration = Configuration(repository, commitSha, wrongTree);

        var act = () => Preparer().PrepareAsync(configuration, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<AgentHostConfigurationException>();
        exception.Which.Reason.Should().Be("assembly_checkout_tree_mismatch");
    }

    private AssemblyBuildCheckoutPreparer Preparer() =>
        new(
            Options.Create(new AgentHostOptions
            {
                BuildScratchRoot = Path.Combine(_root, "scratch"),
                BuildScratchMinimumFreeBytes = 0,
            }),
            NullLogger<AssemblyBuildCheckoutPreparer>.Instance);

    private AgentHostRunConfiguration Configuration(
        string repository,
        string commitSha,
        string expectedTreeHash)
    {
        const string runId = "assembly-run";
        var scratchRoot = Path.Combine(_root, "scratch");
        return new AgentHostRunConfiguration(
            runId,
            UserId: "owner",
            TurnBearerToken: "token",
            KvUserSecretName: null,
            GitHubAccessToken: null,
            PreviewRunnerCredential: null,
            WorkingDirectory: repository,
            Purpose: AgentHostPurpose.AssemblyBuildTest,
            SourceRepositoryPath: repository,
            IntegrationRef: "integration",
            CommitSha: commitSha,
            ExpectedTreeHash: expectedTreeHash,
            LocalExecutionPath: AssemblyBuildTestExecution.GetCheckoutPath(
                scratchRoot,
                runId,
                expectedTreeHash));
    }

    private string CreateRepository()
    {
        var repository = Path.Combine(_root, "source-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(repository);
        Git(repository, "init");
        Git(repository, "config", "user.email", "tests@example.invalid");
        Git(repository, "config", "user.name", "Agentweaver Tests");
        File.WriteAllText(Path.Combine(repository, "package.json"), """{"scripts":{"build":"echo ok"}}""");
        Git(repository, "add", "package.json");
        Git(repository, "commit", "-m", "fixture");
        Git(repository, "branch", "integration");
        return repository;
    }

    private static string Git(string workingDirectory, params string[] args)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.Should().Be(0, $"git {string.Join(' ', args)} failed: {stderr}");
        return stdout.Trim();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
