using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.AgentRuntime;
using Agentweaver.Identity;
using Xunit;

#pragma warning disable GHCP001 // Permission callback contracts are pinned to SDK 1.0.18.
namespace Agentweaver.Identity.Broker.Tests;

public sealed class RuntimeActionHttpClientTests
{
    [Fact]
    public async Task NativePermissionAuthorizationIsBoundToTheHookToolCall()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var external = new ControlledCopilotRuntime
        {
            ExpectedAvailableToolsCount = 7,
            BeforeTurnResponse = token => release.Task.WaitAsync(token)
        };
        var registration = RuntimeCopilotSessionTests.Registration();
        var actions = new List<RuntimeActionRequest>();
        await using var session = await RuntimeCopilotSessionTests.Factory(external).CreateAsync(
            registration, registration.Binding.ModelSelectionReference!,
            RuntimeCopilotSessionTests.SdkCredential(), _ => Task.CompletedTask, timeout.Token,
            requireActionAuthority: (request, token) =>
            {
                token.ThrowIfCancellationRequested();
                Assert.Equal("tool.read", request.ActionId);
                Assert.NotEqual(Guid.Empty, request.EventId);
                Assert.Equal(64, request.InputHash.Length);
                actions.Add(request);
                return Task.CompletedTask;
            });
        var turn = session.SendTurnAsync("A bounded user request.", timeout.Token, Guid.NewGuid());
        await external.TurnReceived.Task.WaitAsync(timeout.Token);
        const string toolCallId = "read-call-1";
        try
        {
            var response = await external.InvokeSdkCallbackAsync("hooks.invoke", new
            {
                sessionId = session.Facts.SdkSessionId, hookType = "preToolUse",
                input = new
                {
                    sessionId = session.Facts.SdkSessionId, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    workingDirectory = "/workspace", toolName = "view", toolArgs = new { path = "readme.txt" }
                }

                #pragma warning restore GHCP001
            }, timeout.Token);
            Assert.Equal("ask", response.GetProperty("output").GetProperty("permissionDecision").GetString());
            Assert.False(Assert.Single(actions).IsToolInvocation);
            await external.EmitPermissionRequestAsync(new
            {
                requestId = "read-hook-permission",
                resolvedByHook = false,
                permissionRequest = new
                {
                    kind = "hook", toolName = "view", toolCallId,
                    toolArgs = new { path = "readme.txt" }
                }
            });
            await external.PermissionDecisionHandled.Task.WaitAsync(timeout.Token);
            var action = Assert.Single(actions, request => request.IsToolInvocation);
            Assert.Equal("tool.read", action.ActionId);
            Assert.NotEqual(Guid.Empty, action.EventId);
        }
        finally
        {
            release.TrySetResult();
        }
        Assert.Equal(external.AssistantResponse, await turn);
    }

    [Fact]
    public async Task LostAuthorizationResponseRetriesTheSameInvocationButIdenticalNewArgsGetANewEvent()
    {
        var requests = new List<RuntimeActionRequest>();
        RuntimeActionAdmission? issued = null;
        using var http = new HttpClient(new Handler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/authorize", StringComparison.Ordinal))
            {
                var action = await request.Content!.ReadFromJsonAsync<RuntimeActionRequest>();
                Assert.NotNull(action);
                Assert.True(action.IsToolInvocation);
                requests.Add(action);
                issued = new(1, action, action.EventId.ToString("N"), "1", action.EventId,
                    PolicyEvaluationOutcome.Allow, PolicyEvaluationReasonCode.Allowed);
                if (requests.Count == 1)
                    throw new HttpRequestException("Lost authorization response after owner commit.");
            }
            return Response(request, issued!);
        }));
        var actor = new RuntimeActorAuthorization(
            new SecretCredential("protected-actor", DateTimeOffset.UtcNow.AddMinutes(1)), "tenant");
        var actions = new RuntimeActionHttpClient(http, new("https://orchestrator.test/"), actor);
        var registration = RuntimeCopilotSessionTests.Registration();
        await actions.RequireAsync(registration, "tool.read", "same args"u8.ToArray(),
            _ => Task.CompletedTask, default, isToolInvocation: true);
        await actions.RequireAsync(registration, "tool.read", "same args"u8.ToArray(),
            _ => Task.CompletedTask, default, isToolInvocation: true);
        Assert.Equal(3, requests.Count);
        Assert.Equal(requests[0], requests[1]);
        Assert.NotEqual(requests[1].EventId, requests[2].EventId);
        Assert.Equal(requests[1].InputHash, requests[2].InputHash);
    }

    [Theory]
    [InlineData("allow")]
    [InlineData("deny")]
    [InlineData("changed")]
    [InlineData("revoked")]
    [InlineData("wrong-request")]
    public async Task DispatchRequiresExactCurrentOwnerAdmissionAndVerifyWithNoCredentialOrPromptBody(string result)
    {
        var checks = 0;
        var methods = new List<string>();
        var registration = RuntimeCopilotSessionTests.Registration();
        var eventId = Guid.NewGuid();
        Guid? costTurnId = result == "allow" ? Guid.NewGuid() : null;
        RuntimeActionAdmission? issued = null;
        using var client = new HttpClient(new Handler(async request =>
        {
            Assert.Equal("protected-actor", request.Headers.Authorization?.Parameter);
            Assert.Equal("tenant", Assert.Single(request.Headers.GetValues("X-Agentweaver-Tenant")));
            var json = await request.Content!.ReadAsStringAsync();
            Assert.DoesNotContain("protected-actor", json);
            Assert.DoesNotContain("protected prompt", json);
            methods.Add(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath.EndsWith("/authorize", StringComparison.Ordinal))
            {
                var action = JsonSerializer.Deserialize<RuntimeActionRequest>(
                    json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                Assert.Equal(RuntimeContractValidation.Hash("protected prompt"u8), action.InputHash);
                Assert.Equal(eventId, action.EventId);
                Assert.Equal(costTurnId, action.CostTurnId);
                Assert.Equal(costTurnId is not null, json.Contains("\"costTurnId\"", StringComparison.Ordinal));
                issued = new(1, result == "wrong-request" ? action with { InputHash = new string('b', 64) } : action,
                    action.EventId.ToString("N"), "1", action.EventId,
                    result == "deny" ? PolicyEvaluationOutcome.Deny : PolicyEvaluationOutcome.Allow,
                    result == "deny" ? PolicyEvaluationReasonCode.DefaultDeny : PolicyEvaluationReasonCode.Allowed);
                return Response(request, issued);
            }
            if (result == "revoked")
            {
                var revoked = Response(request, issued!);
                revoked.StatusCode = HttpStatusCode.Forbidden;
                return revoked;
            }
            return Response(request, result == "changed" ? issued! with { GrantRevision = "2" } : issued!);
        }));
        var actor = new RuntimeActorAuthorization(
            new SecretCredential("protected-actor", DateTimeOffset.UtcNow.AddMinutes(1)), "tenant");
        var actions = new RuntimeActionHttpClient(client, new("https://orchestrator.test/"), actor);
        Task Current(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            checks++;
            return Task.CompletedTask;
        }
        var dispatch = actions.RequireAsync(
            registration, "model.turn", eventId, "protected prompt"u8.ToArray(), Current, default, costTurnId);
        if (result == "allow")
        {
            await dispatch;
            Assert.Equal(3, checks);
        }
        else
            await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => dispatch);
        Assert.Equal(result is "deny" or "wrong-request" ? 1 : 2, methods.Count);
    }

    [Theory]
    [InlineData("view")]
    [InlineData("edit")]
    [InlineData("bash")]
    [InlineData("read_bash")]
    [InlineData("write_bash")]
    [InlineData("stop_bash")]
    [InlineData("unregistered-tool")]
    public async Task ActualNativePreToolCallbackAsksBeforePermissionAuthorization(string tool)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var external = new ControlledCopilotRuntime
        {
            ExpectedAvailableToolsCount = 7,
            BeforeTurnResponse = token => release.Task.WaitAsync(token)
        };
        var registration = RuntimeCopilotSessionTests.Registration();
        var actions = new List<RuntimeActionRequest>();
        await using var session = await RuntimeCopilotSessionTests.Factory(external).CreateAsync(
            registration, registration.Binding.ModelSelectionReference!,
            RuntimeCopilotSessionTests.SdkCredential(), _ => Task.CompletedTask, timeout.Token,
            requireActionAuthority: (request, token) =>
            {
                token.ThrowIfCancellationRequested();
                Assert.Equal(64, request.InputHash.Length);
                actions.Add(request);
                return Task.CompletedTask;
            });
        var turn = session.SendTurnAsync("A bounded user request.", timeout.Token, Guid.NewGuid());
        await external.TurnReceived.Task.WaitAsync(timeout.Token);
        var toolCallId = "test-tool-call-" + tool;
        var response = await external.InvokeSdkCallbackAsync("hooks.invoke", new
        {
            sessionId = session.Facts.SdkSessionId, hookType = "preToolUse",
            input = new
            {
                sessionId = session.Facts.SdkSessionId, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                workingDirectory = "/workspace", toolName = tool, toolArgs = new { command = "bounded" }
            }
        }, timeout.Token);
        Assert.Equal(tool == "unregistered-tool" ? "deny" : "ask",
            response.GetProperty("output").GetProperty("permissionDecision").GetString());
        if (tool == "unregistered-tool")
            Assert.Empty(actions);
        else
            Assert.False(Assert.Single(actions).IsToolInvocation);
        release.TrySetResult();
        Assert.Equal(external.AssistantResponse, await turn);
    }

    [Fact]
    public async Task ActualNativePermissionCallbackAuthorizesItsExactActionAndToolCall()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var external = new ControlledCopilotRuntime
        {
            ExpectedAvailableToolsCount = 7,
            BeforeTurnResponse = token => release.Task.WaitAsync(token)
        };
        var registration = RuntimeCopilotSessionTests.Registration();
        var authorized = new TaskCompletionSource<RuntimeActionRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = await RuntimeCopilotSessionTests.Factory(external).CreateAsync(
            registration, registration.Binding.ModelSelectionReference!,
            RuntimeCopilotSessionTests.SdkCredential(), _ => Task.CompletedTask, timeout.Token,
            requireActionAuthority: (request, token) =>
            {
                token.ThrowIfCancellationRequested();
                authorized.TrySetResult(request);
                return Task.CompletedTask;
            });
        var turn = session.SendTurnAsync("A bounded user request.", timeout.Token, Guid.NewGuid());
        await external.TurnReceived.Task.WaitAsync(timeout.Token);

        const string toolCallId = "native-call-42";
        var requestId = Guid.NewGuid().ToString("N");
        await external.EmitPermissionRequestAsync(new
        {
            requestId,
            resolvedByHook = false,
            permissionRequest = new
            {
                kind = "hook",
                toolName = "bash",
                toolCallId,
                toolArgs = new { command = "bounded" }
            }
        });

        var nativeAction = await authorized.Task.WaitAsync(timeout.Token);
        Assert.Equal("exec.shell", nativeAction.ActionId);
        Assert.NotEqual(Guid.Empty, nativeAction.EventId);
        GitHub.Copilot.PermissionRequest expected = JsonSerializer.Deserialize<GitHub.Copilot.PermissionRequestHook>(
            JsonSerializer.Serialize(new
            {
                kind = "hook", toolName = "bash", toolCallId,
                toolArgs = new { command = "bounded" }
            }), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(expected)),
            nativeAction.InputHash);
        Assert.True(nativeAction.IsToolInvocation);

        await external.PermissionDecisionHandled.Task.WaitAsync(timeout.Token);
        var permissionDecision = Assert.Single(external.Requests,
            request => request.Method == "session.permissions.handlePendingPermissionRequest");
        Assert.Equal(session.Facts.SdkSessionId,
            permissionDecision.Parameters.GetProperty("sessionId").GetString());
        Assert.Equal(requestId, permissionDecision.Parameters.GetProperty("requestId").GetString());
        Assert.Equal("approve-once",
            permissionDecision.Parameters.GetProperty("result").GetProperty("kind").GetString());

        release.TrySetResult();
        Assert.Equal(external.AssistantResponse, await turn);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModelTurnSendsContentOnlyAfterTheOwnerRequiresGuardrailsContent(bool contentRequired)
    {
        // The stub owner answers the first admission with 428 when contentRequired is true, which the client
        // reads as "content required". The hash-only call must never contain the prompt text.
        var paths = new List<string>();
        RuntimeActionContentRequest? sent = null;
        RuntimeActionAdmission? issued = null;
        using var client = new HttpClient(new Handler(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            paths.Add(path);
            var json = await request.Content!.ReadAsStringAsync();
            if (path.EndsWith("/authorize", StringComparison.Ordinal))
            {
                Assert.DoesNotContain("protected prompt", json);
                var action = JsonSerializer.Deserialize<RuntimeActionRequest>(
                    json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                issued = new(1, action, action.EventId.ToString("N"), "1", action.EventId,
                    PolicyEvaluationOutcome.Allow, PolicyEvaluationReasonCode.Allowed);
                if (!contentRequired)
                    return Response(request, issued);
                var required = Response(request, new { code = "content_required" });
                required.StatusCode = HttpStatusCode.PreconditionRequired;
                return required;
            }
            if (path.EndsWith("/authorize-content", StringComparison.Ordinal))
            {
                sent = JsonSerializer.Deserialize<RuntimeActionContentRequest>(
                    json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                return Response(request, issued!);
            }
            return Response(request, issued!);
        }));
        var actor = new RuntimeActorAuthorization(
            new SecretCredential("protected-actor", DateTimeOffset.UtcNow.AddMinutes(1)), "tenant");
        var actions = new RuntimeActionHttpClient(client, new("https://orchestrator.test/"), actor);
        await actions.RequireAsync(RuntimeCopilotSessionTests.Registration(), "model.turn", Guid.NewGuid(),
            "protected prompt"u8.ToArray(), _ => Task.CompletedTask, default, null);
        // Expected calls: authorize, then authorize-content only when required, then the follow-up call that
        // happens after admission in both cases.
        if (contentRequired)
        {
            Assert.Equal(3, paths.Count);
            Assert.EndsWith("/authorize-content", paths[1], StringComparison.Ordinal);
            Assert.NotNull(sent);
            Assert.Equal("protected prompt", sent.Content);
            Assert.Equal(issued!.Request, sent.Request);
        }
        else
        {
            Assert.Equal(2, paths.Count);
            Assert.DoesNotContain(paths, path => path.EndsWith("/authorize-content", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task ContentRequiredForAnActionWithoutContentFailsClosed()
    {
        // A tool action has no content form, so a 428 from the owner must fail without a second call.
        var paths = new List<string>();
        using var client = new HttpClient(new Handler(request =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            var required = Response(request, new { code = "content_required" });
            required.StatusCode = HttpStatusCode.PreconditionRequired;
            return Task.FromResult(required);
        }));
        var actor = new RuntimeActorAuthorization(
            new SecretCredential("protected-actor", DateTimeOffset.UtcNow.AddMinutes(1)), "tenant");
        var actions = new RuntimeActionHttpClient(client, new("https://orchestrator.test/"), actor);
        var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => actions.RequireAsync(
            RuntimeCopilotSessionTests.Registration(), "tool.read", "args"u8.ToArray(),
            _ => Task.CompletedTask, default, isToolInvocation: true));
        Assert.Equal("runtime_guardrails_content_unsupported", failure.Code);
        Assert.Single(paths);
    }

    [Fact]
    public void ContentValidationRequiresTheExactModelTurnText()
    {
        var registration = RuntimeCopilotSessionTests.Registration();
        var hash = RuntimeContractValidation.Hash("bounded text"u8);
        var request = new RuntimeActionRequest(1, registration.RuntimeInstanceId, registration.Revision,
            registration.Binding.ExecutionFence, Guid.NewGuid(), "model.turn", hash);
        Assert.Equal("bounded text"u8.ToArray(),
            RuntimeActionContract.ValidateContent(new(request, "bounded text")));
        // Rejected: text that doesn't match the hash, a non-model action, oversized text even with a matching
        // hash, and empty text.
        Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeActionContract.ValidateContent(new(request, "changed text")));
        Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeActionContract.ValidateContent(new(request with { ActionId = "tool.read" }, "bounded text")));
        var oversized = new string('a', RuntimeActionContract.MaximumContentBytes + 1);
        Assert.Throws<RuntimeAuthorizationException>(() => RuntimeActionContract.ValidateContent(
            new(request with { InputHash = RuntimeContractValidation.Hash(System.Text.Encoding.UTF8.GetBytes(oversized)) },
                oversized)));
        Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeActionContract.ValidateContent(new(request, "")));
    }

    private static HttpResponseMessage Response<T>(HttpRequestMessage request, T body) => new(HttpStatusCode.OK)
    {
        RequestMessage = request, Content = JsonContent.Create(body),
        Headers = { CacheControl = new CacheControlHeaderValue { NoStore = true } }
    };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            send(request);
    }
}
