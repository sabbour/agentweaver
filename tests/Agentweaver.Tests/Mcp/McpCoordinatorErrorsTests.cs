using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Agentweaver.Mcp;
using Agentweaver.Mcp.Tools;
using ModelContextProtocol;

namespace Agentweaver.Tests.Mcp;

/// <summary>
/// Regression tests for #347: MCP <c>coordinator_start</c> (and the other coordinator tools that
/// share the same dispatch path) must surface the real underlying API error detail instead of the
/// opaque MCP wrapper message "An error occurred invoking '&lt;tool&gt;'.".
///
/// The MCP SDK only appends an exception's message to the tool-call error content when the thrown
/// exception derives from <see cref="McpException"/>; any other exception collapses to the opaque
/// generic string. These tests lock in that every coordinator tool threads backend failures through
/// <see cref="McpApiException"/> (an <see cref="McpException"/>) so the actionable detail survives.
/// </summary>
public sealed class McpCoordinatorErrorsTests
{
    // The exact opaque string the MCP SDK emits for a non-McpException. If any assertion below ever
    // matches this, the swallow described in #347 has regressed.
    private const string OpaqueWrapperMessage = "An error occurred invoking 'coordinator_start'.";

    [Fact]
    public async Task CoordinatorStart_BadRequest_SurfacesRealDetailNotOpaqueWrapper()
    {
        var tools = CreateCoordinatorTools((request, _) =>
        {
            request.Method.Should().Be(HttpMethod.Post);
            request.RequestUri!.AbsolutePath.Should().Be("/api/projects/proj-1/orchestrations");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = JsonContent.Create(new { error = "model_id is not allowed." })
            });
        });

        var act = () => tools.CoordinatorStartAsync("proj-1", "Ship the thing", model_id: "bad-model", ct: CancellationToken.None);

        var ex = await act.Should().ThrowAsync<McpApiException>();

        // It is an McpException, so the SDK forwards ex.Message to the client (see SDK
        // AIFunctionMcpServerTool.InvokeAsync) instead of the opaque generic string.
        ex.Which.Should().BeAssignableTo<McpException>();
        ex.Which.StatusCode.Should().Be(400);
        ex.Which.Error.Should().Be("model_id is not allowed.");

        // The real detail is present in the serialized message the client receives...
        using var payload = JsonDocument.Parse(ex.Which.Message);
        payload.RootElement.GetProperty("error").GetString().Should().Be("model_id is not allowed.");
        payload.RootElement.TryGetProperty("hint", out _).Should().BeTrue();

        // ...and it is NOT the opaque wrapper #347 reported.
        ex.Which.Message.Should().NotBe(OpaqueWrapperMessage);
        ex.Which.Message.Should().Contain("model_id is not allowed.");
    }

    [Fact]
    public async Task CoordinatorStart_ProjectNotFound_SurfacesProjectHint()
    {
        // The backend returns 404 with an empty body for a missing project (Results.NotFound()).
        // The mapping must still produce an actionable, project-scoped message rather than swallowing it.
        var tools = CreateCoordinatorTools((request, _) =>
        {
            request.RequestUri!.AbsolutePath.Should().Be("/api/projects/proj-404/orchestrations");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var act = () => tools.CoordinatorStartAsync("proj-404", "Do work", model_id: null, ct: CancellationToken.None);

        var ex = await act.Should().ThrowAsync<McpApiException>();
        ex.Which.StatusCode.Should().Be(404);
        ex.Which.Error.Should().Be("Project 'proj-404' not found.");
        ex.Which.Hint.Should().Be("Call project_list to see available projects.");
        ex.Which.Message.Should().NotBe(OpaqueWrapperMessage);
    }

    [Fact]
    public async Task CoordinatorStart_NoTeamConflict_SurfacesConflictDetail()
    {
        var tools = CreateCoordinatorTools((request, _) =>
        {
            request.RequestUri!.AbsolutePath.Should().Be("/api/projects/proj-1/orchestrations");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = JsonContent.Create(new
                {
                    error = "no_team",
                    message = "This project has no team. Cast a team before starting a coordinator run."
                })
            });
        });

        var act = () => tools.CoordinatorStartAsync("proj-1", "Ship it", model_id: null, ct: CancellationToken.None);

        var ex = await act.Should().ThrowAsync<McpApiException>();
        ex.Which.StatusCode.Should().Be(409);
        // The backend message survives into the surfaced error, not the opaque wrapper.
        ex.Which.Message.Should().Contain("no team");
        ex.Which.Message.Should().NotBe(OpaqueWrapperMessage);
    }

    [Fact]
    public async Task CoordinatorSteer_BackendConflict_SurfacesRealDetail()
    {
        // A second coordinator tool exercising the same dispatch/error-mapping path proves the fix
        // is not specific to coordinator_start.
        var tools = CreateCoordinatorTools((request, _) =>
        {
            if (request.Method == HttpMethod.Get
                && request.RequestUri!.AbsolutePath == "/api/runs/run-1")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        run_id = "run-1",
                        project_id = "proj-1",
                        parent_run_id = (string?)null,
                        agent_name = "Coordinator",
                    }),
                });
            }
            request.Method.Should().Be(HttpMethod.Post);
            request.RequestUri!.AbsolutePath.Should().Be("/api/runs/run-1/steer");
            request.Headers.GetValues("If-Model-Provider-Key").Should().ContainSingle()
                .Which.Should().Be("signed-provider-key");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = JsonContent.Create(new { error = "Run is not in a steerable state (current state: 'completed')." })
            });
        });

        var act = () => tools.CoordinatorSteerAsync(
            "run-1", kind: "redirect", instruction: "focus on tests", target_child_run_id: null, ct: CancellationToken.None);

        var ex = await act.Should().ThrowAsync<McpApiException>();
        ex.Which.Should().BeAssignableTo<McpException>();
        ex.Which.StatusCode.Should().Be(409);
        ex.Which.Message.Should().Contain("steerable state");
        ex.Which.Message.Should().NotBe("An error occurred invoking 'coordinator_steer'.");
    }

    [Fact]
    public async Task CoordinatorStart_ProviderFailure_IsActionable()
    {
        var tools = new CoordinatorTools(CreateApiClient((request, _) =>
        {
            request.RequestUri!.AbsolutePath.Should().Be("/api/ai/execution-context");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    ai_required = true,
                    operation = "orchestration",
                    phase = "prepared",
                    execution_key = (string?)null,
                    expires_at = (DateTimeOffset?)null,
                    effective_model_provider = new
                    {
                        state = "unavailable",
                        provider_kind = "unavailable",
                        resolution_scope = "project",
                        provider_scope = "none",
                        provider_type = (string?)null,
                        model_id = (string?)null,
                        provider_key = (string?)null,
                        unavailable_reason = "no_provider",
                    },
                }),
            });
        }, bypassPreflight: true));

        var act = () => tools.CoordinatorStartAsync(
            "proj-1", "Ship it", model_id: null, ct: CancellationToken.None);

        var ex = await act.Should().ThrowAsync<McpApiException>();
        ex.Which.StatusCode.Should().Be(409);
        ex.Which.Error.Should().Contain("effective AI provider");
        ex.Which.Hint.Should().NotBeNullOrWhiteSpace();
        ex.Which.Message.Should().NotBe(OpaqueWrapperMessage);
    }

    [Fact]
    public async Task CoordinatorStart_ResolvedAzureByokContext_ForwardsItsExecutionKey()
    {
        var tools = new CoordinatorTools(CreateApiClient((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/ai/execution-context")
            {
                request.Method.Should().Be(HttpMethod.Post);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        ai_required = true,
                        operation = "orchestration",
                        phase = "prepared",
                        execution_key = "azure-byok-execution-key",
                        expires_at = DateTimeOffset.UtcNow.AddMinutes(5),
                        effective_model_provider = new
                        {
                            state = "resolved",
                            provider_kind = "platform_byok",
                            resolution_scope = "project",
                            provider_scope = "platform",
                            provider_type = "azure_openai",
                            model_id = "gpt-5",
                            provider_key = "provider-fingerprint",
                            unavailable_reason = (string?)null,
                        },
                    }),
                });
            }

            request.RequestUri!.AbsolutePath.Should().Be("/api/projects/proj-1/orchestrations");
            request.Headers.GetValues("If-Model-Provider-Key").Should().ContainSingle()
                .Which.Should().Be("azure-byok-execution-key");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = JsonContent.Create(new { run_id = "run-1" }),
            });
        }, bypassPreflight: true));

        var result = await tools.CoordinatorStartAsync("proj-1", "Ship it", model_id: null, ct: CancellationToken.None);

        result.Should().Contain("run-1");
    }

    [Fact]
    public async Task MarketplaceBrowse_AutoDetectCacheHit_DoesNotPrepareProvider()
    {
        var preflightCount = 0;
        var browseCount = 0;
        var tools = new SkillTools(CreateApiClient((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/projects/proj-1/skill-marketplaces")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new[] { new { name = "catalog", auto_detect = true } }),
                });
            }
            if (request.RequestUri.AbsolutePath == "/api/ai/execution-context")
            {
                preflightCount++;
                throw new InvalidOperationException("A cached marketplace browse must not prepare a provider.");
            }

            browseCount++;
            request.Headers.Contains("If-Model-Provider-Key").Should().BeFalse();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { marketplace = "catalog", candidates = Array.Empty<object>() }),
            });
        }, bypassPreflight: true));

        var result = await tools.SkillMarketplaceBrowseAsync("proj-1", "catalog");

        using (var json = JsonDocument.Parse(result))
            json.RootElement.GetProperty("marketplace").GetString().Should().Be("catalog");
        browseCount.Should().Be(1);
        preflightCount.Should().Be(0);
    }

    [Fact]
    public async Task MarketplaceBrowse_AutoDetectClassifierNeed_PreparesAndRetriesWithProviderKey()
    {
        var browseCount = 0;
        var preflightCount = 0;
        var tools = new SkillTools(CreateApiClient((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/projects/proj-1/skill-marketplaces")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new[] { new { name = "catalog", auto_detect = true } }),
                });
            }
            if (request.RequestUri.AbsolutePath == "/api/ai/execution-context")
            {
                preflightCount++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        ai_required = true,
                        operation = "marketplace_catalog_classification",
                        phase = "prepared",
                        execution_key = "signed-provider-key",
                        expires_at = DateTimeOffset.UtcNow.AddMinutes(5),
                        effective_model_provider = new
                        {
                            state = "resolved",
                            provider_kind = "platform_github_copilot",
                            resolution_scope = "project",
                            provider_scope = "platform",
                            provider_type = (string?)null,
                            model_id = "gpt-5",
                            provider_key = "provider-fingerprint",
                            unavailable_reason = (string?)null,
                        },
                    }),
                });
            }

            browseCount++;
            if (browseCount == 1)
            {
                request.Headers.Contains("If-Model-Provider-Key").Should().BeFalse();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
                {
                    Content = JsonContent.Create(new
                    {
                        error = "ai_execution_context_required",
                        message = "Prepare the AI execution context before starting this operation.",
                    }),
                });
            }

            request.Headers.GetValues("If-Model-Provider-Key").Should().ContainSingle()
                .Which.Should().Be("signed-provider-key");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { marketplace = "catalog", candidates = Array.Empty<object>() }),
            });
        }, bypassPreflight: true));

        var result = await tools.SkillMarketplaceBrowseAsync("proj-1", "catalog");

        using (var json = JsonDocument.Parse(result))
            json.RootElement.GetProperty("marketplace").GetString().Should().Be("catalog");
        browseCount.Should().Be(2);
        preflightCount.Should().Be(1);
    }

    private static CoordinatorTools CreateCoordinatorTools(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) =>
        new(CreateApiClient(handler));

    private static AgentweaverApiClient CreateApiClient(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler,
        bool bypassPreflight = false)
    {
        var httpClient = new HttpClient(new DelegatingHandlerStub((request, ct) =>
        {
            if (!bypassPreflight && request.RequestUri!.AbsolutePath == "/api/ai/execution-context")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        ai_required = true,
                        operation = "orchestration",
                        phase = "prepared",
                        execution_key = "signed-provider-key",
                        expires_at = DateTimeOffset.UtcNow.AddMinutes(5),
                        effective_model_provider = new
                        {
                            state = "resolved",
                            provider_kind = "platform_github_copilot",
                            resolution_scope = "project",
                            provider_scope = "platform",
                            provider_type = (string?)null,
                            model_id = "gpt-5",
                            provider_key = "provider-fingerprint",
                            unavailable_reason = (string?)null,
                        },
                    }),
                });
            }
            if (!bypassPreflight
                && request.RequestUri!.AbsolutePath.EndsWith("/orchestrations", StringComparison.Ordinal))
            {
                request.Headers.GetValues("If-Model-Provider-Key").Should().ContainSingle()
                    .Which.Should().Be("signed-provider-key");
            }
            return handler(request, ct);
        }))
        {
            BaseAddress = new Uri("http://localhost/")
        };
        return new AgentweaverApiClient(httpClient, new McpConfig("http://localhost", "test-api-key"));
    }

    private sealed class DelegatingHandlerStub(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
