using System.Collections.Concurrent;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.AgentRuntime;
using Agentweaver.Identity;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Xunit;

#pragma warning disable GHCP001 // Permission callback test types are pinned to SDK 1.0.18.
namespace Agentweaver.Identity.Broker.Tests;

public sealed class RuntimeCopilotToolCallAuthorizationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("view", "tool.read")]
    [InlineData("create", "tool.write")]
    [InlineData("edit", "tool.write")]
    [InlineData("bash", "exec.shell")]
    [InlineData("read_bash", "exec.read")]
    [InlineData("write_bash", "exec.write")]
    [InlineData("stop_bash", "exec.stop")]
    public async Task PermissionAuthorizationUsesExactPreToolActionAndStableToolCallId(
        string toolName, string expectedAction)
    {
        var registration = RuntimeCopilotSessionTests.Registration();
        var sessionId = RuntimeContractValidation.NativeSessionId(registration.Binding);
        var callId = "native-call-" + toolName;
        var calls = new ConcurrentQueue<(string Action, Guid EventId, byte[] Input)>();
        var authorization = new RuntimeCopilotToolCallAuthorization(
            registration, sessionId,
            (action, eventId, input, token) =>
            {
                token.ThrowIfCancellationRequested();
                calls.Enqueue((action, eventId, input.ToArray()));
                return Task.CompletedTask;
            },
            () => CancellationToken.None);
        var input = Create<PreToolUseHookInput>(new
        {
            sessionId,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            workingDirectory = "/workspace",
            toolName,
            toolArgs = new { command = "bounded" }
        });
        var hookInvocation = CreateHookInvocation(sessionId);

        var hook = await authorization.OnPreToolUseAsync(input, hookInvocation);
        Assert.Equal("ask", hook?.PermissionDecision);
        Assert.Empty(calls);

        var request = PermissionRequest(toolName, callId);
        var permissionInvocation = CreatePermissionInvocation(sessionId);
        var decision = await authorization.OnPermissionRequestAsync(request, permissionInvocation);
        Assert.Equal(PermissionDecision.ApproveOnce().Kind, decision.Kind);
        var shellDecision = await authorization.OnPermissionRequestAsync(
            SecondaryPermission(toolName, callId), permissionInvocation);
        Assert.Equal(PermissionDecision.ApproveOnce().Kind, shellDecision.Kind);
        var repeatedDecision = await authorization.OnPermissionRequestAsync(request, permissionInvocation);
        Assert.Equal(PermissionDecision.ApproveOnce().Kind, repeatedDecision.Kind);

        var first = Assert.Single(calls);
        Assert.Equal(expectedAction, first.Action);
        Assert.NotEqual(Guid.Empty, first.EventId);
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(request), first.Input);
    }

    [Fact]
    public async Task ConcurrentPermissionCallbacksRemainBoundToTheirToolCallIds()
    {
        var registration = RuntimeCopilotSessionTests.Registration();
        var sessionId = RuntimeContractValidation.NativeSessionId(registration.Binding);
        var calls = new ConcurrentDictionary<Guid, string>();
        var authorization = new RuntimeCopilotToolCallAuthorization(
            registration, sessionId,
            (action, eventId, _, _) =>
            {
                Assert.True(calls.TryAdd(eventId, action));
                return Task.CompletedTask;
            },
            () => CancellationToken.None);

        var readInput = Create<PreToolUseHookInput>(HookInput(sessionId, "read_bash"));
        var writeInput = Create<PreToolUseHookInput>(HookInput(sessionId, "write_bash"));
        var hookInvocation = CreateHookInvocation(sessionId);
        var permissionInvocation = CreatePermissionInvocation(sessionId);
        Assert.Equal("ask", (await authorization.OnPreToolUseAsync(readInput, hookInvocation))?.PermissionDecision);
        Assert.Equal("ask", (await authorization.OnPreToolUseAsync(writeInput, hookInvocation))?.PermissionDecision);

        var readRequest = PermissionRequest("read_bash", "call-read");
        var writeRequest = PermissionRequest("write_bash", "call-write");
        var decisions = await Task.WhenAll(
            authorization.OnPermissionRequestAsync(readRequest, permissionInvocation),
            authorization.OnPermissionRequestAsync(writeRequest, permissionInvocation));

        Assert.All(decisions, decision => Assert.Equal(PermissionDecision.ApproveOnce().Kind, decision.Kind));
        Assert.Equal(2, calls.Count);
        Assert.Contains(calls, call => call.Value == "exec.read");
        Assert.Contains(calls, call => call.Value == "exec.write");
    }

    [Fact]
    public async Task MissingMismatchedOrDeniedPermissionCallsFailClosed()
    {
        var registration = RuntimeCopilotSessionTests.Registration();
        var sessionId = RuntimeContractValidation.NativeSessionId(registration.Binding);
        var calls = 0;
        var authorization = new RuntimeCopilotToolCallAuthorization(
            registration, sessionId,
            (_, _, _, _) =>
            {
                calls++;
                throw new RuntimeAuthorizationException("runtime_action_denied");
            },
            () => CancellationToken.None);
        var hookInvocation = CreateHookInvocation(sessionId);
        var permissionInvocation = CreatePermissionInvocation(sessionId);
        var hook = await authorization.OnPreToolUseAsync(
            Create<PreToolUseHookInput>(HookInput(sessionId, "read_bash")), hookInvocation);
        Assert.Equal("ask", hook?.PermissionDecision);

        var unregisteredPermission = await authorization.OnPermissionRequestAsync(
            PermissionRequest("unregistered-tool", "different-call"), permissionInvocation);
        Assert.Equal(PermissionDecision.Reject("runtime_tool_authority_unavailable").Kind, unregisteredPermission.Kind);
        Assert.Equal(0, calls);

        var unattributedPermission = await authorization.OnPermissionRequestAsync(
            ShellPermissionRequest("call-1"), permissionInvocation);
        Assert.Equal(PermissionDecision.Reject("runtime_tool_authority_unavailable").Kind, unattributedPermission.Kind);
        Assert.Equal(0, calls);

        var denied = await authorization.OnPermissionRequestAsync(
            PermissionRequest("read_bash", "call-1"), permissionInvocation);
        Assert.Equal(PermissionDecision.Reject("runtime_action_denied").Kind, denied.Kind);
        Assert.Equal(1, calls);

        var unregistered = await authorization.OnPreToolUseAsync(
            Create<PreToolUseHookInput>(HookInput(sessionId, "unregistered-tool")), hookInvocation);
        Assert.Equal("deny", unregistered?.PermissionDecision);

        var deniedShellWithoutHook = await authorization.OnPermissionRequestAsync(
            ShellPermissionRequest("no-hook-call"), permissionInvocation);
        Assert.Equal(PermissionDecision.Reject("runtime_tool_authority_unavailable").Kind, deniedShellWithoutHook.Kind);
        Assert.Equal(1, calls);
    }

    private static object HookInput(string sessionId, string toolName) => new
    {
        sessionId,
        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        workingDirectory = "/workspace",
        toolName,
        toolArgs = new { command = "bounded" }
    };

    private static HookInvocation CreateHookInvocation(string sessionId) =>
    Create<HookInvocation>(new { sessionId });

    private static PermissionInvocation CreatePermissionInvocation(string sessionId) =>
    Create<PermissionInvocation>(new { sessionId });

    private static PermissionRequest PermissionRequest(string toolName, string callId) =>
    Create<PermissionRequestHook>(new
    {
        kind = "hook", toolName, toolCallId = callId, toolArgs = new { command = "bounded" }
    });

    private static PermissionRequest ShellPermissionRequest(string callId) =>
    Create<PermissionRequestShell>(new
    {
        kind = "shell", toolCallId = callId, requestSandboxBypass = false,
        managedApprovalRequired = false, fullCommandText = "echo bounded",
        canOfferSessionApproval = false, hasWriteFileRedirection = false,
        commands = new[] { new { identifier = "echo", readOnly = true } },
        possiblePaths = Array.Empty<string>(), possibleUrls = Array.Empty<object>(),
        intention = "run a bounded command"
    });

    private static PermissionRequest SecondaryPermission(string toolName, string callId) => toolName switch
    {
    "view" => Create<PermissionRequestRead>(new
    {
        kind = "read", toolCallId = callId, requestSandboxBypass = false,
        managedApprovalRequired = false, path = "/workspace/input.txt",
        intention = "read a bounded input"
    }),
    "create" or "edit" => Create<PermissionRequestWrite>(new
    {
        kind = "write", toolCallId = callId, requestSandboxBypass = false,
        managedApprovalRequired = false, fileName = "/workspace/output.txt", newFileContents = "bounded",
        canOfferSessionApproval = false, diff = "", intention = "write a bounded output"
    }),
    _ => ShellPermissionRequest(callId)
    };

    private static T Create<T>(object value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value), JsonOptions)
        ?? throw new InvalidOperationException($"Could not create {typeof(T).Name} test input.");
}

#pragma warning restore GHCP001
