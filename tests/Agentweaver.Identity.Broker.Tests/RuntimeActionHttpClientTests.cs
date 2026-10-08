using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.AgentRuntime;
using Agentweaver.Identity;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class RuntimeActionHttpClientTests
{
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
            registration, "model.turn", "protected prompt"u8.ToArray(), Current, default);
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
    [InlineData("view", "tool.read", false)]
    [InlineData("edit", "tool.write", false)]
    [InlineData("bash", "exec.shell", false)]
    [InlineData("bash", "exec.shell", true)]
    [InlineData("unregistered-tool", null, false)]
    public async Task ActualNativePreToolCallbackRequiresCurrentAuthorityDuringAnActiveTurn(
        string tool, string? expectedAction, bool deny)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var external = new ControlledCopilotRuntime
        {
            ExpectedAvailableToolsCount = 7,
            BeforeTurnResponse = token => release.Task.WaitAsync(token)
        };
        var registration = RuntimeCopilotSessionTests.Registration();
        var actions = new List<string>();
        await using var session = await RuntimeCopilotSessionTests.Factory(external).CreateAsync(
            registration, registration.Binding.ModelSelectionReference!,
            RuntimeCopilotSessionTests.SdkCredential(), _ => Task.CompletedTask, timeout.Token,
            requireActionAuthority: (action, input, token) =>
            {
                token.ThrowIfCancellationRequested();
                Assert.NotEmpty(input.ToArray());
                actions.Add(action);
                if (deny)
                    throw new RuntimeAuthorizationException("runtime_action_denied");
                return Task.CompletedTask;
            });
        var turn = session.SendTurnAsync("A bounded user request.", timeout.Token);
        await external.TurnReceived.Task.WaitAsync(timeout.Token);
        var response = await external.InvokeSdkCallbackAsync("hooks.invoke", new
        {
            sessionId = session.Facts.SdkSessionId, hookType = "preToolUse",
            input = new
            {
                sessionId = session.Facts.SdkSessionId, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                workingDirectory = "/workspace", toolName = tool, toolArgs = new { command = "bounded" }
            }
        }, timeout.Token);
        Assert.Equal(deny || expectedAction is null ? "deny" : "ask",
            response.GetProperty("output").GetProperty("permissionDecision").GetString());
        Assert.Equal(expectedAction is null ? [] : new[] { expectedAction }, actions);
        release.TrySetResult();
        Assert.Equal(external.AssistantResponse, await turn);
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
