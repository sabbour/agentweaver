using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Api.Blueprints;
using Agentweaver.Api.Casting;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;
using Agentweaver.Squad.Catalog;
using Agentweaver.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Tests.Blueprints;

/// <summary>
/// Integration tests for the blueprint runtime (Feature 012): listing predefined blueprints,
/// applying a blueprint at project creation, the role constraint (reject unknown roles/no minting),
/// and generate-output validation. Uses live SQLite + the real catalog and
/// casting pipeline through the web host; only the external model is stubbed (StubBlueprintGenerator).
/// </summary>
public sealed class BlueprintEndpointsTests : IClassFixture<BlueprintsWebApplicationFactory>
{
    private readonly BlueprintsWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public BlueprintEndpointsTests(BlueprintsWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    private async Task<(string Id, string Dir)> CreateBlankWithBlueprintAsync(CreateProjectRequest request)
    {
        var response = await _client.PostAsJsonAsync("/api/projects", request);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("project_id").GetString()!, body.GetProperty("working_directory").GetString()!);
    }

    private async Task<(HttpResponseMessage Accepted, JsonElement Job)> GenerateBlueprintAsync(
        GenerateBlueprintRequest request,
        string? idempotencyKey = null)
    {
        var (accepted, job) = await SubmitBlueprintAsync(request, idempotencyKey);
        accepted.StatusCode.Should().Be(HttpStatusCode.Accepted, await accepted.Content.ReadAsStringAsync());
        var worker = _factory.Services.GetServices<IHostedService>()
            .OfType<BlueprintGenerationJobWorker>()
            .Single();
        _ = await worker.RunOneAsync(CancellationToken.None);
        var statusUrl = job.GetProperty("status_url").GetString()!;
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var statusResponse = await _client.GetAsync(statusUrl);
            statusResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            job = await statusResponse.Content.ReadFromJsonAsync<JsonElement>();
            if (job.GetProperty("status").GetString() is not ("queued" or "running"))
            {
                _factory.Generator.ExceptionToThrow = null;
                return (accepted, job);
            }
            await Task.Delay(25);
        }
        throw new TimeoutException("Blueprint generation job did not reach a terminal state.");
    }

    private async Task<(HttpResponseMessage Response, JsonElement Body)> SubmitBlueprintAsync(
        GenerateBlueprintRequest request,
        string? idempotencyKey = null)
    {
        await _factory.PrepareAiExecutionAsync(_client, "blueprint_generation", request.ProjectId);
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/blueprints/generate")
        {
            Content = JsonContent.Create(request),
        };
        message.Headers.Add("Idempotency-Key", idempotencyKey ?? Guid.NewGuid().ToString("N"));
        var response = await _client.SendAsync(message);
        return (response, await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    [Fact]
    public async Task WorkerClaim_DoesNotStarveQueuedJobBehindActiveLeases()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var prefix = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        for (var index = 0; index < 100; index++)
        {
            db.BlueprintGenerationJobs.Add(new BlueprintGenerationJobRecord
            {
                JobId = $"{prefix}-running-{index:D3}",
                Subject = $"{prefix}-subject",
                IdempotencyKey = $"running-{index:D3}",
                RequestFingerprint = prefix,
                Description = "active",
                ProviderKind = "byok",
                ProviderKey = prefix,
                ProviderScope = "global",
                ResolutionScope = "global",
                QueuedProviderKey = prefix,
                Status = BlueprintGenerationJobStatuses.Running,
                LeaseOwner = $"owner-{index}",
                LeaseExpiresAt = now.AddMinutes(1),
                CreatedAt = now.AddMinutes(-2),
                UpdatedAt = now,
            });
        }
        var queuedId = $"{prefix}-queued";
        db.BlueprintGenerationJobs.Add(new BlueprintGenerationJobRecord
        {
            JobId = queuedId,
            Subject = $"{prefix}-subject",
            IdempotencyKey = "queued",
            RequestFingerprint = prefix,
            Description = "queued",
            ProviderKind = "byok",
            ProviderKey = prefix,
            ProviderScope = "global",
            ResolutionScope = "global",
            QueuedProviderKey = prefix,
            Status = BlueprintGenerationJobStatuses.Queued,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        var store = scope.ServiceProvider.GetRequiredService<BlueprintGenerationJobStore>();
        var claimed = await store.TryClaimNextAsync("test-lease", TimeSpan.FromMinutes(2), CancellationToken.None);

        claimed.Should().NotBeNull();
        claimed!.Job.JobId.Should().Be(queuedId);
        (await db.BlueprintGenerationJobs.CountAsync(x => x.Status == BlueprintGenerationJobStatuses.Running))
            .Should().Be(101);
    }

    private Task<HttpResponseMessage> GetGenerationResultAsync(JsonElement job) =>
        _client.GetAsync(job.GetProperty("result_url").GetString()!);

    [Fact]
    public async Task GenerateBlueprint_IdempotencyReturnsSameJobAndRejectsConflict()
    {
        const string key = "blueprint-idempotency-contract";
        var request = new GenerateBlueprintRequest { Description = "a durable data team" };
        var (firstResponse, first) = await SubmitBlueprintAsync(request, key);
        var (sameResponse, same) = await SubmitBlueprintAsync(request, key);
        var (conflictResponse, conflict) = await SubmitBlueprintAsync(
            new GenerateBlueprintRequest { Description = "a different team" },
            key);

        firstResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        sameResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        same.GetProperty("job_id").GetString().Should().Be(first.GetProperty("job_id").GetString());
        conflictResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        conflict.GetProperty("error").GetString().Should().Be("idempotency_key_conflict");
        _ = await _client.PostAsync(
            $"/api/blueprints/generation-jobs/{first.GetProperty("job_id").GetString()}/cancel",
            content: null);
    }

    [Fact]
    public async Task GenerateBlueprint_CancelRetryCompletesOneImmutableArtifact()
    {
        _factory.Generator.Response = """
            {
              "id": "retry-blueprint",
              "name": "Retry Blueprint",
              "description": "Exercises durable retry.",
              "roster": ["backend-engineer"],
              "workflows": ["software-delivery"],
              "review_policy": "default",
              "sandbox_profile": "default"
            }
            """;
        var (accepted, job) = await SubmitBlueprintAsync(
            new GenerateBlueprintRequest { Description = "retryable blueprint" });
        accepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var jobId = job.GetProperty("job_id").GetString()!;

        var cancelled = await _client.PostAsync(
            $"/api/blueprints/generation-jobs/{jobId}/cancel",
            content: null);
        cancelled.StatusCode.Should().Be(HttpStatusCode.OK);
        (await cancelled.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("status").GetString().Should().Be("cancelled");

        var retried = await _client.PostAsync(
            $"/api/blueprints/generation-jobs/{jobId}/retry",
            content: null);
        retried.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var worker = _factory.Services.GetServices<IHostedService>()
            .OfType<BlueprintGenerationJobWorker>()
            .Single();
        JsonElement status = default;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            _ = await worker.RunOneAsync(CancellationToken.None);
            status = await _client.GetFromJsonAsync<JsonElement>(
                $"/api/blueprints/generation-jobs/{jobId}");
            if (status.GetProperty("status").GetString() == "completed")
                break;
        }
        status.GetProperty("status").GetString().Should().Be("completed");
        status.GetProperty("artifact").GetProperty("version").GetInt32().Should().Be(1);
        var artifactId = status.GetProperty("artifact").GetProperty("artifact_id").GetString();

        var retryCompleted = await _client.PostAsync(
            $"/api/blueprints/generation-jobs/{jobId}/retry",
            content: null);
        retryCompleted.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var unchanged = await _client.GetFromJsonAsync<JsonElement>(
            $"/api/blueprints/generation-jobs/{jobId}");
        unchanged.GetProperty("artifact").GetProperty("artifact_id").GetString().Should().Be(artifactId);
    }

    [Fact]
    public async Task GetBlueprints_ReturnsFivePredefined_WithCatalogRostersAndExportability()
    {
        var response = await _client.GetAsync("/api/blueprints");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var blueprints = body.GetProperty("blueprints").EnumerateArray().ToList();
        blueprints.Should().HaveCount(5);

        var ids = blueprints.Select(b => b.GetProperty("id").GetString()).ToList();
        ids.Should().Contain(new[]
        {
            "blueprint-content-authoring",
            "blueprint-ai-agent-engineering",
            "blueprint-product-management",
            "blueprint-software-development",
            "blueprint-pm-and-software-development",
        });

        var catalog = _factory.Services.GetRequiredService<CatalogReader>();
        foreach (var b in blueprints)
        {
            var roster = b.GetProperty("roster").EnumerateArray().Select(r => r.GetString()!).ToList();
            roster.Should().NotBeEmpty();
            foreach (var roleId in roster)
                catalog.HasRole(roleId).Should().BeTrue($"roster role '{roleId}' must resolve in the catalog");

            b.GetProperty("workflow").GetString().Should().NotBeNullOrWhiteSpace();
            b.GetProperty("review_policy").GetString().Should().NotBeNullOrWhiteSpace();
            b.GetProperty("sandbox_profile").GetString().Should().NotBeNullOrWhiteSpace();
            b.GetProperty("exportability").GetProperty("status").GetString().Should().Be("exportable");
            b.GetProperty("exportability").GetProperty("codes").EnumerateArray().Should().BeEmpty();
        }
    }

    [Fact]
    public async Task CreateProject_WithPredefinedBlueprint_SeedsRosterAndDefaults()
    {
        var dir = _factory.NewWorkingDirectory();
        var request = new CreateProjectRequest
        {
            Name = "SD Blueprint Project",
            Origin = "blank",
            WorkingDirectory = dir,
            BlueprintId = "blueprint-software-development",
        };

        var (id, _) = await CreateBlankWithBlueprintAsync(request);

        // Project defaults reflect the blueprint.
        var store = _factory.Services.GetRequiredService<IProjectStore>();
        var project = await store.GetAsync(ProjectId.Parse(id));
        project.Should().NotBeNull();
        project!.DefaultWorkflowId.Should().Be("software-delivery");
        project.ActiveReviewPolicyName.Should().Be("default");
        project.SandboxProfile.Should().Be("default");

        // Roster seeded by casting contains the blueprint roles.
        var casting = _factory.Services.GetRequiredService<CastingService>();
        var team = await casting.GetTeamAsync(id, CancellationToken.None);
        team.Should().NotBeNull();
        var roleIds = team!.Members.Select(m => m.Role.Id).ToList();
        roleIds.Should().Contain(new[]
        {
            "lead-architect", "frontend-engineer", "backend-engineer", "security-engineer", "docs-writer",
        });
    }

    [Fact]
    public async Task CreateProject_InlineBlueprintWithRestrictedSandbox_WritesConcretePolicy()
    {
        var dir = _factory.NewWorkingDirectory();
        var request = new CreateProjectRequest
        {
            Name = "Restricted Sandbox Blueprint Project",
            Origin = "blank",
            WorkingDirectory = dir,
            Blueprint = new BlueprintDto
            {
                Id = "blueprint-restricted",
                Name = "Restricted",
                Description = "Uses the restricted sandbox preset.",
                Roster = ["backend-engineer"],
                Workflow = "default",
                ReviewPolicy = "default",
                SandboxProfile = "restricted",
            },
        };

        var (id, _) = await CreateBlankWithBlueprintAsync(request);

        var store = _factory.Services.GetRequiredService<IProjectStore>();
        var project = await store.GetAsync(ProjectId.Parse(id));
        project!.SandboxProfile.Should().Be("restricted");

        var policyStore = _factory.Services.GetRequiredService<ISandboxPolicyStore>();
        var policy = await policyStore.GetPolicyAsync(dir);
        policy.RequireApprovalForAllShell.Should().BeTrue(
            "the blueprint sandbox profile must be enforced via a persisted concrete policy");
        policy.NetworkEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task ValidateBlueprint_SecurityEngineerRole_IsAccepted()
    {
        // bp-allow-security: the core "security-engineer" role (added to the catalog by tank-4) must be
        // rosterable in blueprints. Validation is catalog-driven, so it is accepted once the role lands.
        var request = new ValidateBlueprintRequest
        {
            Blueprint = new BlueprintDto
            {
                Id = "blueprint-quick-software",
                Name = "Quick Software",
                Description = "Frontend, backend, and security.",
                Roster = ["frontend-engineer", "backend-engineer", "security-engineer"],
                Workflow = "default",
                ReviewPolicy = "default",
                SandboxProfile = "default",
            },
        };

        var response = await _client.PostAsJsonAsync("/api/blueprints/validate", request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("valid").GetBoolean().Should().BeTrue(
            (await response.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task ValidateBlueprint_NullSkillBinding_ReturnsValidationError()
    {
        var response = await _client.PostAsJsonAsync("/api/blueprints/validate", new
        {
            blueprint = new
            {
                id = "blueprint-null-binding",
                name = "Null Binding",
                description = "Malformed binding fixture",
                roster = new[] { "qa-engineer" },
                workflows = new[] { "default" },
                review_policy = "default",
                sandbox_profile = "default",
                skill_bindings = new object?[] { null },
            },
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("valid").GetBoolean().Should().BeFalse();
        body.GetProperty("errors").EnumerateArray()
            .Select(error => error.GetString())
            .Should().Contain(error => error!.Contains("role_id", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidateBlueprint_UnknownRole_IsRejected()
    {
        var request = new ValidateBlueprintRequest
        {
            Blueprint = new BlueprintDto
            {
                Id = "blueprint-bogus",
                Name = "Bogus",
                Description = "Has a role nobody registered.",
                Roster = ["totally-unknown-role"],
                Workflow = "default",
                ReviewPolicy = "default",
                SandboxProfile = "default",
            },
        };

        var response = await _client.PostAsJsonAsync("/api/blueprints/validate", request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("valid").GetBoolean().Should().BeFalse();
        var errors = body.GetProperty("errors").EnumerateArray().Select(e => e.GetString()).ToList();
        errors.Should().Contain(e => e!.Contains("totally-unknown-role"));
    }

    [Fact]
    public async Task ValidateBlueprint_WithGeneratedWorkflowYaml_AcceptsReferencedWorkflow()
    {
        const string workflowId = "generated-blueprint-workflow";
        const string yaml = """
            id: generated-blueprint-workflow
            name: Generated Blueprint Workflow
            description: A generated workflow used by a blueprint.
            version: "1.0"
            start: agent
            nodes:
              - id: agent
                type: prompt
                label: Agent
                agent: lead
            """;

        var response = await _client.PostAsJsonAsync("/api/blueprints/validate", new
        {
            blueprint = new
            {
                id = "blueprint-generated-workflow",
                name = "Generated Workflow",
                description = "References a generated workflow.",
                roster = new[] { "backend-engineer" },
                workflow = workflowId,
                review_policy = "default",
                sandbox_profile = "default",
                generated_workflow_yaml = yaml,
            },
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("valid").GetBoolean().Should().BeTrue(
            await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("scribe")]
    [InlineData("work-monitor")]
    [InlineData("coordinator")]
    [InlineData("rai")]
    public async Task ValidateBlueprint_ReservedOrchestrationRole_IsRejected(string reservedRoleId)
    {
        // Regression for #311: Scribe, Work Monitor, Coordinator, and Rai are platform-owned
        // orchestration roles provisioned automatically for every team. A generated blueprint must
        // never be able to roster them, even though "scribe"/"work-monitor" resolve as real catalog
        // roles (their catalog entries exist only so the built-in charters can be compiled).
        var request = new ValidateBlueprintRequest
        {
            Blueprint = new BlueprintDto
            {
                Id = "blueprint-reserved-role",
                Name = "Reserved Role",
                Description = "Attempts to roster a reserved orchestration role.",
                Roster = ["backend-engineer", reservedRoleId],
                Workflow = "default",
                ReviewPolicy = "default",
                SandboxProfile = "default",
            },
        };

        var response = await _client.PostAsJsonAsync("/api/blueprints/validate", request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("valid").GetBoolean().Should().BeFalse();
        var errors = body.GetProperty("errors").EnumerateArray().Select(e => e.GetString()).ToList();
        errors.Should().Contain(e => e!.Contains(reservedRoleId) && e.Contains("reserved"));
    }

    [Fact]
    public async Task CreateProject_InlineBlueprintWithReservedRole_IsRejected()
    {
        var dir = _factory.NewWorkingDirectory();
        var request = new CreateProjectRequest
        {
            Name = "Reserved Role Blueprint Project",
            Origin = "blank",
            WorkingDirectory = dir,
            Blueprint = new BlueprintDto
            {
                Id = "blueprint-with-reserved-role",
                Name = "With Reserved Role",
                Description = "References the Scribe orchestration role.",
                Roster = ["backend-engineer", "scribe"],
                Workflow = "default",
                ReviewPolicy = "default",
                SandboxProfile = "default",
            },
        };

        var response = await _client.PostAsJsonAsync("/api/projects", request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("invalid_blueprint");
        var details = body.GetProperty("details").EnumerateArray().Select(e => e.GetString()).ToList();
        details.Should().Contain(e => e!.Contains("scribe") && e.Contains("reserved"));
    }

    [Fact]
    public async Task CreateProject_InlineBlueprintWithUnknownRole_IsRejected()
    {
        var dir = _factory.NewWorkingDirectory();
        // Blueprints never mint roles: an inline blueprint that rosters a non-catalog role is rejected
        // at creation and no role is created.
        const string unknownRoleId = "growth-hacker";
        var request = new CreateProjectRequest
        {
            Name = "Unknown Role Blueprint Project",
            Origin = "blank",
            WorkingDirectory = dir,
            Blueprint = new BlueprintDto
            {
                Id = "blueprint-with-unknown-role",
                Name = "With Unknown Role",
                Description = "References a role that is not in the catalog.",
                Roster = ["backend-engineer", unknownRoleId],
                Workflow = "default",
                ReviewPolicy = "default",
                SandboxProfile = "default",
            },
        };

        var response = await _client.PostAsJsonAsync("/api/projects", request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("invalid_blueprint");
        var details = body.GetProperty("details").EnumerateArray().Select(e => e.GetString()).ToList();
        details.Should().Contain(e => e!.Contains(unknownRoleId));

        // No role was minted: the catalog still does not know the unknown role.
        var catalog = _factory.Services.GetRequiredService<CatalogReader>();
        catalog.HasRole(unknownRoleId).Should().BeFalse();
    }

    [Fact]
    public async Task CreateProject_InlineBlueprintWithUnknownWorkflow_IsRejected()
    {
        var request = new CreateProjectRequest
        {
            Name = "Unknown References Blueprint Project",
            Origin = "blank",
            WorkingDirectory = _factory.NewWorkingDirectory(),
            Blueprint = new BlueprintDto
            {
                Id = "blueprint-with-unknown-refs",
                Name = "With Unknown References",
                Description = "References a workflow id that is unavailable.",
                Roster = ["backend-engineer"],
                Workflow = "missing-workflow",
                ReviewPolicy = "default",
                SandboxProfile = "default",
            },
        };

        var response = await _client.PostAsJsonAsync("/api/projects", request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("invalid_blueprint");
        var details = body.GetProperty("details").EnumerateArray().Select(e => e.GetString()).ToList();
        details.Should().Contain(e => e!.Contains("missing-workflow"));
    }

    [Fact]
    public async Task GenerateBlueprint_UsesProjectBlueprintGenerationModel()
    {
        _factory.Generator.Response =
            """
            {
              "id": "data-team",
              "name": "Data Team",
              "description": "Runs data operations.",
              "roster": ["backend-engineer"],
              "workflows": ["software-delivery"],
              "review_policy": "default",
              "sandbox_profile": "default"
            }
            """;

        var (projectId, _) = await CreateBlankWithBlueprintAsync(new CreateProjectRequest
        {
            Name = $"Blueprint Model Test {Guid.NewGuid():N}",
            Origin = "blank",
            WorkingDirectory = _factory.NewWorkingDirectory(),
        });

        var update = await _client.PutAsJsonAsync(
            $"/api/projects/{projectId}/provider-settings",
            new { blueprint_generation_model = "gpt-5-mini" });
        update.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var (_, job) = await GenerateBlueprintAsync(
            new GenerateBlueprintRequest { ProjectId = projectId, Description = "a data team" });

        job.GetProperty("status").GetString().Should().Be("completed");
        _factory.Generator.LastModelId.Should().Be("gpt-5-mini");
    }

    [Fact]
    public async Task GenerateBlueprint_MalformedModelOutput_Returns422()
    {
        _factory.Generator.Response = "I am sorry, I cannot produce that.";

        var (_, job) = await GenerateBlueprintAsync(
            new GenerateBlueprintRequest { Description = "a data team" });

        job.GetProperty("status").GetString().Should().Be("failed");
        job.GetProperty("failure").GetProperty("code").GetString().Should().Be("blueprint_generation_invalid");
    }

    [Fact]
    public async Task GenerateBlueprint_ProviderModelListFailure_ReturnsCanonicalRedactedFailure()
    {
        _factory.Generator.ExceptionToThrow = AgentProviderException.Classify(
            ModelSource.GitHubCopilot,
            new InvalidOperationException("Session error: Execution failed: Error: Failed to list models"))!;

        var (_, job) = await GenerateBlueprintAsync(
            new GenerateBlueprintRequest { Description = "a data team" });

        job.GetProperty("status").GetString().Should().Be("failed");
        var failure = job.GetProperty("failure");
        failure.GetProperty("code").GetString().Should().Be("blueprint_provider_unavailable");
        failure.GetProperty("message").GetString().Should().NotContain("list models");
        failure.GetProperty("retryable").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task GenerateBlueprint_UnexpectedGeneratorFailure_Returns500InternalError()
    {
        _factory.Generator.ExceptionToThrow = new InvalidOperationException("boom");

        var (_, job) = await GenerateBlueprintAsync(
            new GenerateBlueprintRequest { Description = "a data team" });

        job.GetProperty("status").GetString().Should().Be("failed");
        var failure = job.GetProperty("failure");
        failure.GetProperty("code").GetString().Should().Be("blueprint_generation_failed");
        failure.GetProperty("retryable").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task GenerateBlueprint_ProviderTimeout_ReturnsCanonicalRedactedFailure()
    {
        _factory.Generator.ExceptionToThrow = new TimeoutException("UND_ERR_HEADERS_TIMEOUT secret details");

        var (_, job) = await GenerateBlueprintAsync(
            new GenerateBlueprintRequest { Description = "a complex repository workflow" });

        job.GetProperty("status").GetString().Should().Be("failed");
        var failure = job.GetProperty("failure");
        failure.GetProperty("code").GetString().Should().Be("blueprint_provider_timeout");
        failure.GetProperty("message").GetString().Should().NotContain("UND_ERR_HEADERS_TIMEOUT");
        failure.GetProperty("retryable").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task GenerateBlueprint_RosterRoleNotInCatalog_Returns422_NoRoleMinted()
    {
        // The model rostered a role that is not in the catalog; generation must reject (no minting).
        const string unknownRoleId = "growth-hacker";
        _factory.Generator.Response = $$"""
            {
              "id": "blueprint-generated-bad",
              "name": "Generated Bad Team",
              "description": "Rosters a non-catalog role.",
              "roster": ["backend-engineer", "{{unknownRoleId}}"],
              "workflows": ["software-delivery"],
              "review_policy": "default",
              "sandbox_profile": "default"
            }
            """;

        var (_, job) = await GenerateBlueprintAsync(
            new GenerateBlueprintRequest { Description = "a growth team" });

        job.GetProperty("status").GetString().Should().Be("failed");
        job.GetProperty("failure").GetProperty("code").GetString().Should().Be("blueprint_generation_invalid");
        var retry = await _client.PostAsync(
            $"/api/blueprints/generation-jobs/{job.GetProperty("job_id").GetString()}/retry",
            content: null);
        retry.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var catalog = _factory.Services.GetRequiredService<CatalogReader>();
        catalog.HasRole(unknownRoleId).Should().BeFalse();
    }

    [Fact]
    public async Task GenerateBlueprint_ValidModelOutput_ReturnsBlueprint()
    {
        _factory.Generator.Response = """
            {
              "id": "blueprint-generated-data",
              "name": "Generated Data Team",
              "description": "A small data team.",
              "roster": ["backend-engineer", "docs-writer"],
              "workflows": ["software-delivery"],
              "review_policy": "default",
              "sandbox_profile": "restricted"
            }
            """;

        var (_, job) = await GenerateBlueprintAsync(
            new GenerateBlueprintRequest { Description = "a data team" });

        var response = await GetGenerationResultAsync(job);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var blueprint = body.GetProperty("blueprint");
        blueprint.GetProperty("id").GetString().Should().Be("blueprint-generated-data");
        blueprint.GetProperty("name").GetString().Should().Be("Generated Data Team");
        blueprint.GetProperty("description").GetString().Should().Be("A small data team.");
        blueprint.GetProperty("workflow").GetString().Should().Be("software-delivery");
        blueprint.GetProperty("review_policy").GetString().Should().Be("default");
        blueprint.GetProperty("sandbox_profile").GetString().Should().Be("restricted");
        var roster = blueprint.GetProperty("roster").EnumerateArray()
            .Select(r => r.GetString()).ToList();
        roster.Should().Contain(new[] { "backend-engineer", "docs-writer" });
        body.TryGetProperty("new_roles", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GenerateBlueprint_ForwardsTargetRepositoryToGenerator()
    {
        _factory.Generator.Response = """
            {
              "id": "blueprint-generated-triage",
              "name": "Generated Triage Team",
              "description": "A repository triage team.",
              "roster": ["backend-engineer"],
              "workflows": ["software-delivery"],
              "review_policy": "default",
              "sandbox_profile": "restricted"
            }
            """;

        var (_, job) = await GenerateBlueprintAsync(
            new GenerateBlueprintRequest
            {
                Description = "Every Monday: triage GitHub issues",
                TargetRepository = "Azure/aks",
            });

        job.GetProperty("status").GetString().Should().Be("completed");
        _factory.Generator.LastTargetRepository.Should().Be("Azure/aks");
    }

    [Fact]
    public async Task GenerateBlueprint_BespokeRoleMissingFromRoster_IsAutoRostered_ReturnsBlueprint()
    {
        // Mirrors the live staging bug: the LLM declared 'job-match-analyst' in bespoke_roles but
        // forgot to add it to roster. After the reconcile fix, generation must succeed and the
        // auto-rostered bespoke id must appear in the returned roster.
        _factory.Generator.Response = """
            {
              "id": "job-search-team",
              "name": "Job Search Team",
              "description": "Finds and compares jobs, generates a customized CV, and creates an interview guide.",
              "roster": ["backend-engineer"],
              "bespoke_roles": [
                {
                  "id": "job-match-analyst",
                  "title": "Job Match Analyst",
                  "charter": "Analyzes job postings against the user profile. Scores and ranks matches. Identifies skill gaps and strengths. Provides actionable recommendations."
                }
              ],
              "workflows": ["software-delivery"],
              "review_policy": "default",
              "sandbox_profile": "default"
            }
            """;

        var (_, job) = await GenerateBlueprintAsync(
            new GenerateBlueprintRequest { Description = "Find jobs based on my profile, compare them, generate a customized CV, create an interview guide" });

        var response = await GetGenerationResultAsync(job);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var blueprint = body.GetProperty("blueprint");
        var roster = blueprint.GetProperty("roster").EnumerateArray()
            .Select(r => r.GetString()).ToList();
        roster.Should().Contain("job-match-analyst",
            "the bespoke role declared in bespoke_roles must be auto-rostered when the LLM omits it from roster");
    }

    [Fact]
    public async Task GenerateBlueprint_RosterRoleNeitherCatalogNorBespoke_IsStillRejected()
    {
        // Confirms that the reconcile step does NOT loosen validation for a roster role that has
        // no catalog entry and no bespoke definition — it must still be rejected (no role minting).
        const string unknownRoleId = "mystery-guru";
        _factory.Generator.Response = $$"""
            {
              "id": "blueprint-still-bad",
              "name": "Still Bad Team",
              "description": "Rosters a role with no catalog entry and no bespoke definition.",
              "roster": ["backend-engineer", "{{unknownRoleId}}"],
              "bespoke_roles": [],
              "workflows": ["software-delivery"],
              "review_policy": "default",
              "sandbox_profile": "default"
            }
            """;

        var (_, job) = await GenerateBlueprintAsync(
            new GenerateBlueprintRequest { Description = "a mystery team" });

        job.GetProperty("status").GetString().Should().Be("failed");
        job.GetProperty("failure").GetProperty("code").GetString().Should().Be("blueprint_generation_invalid");

        var catalog = _factory.Services.GetRequiredService<CatalogReader>();
        catalog.HasRole(unknownRoleId).Should().BeFalse();
    }
}
