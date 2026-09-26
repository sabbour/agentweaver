using System.Net;
using System.Text.Json;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Workflows;
using FluentAssertions;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.OpenApi;

public sealed class OpenApiEndpointsTests : IDisposable
{
    private readonly AgentweaverWebApplicationFactory _factory = new();
    private readonly HttpClient _client;

    public OpenApiEndpointsTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task OpenApiJson_IncludesNamedTaggedDocumentedOperations_AndBearerSecurity()
    {
        var response = await _client.GetAsync("/openapi/v1.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        root.GetProperty("openapi").GetString().Should().StartWith("3.1");
        var paths = root.GetProperty("paths");

        var createProject = paths.GetProperty("/api/projects").GetProperty("post");
        createProject.GetProperty("operationId").GetString().Should().Be("CreateProject");
        createProject.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).Should().Contain("Projects");
        createProject.GetProperty("summary").GetString().Should().Contain("Creates a project workspace");
        if (createProject.TryGetProperty("description", out var createProjectDescription))
        {
            createProjectDescription.GetString().Should().NotBeNullOrWhiteSpace();
        }
        var createProjectSchema = root.GetProperty("components").GetProperty("schemas")
            .GetProperty("CreateProjectRequest").GetProperty("properties");
        createProjectSchema.TryGetProperty("repository_selection_code", out _).Should().BeTrue();
        createProjectSchema.TryGetProperty("source_repository", out _).Should().BeFalse();

        var startOrchestration = paths.GetProperty("/api/projects/{id}/orchestrations").GetProperty("post");
        startOrchestration.GetProperty("operationId").GetString().Should().Be("StartProjectOrchestration");
        startOrchestration.GetProperty("security").GetArrayLength().Should().BeGreaterThan(0);
        startOrchestration.GetProperty("parameters").EnumerateArray()
            .Should().Contain(parameter =>
                parameter.GetProperty("name").GetString() == "If-Model-Provider-Key"
                && parameter.GetProperty("in").GetString() == "header"
                && parameter.GetProperty("required").GetBoolean());
        var startSchema = root.GetProperty("components").GetProperty("schemas")
            .GetProperty("StartOrchestrationRequest");
        var startProperties = startSchema.GetProperty("properties");
        startProperties.TryGetProperty("auto_approve_tools", out _).Should().BeTrue();
        startProperties.TryGetProperty("autopilot", out _).Should().BeTrue();
        if (startSchema.TryGetProperty("required", out var startRequired))
        {
            var requiredNames = startRequired.EnumerateArray().Select(value => value.GetString()).ToArray();
            requiredNames.Should().NotContain("auto_approve_tools");
            requiredNames.Should().NotContain("autopilot");
        }

        var executionContext = paths.GetProperty("/api/ai/execution-context").GetProperty("post");
        executionContext.GetProperty("operationId").GetString().Should().Be("ResolveAiExecutionContext");
        executionContext.GetProperty("requestBody").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString()
            .Should().EndWith("/AiExecutionContextRequest");
        executionContext.GetProperty("responses").GetProperty("200").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString()
            .Should().EndWith("/AiExecutionContextResponse");
        executionContext.GetProperty("responses").GetProperty("409").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString()
            .Should().EndWith("/AiExecutionContextErrorResponse");
        executionContext.GetProperty("description").GetString().Should().Contain("If-Model-Provider-Key");
        var executionContextOperation = root.GetProperty("components").GetProperty("schemas")
            .GetProperty("AiExecutionContextRequest").GetProperty("properties").GetProperty("operation");
        executionContextOperation.GetProperty("enum").EnumerateArray().Select(value => value.GetString())
            .Should().BeEquivalentTo(AiOperationCatalog.Names);
        executionContextOperation.GetProperty("description").GetString()
            .Should().Contain("guarded endpoint");
        AiOperationCatalog.Names.Should().Contain("orchestration");

        var generateBlueprint = paths.GetProperty("/api/blueprints/generate").GetProperty("post");
        generateBlueprint.GetProperty("parameters").EnumerateArray()
            .Should().Contain(parameter =>
                parameter.GetProperty("name").GetString() == "Idempotency-Key"
                && parameter.GetProperty("in").GetString() == "header"
                && parameter.GetProperty("required").GetBoolean());
        generateBlueprint.GetProperty("responses").GetProperty("202").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString()
            .Should().EndWith("/BlueprintGenerationJobResponse");
        paths.TryGetProperty("/api/blueprints/generation-jobs/{jobId}", out _).Should().BeTrue();
        var generationResult = paths.GetProperty("/api/blueprints/generation-jobs/{jobId}/result")
            .GetProperty("get");
        generationResult.GetProperty("responses").GetProperty("200").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString()
            .Should().EndWith("/BlueprintGenerationResultResponse");
        paths.TryGetProperty("/api/blueprints/generation-jobs/{jobId}/cancel", out _).Should().BeTrue();
        paths.TryGetProperty("/api/blueprints/generation-jobs/{jobId}/retry", out _).Should().BeTrue();

        var outcomeSpec = paths.GetProperty("/api/runs/{id}/outcome-spec").GetProperty("get");
        outcomeSpec.GetProperty("operationId").GetString().Should().Be("GetCoordinatorOutcomeSpec");
        outcomeSpec.GetProperty("summary").GetString().Should().Contain("Returns the coordinator's current drafted outcome spec");

        var grammarOperation = paths.GetProperty("/api/workflows/grammar").GetProperty("get");
        grammarOperation.GetProperty("operationId").GetString().Should().Be("GetWorkflowGrammar");
        grammarOperation.GetProperty("responses").GetProperty("200").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString()
            .Should().EndWith("/WorkflowGrammarDto");

        var runEvents = paths.GetProperty("/api/runs/{id}/events").GetProperty("get");
        var eventParameters = runEvents.GetProperty("parameters").EnumerateArray()
            .Where(parameter => parameter.GetProperty("in").GetString() == "query")
            .ToDictionary(parameter => parameter.GetProperty("name").GetString()!, parameter => parameter);
        eventParameters.Keys.Should().BeEquivalentTo("type", "after", "limit");
        eventParameters["type"].GetProperty("schema").GetProperty("type").GetString().Should().Be("string");
        eventParameters["type"].GetProperty("schema").GetProperty("minLength").GetInt32().Should().Be(1);
        eventParameters["type"].GetProperty("schema").GetProperty("maxLength").GetInt32().Should().Be(128);
        eventParameters["after"].GetProperty("schema").GetProperty("type").GetString().Should().Be("integer");
        eventParameters["after"].GetProperty("schema").GetProperty("minimum").GetInt32().Should().Be(0);
        eventParameters["limit"].GetProperty("schema").GetProperty("minimum").GetInt32().Should().Be(1);
        eventParameters["limit"].GetProperty("schema").GetProperty("maximum").GetInt32().Should().Be(1000);

        var securitySchemes = root.GetProperty("components").GetProperty("securitySchemes");
        var bearer = securitySchemes.GetProperty("Bearer");
        bearer.GetProperty("type").GetString().Should().Be("http");
        bearer.GetProperty("scheme").GetString().Should().Be("bearer");
        bearer.GetProperty("description").GetString().Should().Contain("Authorization: Bearer");
    }

