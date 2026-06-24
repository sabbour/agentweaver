using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.Api.Workflows;
using Agentweaver.Api.Auth;
using Agentweaver.Domain;
using Agentweaver.Squad.Catalog;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Workflows;

/// <summary>
/// Tests for the LLM workflow generator (Feature 015 US10, FR-056–FR-061). Unit tests exercise
/// <see cref="CopilotWorkflowGenerator"/> against a scripted <see cref="IAgentRunner"/> so the
/// prompt → validate → correction-pass pipeline runs without the live model; an integration-style test
/// drives the generate endpoint through a stub generator. Validation reuses the real
/// <see cref="WorkflowDefinitionLoader"/> (Principle VII).
/// </summary>
public sealed class WorkflowGeneratorTests
{
    private const string ValidWorkflowYaml = """
        id: generated-flow
        name: Generated Flow
        description: A generated workflow for tests.
        version: "1.0"
        trigger:
          type: manual
        start: agent
        nodes:
          - id: agent
            type: prompt
            label: Agent
          - id: done
            type: terminal
            label: Done
        edges:
          - from: agent
            to: done
        """;

    // YAML that parses but fails schema validation (no trigger/start/nodes) → drives a correction pass.
    private const string InvalidWorkflowYaml = "name: Broken Workflow\n";

