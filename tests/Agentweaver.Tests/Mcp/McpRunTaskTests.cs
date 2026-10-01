using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Agentweaver.Mcp;
using Agentweaver.Mcp.Tools;

namespace Agentweaver.Tests.Mcp;

public sealed class McpRunTaskTests
{
    [Fact]
    public async Task RunTask_HappyPath_ReturnsArtifactsInline()
    {
        var statusCalls = 0;
        JsonElement? startBody = null;
        var tools = CreateRunTools((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/api/projects/proj-1/orchestrations")
            {
                startBody = request.Content!.ReadFromJsonAsync<JsonElement>().GetAwaiter().GetResult();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = JsonContent.Create(new { runId = "run-1" })
                });
            }

            if (request.Method == HttpMethod.Get && path == "/api/runs/run-1")
            {
                statusCalls++;
                if (statusCalls == 1)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(new { run_id = "run-1", status = "in_progress" })
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { run_id = "run-1", status = "merged", result = "ok" })
                });
            }

            if (request.Method == HttpMethod.Get && path == "/api/runs/run-1/files")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new[] { new { path = "README.md", change_type = "modified" } })
                });
            }

            throw new InvalidOperationException($"Unexpected request: {request.Method} {path}");
        });

        var result = await tools.RunTaskAsync(
            "proj-1", "Ship it", workflow_id: null, model_id: null, start_mode: "direct",
            auto_approve_tools: true, autopilot: true,
            timeout_seconds: 5, poll_interval_seconds: 1, ct: CancellationToken.None);

        result.RunId.Should().Be("run-1");
        result.Status.Should().Be("merged");
        result.Artifacts.Should().NotBeNull();
        result.Artifacts![0].GetProperty("path").GetString().Should().Be("README.md");
        startBody!.Value.GetProperty("auto_approve_tools").GetBoolean().Should().BeTrue();
        startBody.Value.GetProperty("autopilot").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task RunTask_AwaitingConfirmation_ReturnsNextAction()
    {
        var tools = CreateRunTools((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/api/projects/proj-1/orchestrations")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = JsonContent.Create(new { runId = "run-2" })
                });
            }

            if (request.Method == HttpMethod.Get && path == "/api/runs/run-2")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        run_id = "run-2",
                        status = "in_progress",
                        coordinator_status = "awaiting_confirmation"
                    })
                });
            }

            throw new InvalidOperationException($"Unexpected request: {request.Method} {path}");
        });

        var result = await tools.RunTaskAsync("proj-1", "Plan it", workflow_id: null, model_id: null, start_mode: "defineOutcome", timeout_seconds: 5, poll_interval_seconds: 1, ct: CancellationToken.None);

        result.Status.Should().Be("awaiting_confirmation");
        result.ReviewPrompt.Should().Contain("coordinator_outcome_spec_get");
    }

    [Fact]
    public async Task RunTask_WorkflowChildWait_DoesNotAdvertiseHumanReview()
    {
        var statusCalls = 0;
        var tools = CreateRunTools((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/api/projects/proj-1/orchestrations")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = JsonContent.Create(new { runId = "run-child-wait" })
                });
            }

            if (request.Method == HttpMethod.Get && path == "/api/runs/run-child-wait")
            {
                statusCalls++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = statusCalls == 1
                        ? JsonContent.Create(new
                        {
                            run_id = "run-child-wait",
                            status = "awaiting_review",
                            pending_request_kind = "workflow_child_work",
                        })
                        : JsonContent.Create(new
                        {
                            run_id = "run-child-wait",
                            status = "merged",
                            result = "joined",
                        })
                });
            }

            if (request.Method == HttpMethod.Get && path == "/api/runs/run-child-wait/files")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(Array.Empty<object>())
                });
            }

            throw new InvalidOperationException($"Unexpected request: {request.Method} {path}");
        });

        var result = await tools.RunTaskAsync(
            "proj-1",
            "Run branches",
            workflow_id: null,
            model_id: null,
            start_mode: "direct",
            timeout_seconds: 5,
            poll_interval_seconds: 1,
            ct: CancellationToken.None);

        statusCalls.Should().BeGreaterThan(1);
        result.Status.Should().Be("merged");
        result.ReviewPrompt.Should().BeNull();
    }

    [Fact]
    public async Task RunTask_WorkflowChildWaitTimeout_GuidesExistingRunWithoutPlanLookup()
    {
        var tools = CreateRunTools((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/api/projects/proj-1/orchestrations")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = JsonContent.Create(new { runId = "composed-parent" })
                });
            if (request.Method == HttpMethod.Get && path == "/api/runs/composed-parent")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        run_id = "composed-parent", status = "awaiting_review",
                        is_coordinator_plan = true, pending_request_kind = "workflow_child_work"
                    })
                });
            throw new InvalidOperationException($"Unexpected request: {request.Method} {path}");
        });

        var result = await tools.RunTaskAsync("proj-1", "Compose",
            timeout_seconds: 1, poll_interval_seconds: 1, ct: CancellationToken.None);

        result.Status.Should().Be("timed_out");
        result.Run!.PendingRequestKind.Should().Be("workflow_child_work");
        result.ReviewPrompt.Should().BeNull();
        result.Hint.Should().Contain("composed-parent").And.Contain("run_status")
            .And.NotContain("run_review").And.NotContain("run_task");
    }

    [Theory]
    [InlineData("waiting", "dispatching", "delegated")]
    [InlineData("ready", "awaiting_assembly", "complete")]
    [InlineData("delivering", "awaiting_assembly", "complete")]
    public async Task RunTask_FanParentWait_ReturnsExistingRunProgressWithoutReview(
        string resumeState, string coordinatorStatus, string planStatus)
    {
        var planReads = 0;
        var tools = CreateRunTools((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/api/projects/proj-1/orchestrations")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = JsonContent.Create(new { runId = "fan-parent" })
                });
            if (request.Method == HttpMethod.Get && path == "/api/runs/fan-parent")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        run_id = "fan-parent", status = "awaiting_review",
                        coordinator_status = coordinatorStatus, is_coordinator_plan = true,
                        step_count = 0, tree_hash = (string?)null,
                        pending_request_kind = (string?)null, sandbox = (object?)null
                    })
                });
            if (request.Method == HttpMethod.Get && path == "/api/runs/fan-parent/work-plan")
            {
                planReads++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        workPlanId = 271, coordinatorRunId = "fan-child",
                        parentRunId = "fan-parent", parentWorkflowNodeId = "split-documents",
                        parentJoinNodeId = "join-documents", parentResumeState = resumeState,
                        status = planStatus,
                        subtasks = new[]
                        {
                            new { status = planStatus == "complete" ? "completed" : "running", childRunId = "branch-a" },
                            new { status = planStatus == "complete" ? "completed" : "pending", childRunId = "branch-b" }
                        }
                    })
                });
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {path}");
        });

        var result = await tools.RunTaskAsync("proj-1", "Fan out", workflow_id: "custom",
            start_mode: "direct", auto_approve_tools: true, autopilot: true,
            timeout_seconds: 1, poll_interval_seconds: 1, ct: CancellationToken.None);

        planReads.Should().BeGreaterThan(0);
        result.RunId.Should().Be("fan-parent");
        result.Status.Should().Be("timed_out");
        result.Run!.Status.Should().Be("awaiting_review");
        result.ReviewPrompt.Should().BeNull();
        result.Hint.Should().Contain("fan-parent").And.Contain("coordinator_work_plan_get")
            .And.Contain("run_status");
        result.Hint.Should().NotContain("run_task").And.NotContain("run_review");
    }

    [Theory]
    [InlineData("delivered", "complete")]
    [InlineData("waiting", "in_review")]
    public async Task RunTask_FanPlanWithCurrentReview_PreservesHumanGate(string resumeState, string planStatus)
    {
        var tools = CreateRunTools((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/api/projects/proj-1/orchestrations")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = JsonContent.Create(new { runId = "fan-parent" })
                });
            if (request.Method == HttpMethod.Get && path == "/api/runs/fan-parent")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        run_id = "fan-parent", status = "awaiting_review",
                        is_coordinator_plan = true, pending_request_kind = (string?)null,
                        tree_hash = "current-review-tree"
                    })
                });
            if (request.Method == HttpMethod.Get && path == "/api/runs/fan-parent/work-plan")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        coordinatorRunId = "fan-child", parentRunId = "fan-parent",
                        parentWorkflowNodeId = "split-documents",
                        parentResumeState = resumeState, status = planStatus
                    })
                });
            throw new InvalidOperationException($"Unexpected request: {request.Method} {path}");
        });

        var result = await tools.RunTaskAsync("proj-1", "Review it",
            timeout_seconds: 1, poll_interval_seconds: 1, ct: CancellationToken.None);

        result.Status.Should().Be("awaiting_review");
        result.ReviewPrompt.Should().Contain("run_review").And.NotContain("run_task");
    }

    [Fact]
    public async Task RunTask_FanWaitThenCurrentHumanReview_ReturnsReviewForSameRun()
    {
        var reads = 0;
        var tools = CreateRunTools((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/api/projects/proj-1/orchestrations")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = JsonContent.Create(new { runId = "fan-parent" })
                });
            if (request.Method == HttpMethod.Get && path == "/api/runs/fan-parent")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        run_id = "fan-parent", status = "awaiting_review",
                        is_coordinator_plan = true,
                        pending_request_kind = ++reads == 1 ? (string?)null : "workflow_review"
                    })
                });
            if (request.Method == HttpMethod.Get && path == "/api/runs/fan-parent/work-plan")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        coordinatorRunId = "fan-child", parentRunId = "fan-parent",
                        parentWorkflowNodeId = "split-documents",
                        parentResumeState = "waiting", status = "delegated"
                    })
                });
            throw new InvalidOperationException($"Unexpected request: {request.Method} {path}");
        });

        var result = await tools.RunTaskAsync("proj-1", "Fan out",
            timeout_seconds: 5, poll_interval_seconds: 1, ct: CancellationToken.None);

        reads.Should().BeGreaterThan(1);
        result.RunId.Should().Be("fan-parent");
        result.Status.Should().Be("awaiting_review");
        result.ReviewPrompt.Should().Contain("run_review").And.NotContain("run_task");
    }

    [Fact]
    public async Task RunTask_OrdinaryHumanReview_DoesNotFetchWorkPlan()
    {
        var tools = CreateRunTools((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/api/projects/proj-1/orchestrations")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = JsonContent.Create(new { runId = "ordinary" })
                });
            if (request.Method == HttpMethod.Get && path == "/api/runs/ordinary")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        run_id = "ordinary", status = "awaiting_review",
                        is_coordinator_plan = false, pending_request_kind = (string?)null
                    })
                });
            throw new InvalidOperationException($"Unexpected request: {request.Method} {path}");
        });

        var result = await tools.RunTaskAsync("proj-1", "Review it", ct: CancellationToken.None);
        result.Status.Should().Be("awaiting_review");
        result.ReviewPrompt.Should().Contain("run_review").And.NotContain("run_task");
    }

    [Fact]
    public async Task RunTask_CoordinatorWithoutChildPlan_PreservesHumanReview()
    {
        var tools = CreateRunTools((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/api/projects/proj-1/orchestrations")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = JsonContent.Create(new { runId = "ordinary-coordinator" })
                });
            if (request.Method == HttpMethod.Get && path == "/api/runs/ordinary-coordinator")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        run_id = "ordinary-coordinator", status = "awaiting_review",
                        is_coordinator_plan = true, pending_request_kind = (string?)null
                    })
                });
            if (request.Method == HttpMethod.Get && path == "/api/runs/ordinary-coordinator/work-plan")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = JsonContent.Create(new { error = "work_plan_not_found" })
                });
            throw new InvalidOperationException($"Unexpected request: {request.Method} {path}");
        });

        var result = await tools.RunTaskAsync("proj-1", "Review it", ct: CancellationToken.None);
        result.Status.Should().Be("awaiting_review");
        result.ReviewPrompt.Should().Contain("run_review").And.NotContain("run_task");
    }

    [Fact]
    public async Task RunTask_FanPlanReadFailure_IsNotPresentedAsReviewOrProgress()
    {
        var tools = CreateRunTools((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/api/projects/proj-1/orchestrations")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = JsonContent.Create(new { runId = "fan-parent" })
                });
            if (request.Method == HttpMethod.Get && path == "/api/runs/fan-parent")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        run_id = "fan-parent", status = "awaiting_review",
                        is_coordinator_plan = true, pending_request_kind = (string?)null
                    })
                });
            if (request.Method == HttpMethod.Get && path == "/api/runs/fan-parent/work-plan")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = JsonContent.Create(new { error = "plan_read_failed" })
                });
            throw new InvalidOperationException($"Unexpected request: {request.Method} {path}");
        });

        var action = () => tools.RunTaskAsync("proj-1", "Fan out", ct: CancellationToken.None);
        var error = await action.Should().ThrowAsync<McpApiException>();
        error.Which.StatusCode.Should().Be(500);
    }

    [Fact]
    public async Task RunTask_Timeout_ReturnsPartialState()
    {
        var tools = CreateRunTools((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/api/projects/proj-1/orchestrations")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = JsonContent.Create(new { runId = "run-3" })
                });
            }

            if (request.Method == HttpMethod.Get && path == "/api/runs/run-3")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { run_id = "run-3", status = "in_progress" })
                });
            }

            throw new InvalidOperationException($"Unexpected request: {request.Method} {path}");
        });

        var result = await tools.RunTaskAsync("proj-1", "Wait", workflow_id: null, model_id: null, start_mode: "direct", timeout_seconds: 1, poll_interval_seconds: 1, ct: CancellationToken.None);

        result.RunId.Should().Be("run-3");
        result.Status.Should().Be("timed_out");
        result.Hint.Should().Contain("run_status");
        // #339: the timed_out response must still carry an artifacts array (empty), not omit it.
        result.Artifacts.Should().NotBeNull();
        result.Artifacts.Should().BeEmpty();
    }

    private static RunTools CreateRunTools(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        var httpClient = new HttpClient(new DelegatingHandlerStub((request, ct) =>
        {
            if (request.Method == HttpMethod.Post
                && request.RequestUri!.AbsolutePath == "/api/ai/execution-context")
            {
                var requestBody = request.Content!.ReadFromJsonAsync<JsonElement>().GetAwaiter().GetResult();
                requestBody.GetProperty("operation").GetString().Should().Be("orchestration");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        ai_required = true,
                        operation = "orchestration",
                        phase = "prepared",
                        execution_key = "opaque-provider-key",
                        expires_at = DateTimeOffset.UtcNow.AddMinutes(5),
                        effective_model_provider = new
                        {
                            state = "resolved",
                            provider_kind = "github_copilot",
                            resolution_scope = "project",
                            provider_scope = "project",
                            model_id = "gpt-5",
                            provider_key = "provider-fingerprint",
                        },
                    }),
                });
            }

            if (request.Method == HttpMethod.Post
                && request.RequestUri!.AbsolutePath.EndsWith("/orchestrations", StringComparison.Ordinal))
            {
                request.Headers.GetValues("If-Model-Provider-Key").Should().Equal("opaque-provider-key");
            }

            return handler(request, ct);
        }))
        {
            BaseAddress = new Uri("http://localhost/")
        };

        var apiClient = new AgentweaverApiClient(httpClient, new McpConfig("http://localhost", "test-api-key"));
        return new RunTools(apiClient);
    }

    private sealed class DelegatingHandlerStub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