    [Fact]
    public async Task WorkflowGrammar_MatchesRuntimeValidationAndBindability()
    {
        var response = await _client.GetAsync("/api/workflows/grammar");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var grammar = document.RootElement;
        grammar.GetProperty("grammar_version").GetString().Should().Be(WorkflowGrammarContract.Version);
        grammar.GetProperty("node_types").EnumerateArray()
            .Select(node => node.GetProperty("yaml_type").GetString())
            .Should().Equal(WorkflowGrammarContract.NodeTypes.Select(node => node.YamlType));
        var nodeTypes = grammar.GetProperty("node_types").EnumerateArray().ToArray();
        nodeTypes.Single(node => node.GetProperty("yaml_type").GetString() == "fan_out")
            .GetProperty("runtime_bindable").GetBoolean().Should().BeTrue();
        nodeTypes.Single(node => node.GetProperty("yaml_type").GetString() == "fan_in")
            .GetProperty("runtime_bindable").GetBoolean().Should().BeTrue();
        grammar.GetProperty("node_fields").GetProperty("optional_fields").EnumerateArray()
            .Select(field => field.GetString())
            .Should().Contain(["independent", "declared_output_paths"]);

        var bindable = WorkflowDefinitionLoader.Load("""
            id: contract-client
            name: Contract client
            version: "1"
            start: author
            nodes:
              - id: author
                type: prompt
              - id: done
                type: terminal
            edges:
              - from: author
                to: done
            """, "contract-client");
        bindable.IsValid.Should().BeTrue(bindable.Error);
        RunWorkflowGraphBinder.GetBindabilityErrors(bindable.Definition!).Should().BeEmpty();

        var promptToTerminal = grammar.GetProperty("edge").GetProperty("transitions").EnumerateArray()
            .Single(transition =>
                transition.GetProperty("from_kind").GetString() == "agent"
                && transition.GetProperty("to_kind").GetString() == "terminal");
        promptToTerminal.GetProperty("unconditional").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task WorkflowGrammar_CheckContractProducesBindableWorkflow()
    {
        var response = await _client.GetAsync("/api/workflows/grammar");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var grammar = document.RootElement;
        var check = grammar.GetProperty("node_types").EnumerateArray()
            .Single(node => node.GetProperty("yaml_type").GetString() == "check");
        check.GetProperty("runtime_bindable").GetBoolean().Should().BeTrue();
        check.GetProperty("required_fields").EnumerateArray()
            .Select(field => field.GetString())
            .Should().Contain(["branches", "gate_kind"]);
        var compatibility = grammar.GetProperty("compatibility");
        compatibility.GetProperty("legacy_loading_only").GetBoolean().Should().BeTrue();
        compatibility.GetProperty("check_gate_id_matching").GetString()
            .Should().Contain("case-insensitive");
        compatibility.GetProperty("check_gate_id_fallbacks").GetProperty("review").GetString()
            .Should().Be("human-review");

        var gateKind = check.GetProperty("allowed_gate_kinds").EnumerateArray()
            .Select(value => value.GetString()!)
            .Intersect(check.GetProperty("runtime_kinds").EnumerateArray().Select(value => value.GetString()!))
            .Single(kind => kind == "human-review");
        var transition = grammar.GetProperty("edge").GetProperty("transitions").EnumerateArray()
            .Single(rule =>
                rule.GetProperty("from_kind").GetString() == gateKind
                && rule.GetProperty("to_kind").GetString() == "terminal");
        var verdict = transition.GetProperty("when").EnumerateArray()
            .Select(value => value.GetString()!)
            .First();

        var result = WorkflowDefinitionLoader.Load($"""
            id: grammar-check-client
            name: Grammar check client
            version: "1"
            start: author
            nodes:
              - id: author
                type: prompt
              - id: review
                type: check
                gate_kind: {gateKind}
                branches:
                  - {verdict}
              - id: done
                type: terminal
            edges:
              - from: author
                to: review
              - from: review
                to: done
                when: {verdict}
            """, "grammar-check-client");

        result.IsValid.Should().BeTrue(result.Error);
        RunWorkflowGraphBinder.GetBindabilityErrors(result.Definition!).Should().BeEmpty();

        var publishedTransitions = grammar.GetProperty("edge").GetProperty("transitions").EnumerateArray()
            .Select(rule => new
            {
                From = rule.GetProperty("from_kind").GetString(),
                To = rule.GetProperty("to_kind").GetString(),
                When = rule.GetProperty("when").EnumerateArray().Select(value => value.GetString()).ToArray(),
            })
            .ToArray();
        publishedTransitions.Should().NotContain(rule =>
            rule.From == "rai" && rule.To == "terminal" && rule.When.Contains("review"));
        publishedTransitions.Should().NotContain(rule =>
            rule.From == "rubberduck" && rule.To == "terminal");
    }

    [Fact]
    public async Task OpenApiJson_DeclaresProviderKeyHeaderOnEveryAiGuardedOperation()
    {
        var response = await _client.GetAsync("/openapi/v1.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");

        // Every operation gated by AiExecutionPlanService. An OpenAPI-guided client can only satisfy
        // the 409 ai_execution_context_required when the header is discoverable here (#1316).
        var guarded = new (string Path, string Method, bool Required)[]
        {
            ("/api/projects/{id}/orchestrations", "post", true),
            ("/api/runs/{id}/outcome-spec/confirm", "post", true),
            ("/api/runs/{id}/outcome-spec/revise", "post", true),
            ("/api/runs/{coordinatorRunId}/steer", "post", false),
            ("/api/runs/{coordinatorRunId}/assembly/review", "post", false),
            ("/api/runs/{id}/review", "post", false),
            ("/api/runs/{id}/request-changes", "post", true),
            ("/api/runs/{id}/retry", "post", true),
            ("/api/assistant/runs", "post", false),
            ("/api/assistant/runs/{id}/messages", "post", true),
            ("/api/projects/{id}/backlog/decompose", "post", true),
            ("/api/blueprints/generate", "post", true),
            ("/api/projects/{id}/casting/proposals", "post", false),
            ("/api/projects/{id}/skills/generate", "post", true),
            ("/api/projects/{id}/skill-marketplaces/{marketplace}/browse", "post", false),
            ("/api/projects/{projectId}/workflows/{workflowId}/run", "post", true),
            ("/api/projects/{projectId}/workflows/generate", "post", true),
        };

        var missing = new List<string>();
        foreach (var (path, method, required) in guarded)
        {
            if (!paths.TryGetProperty(path, out var pathItem)
                || !pathItem.TryGetProperty(method, out var operation))
            {
                missing.Add($"{method} {path} (route not in document)");
                continue;
            }

            var header = operation.TryGetProperty("parameters", out var parameters)
                ? parameters.EnumerateArray().FirstOrDefault(parameter =>
                    parameter.GetProperty("name").GetString() == AiExecutionPlanHeaders.ProviderKey
                    && parameter.GetProperty("in").GetString() == "header")
                : default;
            if (header.ValueKind != JsonValueKind.Object)
            {
                missing.Add($"{method} {path} (no {AiExecutionPlanHeaders.ProviderKey} header)");
                continue;
            }

            var isRequired = header.TryGetProperty("required", out var requiredFlag)
                && requiredFlag.GetBoolean();
            if (isRequired != required)
                missing.Add($"{method} {path} (required should be {required})");
            var describes = header.TryGetProperty("description", out var headerDescription)
                && headerDescription.GetString()?.Contains("/api/ai/execution-context") == true;
            if (!describes)
                missing.Add($"{method} {path} (description omits the prepare endpoint)");
        }

        missing.Should().BeEmpty("every AI-guarded operation must declare its precondition header");
    }

    [Fact]
    public async Task OpenApiYaml_UsesYamlRoute_AndExposesSameDocumentSurface()
    {
        var response = await _client.GetAsync("/openapi/v1.yaml");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("openapi:");
        body.Should().Contain("title: Agentweaver API");
        body.Should().Contain("/api/projects:");
        body.Should().Contain("components:");
        body.Should().Contain("securitySchemes:");
    }
}