    private static CopilotWorkflowGenerator CreateGenerator(IAgentRunner runner)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Providers:GitHubCopilot:Model"] = "gpt-4o",
            })
            .Build();
        return new CopilotWorkflowGenerator(runner, new CatalogReader(), config, NullLogger<CopilotWorkflowGenerator>.Instance);
    }

    [Fact]
    public async Task ValidResponse_ReturnsParsedWorkflow_NotCorrected()
    {
        var runner = new ScriptedAgentRunner(ValidWorkflowYaml);
        var generator = CreateGenerator(runner);

        var result = await generator.GenerateAsync(new WorkflowGenerationRequest("A simple manual workflow."));

        result.WasCorrected.Should().BeFalse();
        result.Workflow.Id.Should().Be("generated-flow");
        result.Workflow.Nodes.Should().HaveCount(2);
        result.GeneratedYaml.Should().Contain("id: generated-flow");
        runner.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task ValidResponseWithMarkdownFences_IsCleanedAndParsed()
    {
        var fenced = "```yaml\n" + ValidWorkflowYaml + "\n```";
        var runner = new ScriptedAgentRunner(fenced);
        var generator = CreateGenerator(runner);

        var result = await generator.GenerateAsync(new WorkflowGenerationRequest("A simple manual workflow."));

        result.WasCorrected.Should().BeFalse();
        result.GeneratedYaml.Should().NotContain("```");
        result.Workflow.Id.Should().Be("generated-flow");
    }

    [Fact]
    public async Task InvalidThenValid_TriggersCorrectionPass_ReturnsCorrected()
    {
        var runner = new ScriptedAgentRunner(InvalidWorkflowYaml, ValidWorkflowYaml);
        var generator = CreateGenerator(runner);

        var result = await generator.GenerateAsync(new WorkflowGenerationRequest("A workflow that needs fixing."));

        result.WasCorrected.Should().BeTrue();
        result.Workflow.Id.Should().Be("generated-flow");
        runner.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task BothPassesInvalid_ThrowsWorkflowGenerationException()
    {
        var runner = new ScriptedAgentRunner(InvalidWorkflowYaml, InvalidWorkflowYaml);
        var generator = CreateGenerator(runner);

        var act = () => generator.GenerateAsync(new WorkflowGenerationRequest("An unfixable description."));

        await act.Should().ThrowAsync<WorkflowGenerationException>();
        runner.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task MissingId_IsDerivedFromDescriptionSlug()
    {
        // Same valid workflow body but without an `id:` line; the generator injects a slug from the
        // description (FR — id generation).
        var noId = """
            name: No Id Flow
            description: A workflow with no id.
            version: "1.0"
            trigger:
              type: manual
            start: agent
            nodes:
              - id: agent
                type: prompt
                label: Agent
              - id: done
                type: terminal
                label: Done
            edges:
              - from: agent
                to: done
            """;
        var runner = new ScriptedAgentRunner(noId);
        var generator = CreateGenerator(runner);

        var result = await generator.GenerateAsync(new WorkflowGenerationRequest("Review and Merge PRs"));

        result.Workflow.Id.Should().Be("review-and-merge-prs");
    }

    [Fact]
    public async Task EmptyDescription_Throws()
    {
        var generator = CreateGenerator(new ScriptedAgentRunner(ValidWorkflowYaml));
        var act = () => generator.GenerateAsync(new WorkflowGenerationRequest("   "));
        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ── Endpoint integration (stub generator) ────────────────────────────────────────────────────

    [Fact]
    public async Task GenerateEndpoint_Returns200_WithYamlAndWorkflowId()
    {
        await using var factory = new StubWorkflowGeneratorFactory();
        var client = factory.CreateAuthenticatedClient();

        var dir = factory.NewWorkingDirectory();
        var create = await client.PostAsJsonAsync("/api/projects", new
        {
            name = $"WfGen Test {Guid.NewGuid():N}",
            origin = "blank",
            working_directory = dir,
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var projectId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("project_id").GetString()!;

        var resp = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/workflows/generate",
            new { description = "A manual review-and-merge workflow." });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("yaml").GetString().Should().Contain("id: generated-flow");
        body.GetProperty("workflowId").GetString().Should().Be("generated-flow");
        body.GetProperty("wasCorrected").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task GenerateEndpoint_MissingDescription_Returns400()
    {
        await using var factory = new StubWorkflowGeneratorFactory();
        var client = factory.CreateAuthenticatedClient();

        var dir = factory.NewWorkingDirectory();
        var create = await client.PostAsJsonAsync("/api/projects", new
        {
            name = $"WfGen Test {Guid.NewGuid():N}",
            origin = "blank",
            working_directory = dir,
        });
        var projectId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("project_id").GetString()!;

        var resp = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/workflows/generate", new { description = "" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>Scripted <see cref="IAgentRunner"/>: returns a queued response per call so the
    /// generator's first/correction passes are deterministic.</summary>
    private sealed class ScriptedAgentRunner : IAgentRunner
    {
        private readonly Queue<string> _responses;
        public int CallCount { get; private set; }

        public ScriptedAgentRunner(params string[] responses) => _responses = new Queue<string>(responses);

        public Task<string> ExecuteAsync(
            string task, string workingDirectory, string repositoryPath, ModelSource modelSource,
            string runId, string? modelId, ChannelWriter<RunEvent>? stream, CancellationToken ct,
            string? systemPromptContext = null)
        {
            CallCount++;
            var next = _responses.Count > 0 ? _responses.Dequeue() : string.Empty;
            return Task.FromResult(next);
        }
    }

    /// <summary>Stub <see cref="IWorkflowGenerator"/>: returns a fixed valid draft so the endpoint's
    /// HTTP/auth/serialization path is exercised without the model.</summary>
    private sealed class StubWorkflowGenerator : IWorkflowGenerator
    {
        public Task<WorkflowGenerationResult> GenerateAsync(WorkflowGenerationRequest request, CancellationToken ct = default)
        {
            var loaded = WorkflowDefinitionLoader.Load(ValidWorkflowYaml, "stub");
            return Task.FromResult(new WorkflowGenerationResult(loaded.Definition!, ValidWorkflowYaml, WasCorrected: false));
        }
    }

    /// <summary>Project test factory that swaps in the stub generator for the generate endpoint test.</summary>
    private sealed class StubWorkflowGeneratorFactory : Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>
    {
        public const string TestApiKey = "wfgen-test-api-key-77001";
        public const string TestUser = "wfgen-test-user";

        private readonly string _dbPath;
        private readonly string _workspaceRoot;
        private readonly string _worktreesPath;
        private readonly string _checkpointsPath;
        private readonly string _coordinatorCheckpointsPath;

        public InMemoryGitHubTokenStore TokenStore { get; } = new();

        public StubWorkflowGeneratorFactory()
        {
            var unique = Guid.NewGuid().ToString("N");
            _dbPath = Path.Combine(Path.GetTempPath(), $"agentweaver-wfgen-{unique}.db");
            _workspaceRoot = Path.Combine(Path.GetTempPath(), $"agentweaver-wfgen-ws-{unique}");
            _worktreesPath = Path.Combine(Path.GetTempPath(), $"agentweaver-wfgen-wt-{unique}");
            _checkpointsPath = Path.Combine(Path.GetTempPath(), $"agentweaver-wfgen-cp-{unique}");
            _coordinatorCheckpointsPath = Path.Combine(Path.GetTempPath(), $"agentweaver-wfgen-ccp-{unique}");
            Directory.CreateDirectory(_workspaceRoot);
        }

        public HttpClient CreateAuthenticatedClient()
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TestApiKey);
            return client;
        }

        public string NewWorkingDirectory()
        {
            var dir = Path.Combine(_workspaceRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = _dbPath,
                    ["Worktrees:BasePath"] = _worktreesPath,
                    ["Checkpoints:Path"] = _checkpointsPath,
                    ["Coordinator:Checkpoints:Path"] = _coordinatorCheckpointsPath,
                    ["Auth:ApiKey"] = TestApiKey,
                    ["Auth:User"] = TestUser,
                    ["Auth:GitHub:ClientId"] = "test-github-client-id",
                    ["Auth:GitHub:BaseUrl"] = "https://github.com",
                    ["Git:Author:Name"] = "Test",
                    ["Git:Author:Email"] = "test@localhost",
                    ["Providers:GitHubCopilot:ApiKey"] = "test-copilot-key",
                    ["Providers:GitHubCopilot:Endpoint"] = "https://api.githubcopilot.com",
                    ["Providers:GitHubCopilot:Model"] = "gpt-4o",
                    ["Providers:MicrosoftFoundry:ApiKey"] = "test-foundry-key",
                    ["Providers:MicrosoftFoundry:Endpoint"] = "https://test.openai.azure.com",
                    ["Providers:MicrosoftFoundry:Deployment"] = "gpt-4o",
                    ["RunBounds:MaxSteps"] = "50",
                    ["RunBounds:MaxMinutes"] = "10",
                });
            });

            builder.ConfigureServices(services =>
            {
                Remove<Agentweaver.Domain.IGitHubTokenStore>(services);
                services.AddSingleton<Agentweaver.Domain.IGitHubTokenStore>(TokenStore);

                Remove<Agentweaver.Api.Git.ProjectGitInitializer>(services);
                services.AddSingleton<Agentweaver.Api.Git.ProjectGitInitializer, NoOpProjectGitInitializer>();

                Remove<IWorkflowGenerator>(services);
                services.AddScoped<IWorkflowGenerator, StubWorkflowGenerator>();
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (!disposing) return;
            foreach (var p in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            {
                try { File.Delete(p); } catch { /* best effort */ }
            }
            foreach (var dir in new[] { _workspaceRoot, _worktreesPath, _checkpointsPath, _coordinatorCheckpointsPath })
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
            }
        }

        private static void Remove<T>(IServiceCollection services)
        {
            var d = services.FirstOrDefault(x => x.ServiceType == typeof(T));
            if (d is not null) services.Remove(d);
        }
    }
}
