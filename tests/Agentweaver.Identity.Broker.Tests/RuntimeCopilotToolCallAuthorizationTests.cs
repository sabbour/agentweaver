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
        var calls = new ConcurrentQueue<RuntimeActionRequest>();
        var authorization = new RuntimeCopilotToolCallAuthorization(
            registration, sessionId,
            (request, token) =>
            {
                token.ThrowIfCancellationRequested();
                calls.Enqueue(request);
                return Task.CompletedTask;
            },
            ActiveTurn());
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
        Assert.False(Assert.Single(calls).IsToolInvocation);

        var request = PermissionRequest(toolName, callId);
        var permissionInvocation = CreatePermissionInvocation(sessionId);
        var decision = await authorization.OnPermissionRequestAsync(request, permissionInvocation);
        Assert.Equal(PermissionDecision.ApproveOnce().Kind, decision.Kind);
        var shellDecision = await authorization.OnPermissionRequestAsync(
            SecondaryPermission(toolName, callId), permissionInvocation);
        Assert.Equal(PermissionDecision.ApproveOnce().Kind, shellDecision.Kind);
        var repeatedDecision = await authorization.OnPermissionRequestAsync(request, permissionInvocation);
        Assert.Equal(PermissionDecision.ApproveOnce().Kind, repeatedDecision.Kind);
        var repeatedSecondary = await authorization.OnPermissionRequestAsync(
            SecondaryPermission(toolName, callId), permissionInvocation);
        Assert.Equal(PermissionDecision.ApproveOnce().Kind, repeatedSecondary.Kind);

        var invocations = calls.Where(call => call.IsToolInvocation).ToArray();
        Assert.Equal(4, invocations.Length);
        Assert.All(invocations, call => Assert.Equal(invocations[0], call));
        var first = invocations[0];
        Assert.Equal(expectedAction, first.ActionId);
        Assert.NotEqual(Guid.Empty, first.EventId);
        Assert.Equal(RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(request)), first.InputHash);
        var secondary = calls.Skip(1).Where(call => !call.IsToolInvocation).ToArray();
        Assert.Equal(2, secondary.Length);
        Assert.Equal(secondary[0], secondary[1]);
        Assert.NotEqual(first.EventId, secondary[0].EventId);
        Assert.All(calls, call =>
        {
            Assert.Equal(expectedAction, call.ActionId);
            Assert.Equal(registration.RuntimeInstanceId, call.RuntimeInstanceId);
            Assert.Equal(registration.Revision, call.RegistrationRevision);
            Assert.Equal(registration.Binding.ExecutionFence, call.ExecutionFence);
            Assert.Null(call.DispatchId);
        });
    }

    [Fact]
    public async Task ConcurrentPermissionCallbacksRemainBoundToTheirToolCallIds()
    {
        var registration = RuntimeCopilotSessionTests.Registration();
        var sessionId = RuntimeContractValidation.NativeSessionId(registration.Binding);
        var calls = new ConcurrentDictionary<Guid, string>();
        var authorization = new RuntimeCopilotToolCallAuthorization(
            registration, sessionId,
            (request, _) =>
            {
                if (request.IsToolInvocation)
                    Assert.True(calls.TryAdd(request.EventId, request.ActionId));
                return Task.CompletedTask;
            },
            ActiveTurn());

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
            (request, _) =>
            {
                if (!request.IsToolInvocation)
                    return Task.CompletedTask;
                calls++;
                throw new RuntimeAuthorizationException("runtime_action_denied");
            },
            ActiveTurn());
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

    [Fact]
    public async Task RepeatedInvocationCannotReuseAllowAfterAuthorityRevocation()
    {
        var checks = 0;
        var authorization = Authorization((_, _) =>
        {
            if (++checks == 2)
                throw new RuntimeAuthorizationException("runtime_action_denied");
            return Task.CompletedTask;
        });
        var request = PermissionRequest("view", "native-call-1");
        Approved(await authorization.OnPermissionRequestAsync(request, CurrentInvocation()));
        Rejected(await authorization.OnPermissionRequestAsync(request, CurrentInvocation()));
        Assert.Equal(2, checks);
    }

    [Fact]
    public async Task SecondaryPermissionRequiresItsOwnFreshPolicyAfterInvocationReplay()
    {
        var requests = new List<RuntimeActionRequest>();
        var authorization = Authorization((request, _) =>
        {
            requests.Add(request);
            if (!request.IsToolInvocation)
                throw new RuntimeAuthorizationException("runtime_action_denied");
            return Task.CompletedTask;
        });
        Approved(await authorization.OnPermissionRequestAsync(
            PermissionRequest("view", "native-call-1"), CurrentInvocation()));
        Rejected(await authorization.OnPermissionRequestAsync(
            SecondaryPermission("view", "native-call-1"), CurrentInvocation()));
        Assert.Equal(new[] { true, true, false }, requests.Select(request => request.IsToolInvocation));
        Assert.Equal(requests[0], requests[1]);
        Assert.NotEqual(requests[1].EventId, requests[2].EventId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledTurnCannotReuseSuccessfulInvocationOrSecondaryPermission(bool secondary)
    {
        using var cancellation = new CancellationTokenSource();
        var turns = new RuntimeCopilotTurnObserver();
        _ = turns.Begin(cancellation.Token, Guid.NewGuid());
        var checks = 0;
        var authorization = Authorization((_, _) => { checks++; return Task.CompletedTask; }, turns);
        var invocation = PermissionRequest("view", "native-call-1");
        Approved(await authorization.OnPermissionRequestAsync(invocation, CurrentInvocation()));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            authorization.OnPermissionRequestAsync(
                secondary ? SecondaryPermission("view", "native-call-1") : invocation,
                CurrentInvocation()));
        Assert.Equal(1, checks);
    }

    [Fact]
    public async Task AdmissionWaitCannotApproveAfterItsTurnHasEnded()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var turns = ActiveTurn();
        var authorization = Authorization(async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
        }, turns);
        var pending = authorization.OnPermissionRequestAsync(
            PermissionRequest("view", "native-call-1"), CurrentInvocation());
        await entered.Task;
        turns.End();
        _ = turns.Begin(CancellationToken.None, Guid.NewGuid());
        release.TrySetResult();
        Rejected(await pending);
    }

    [Fact]
    public async Task TurnAndRegistrationScopeCannotRecoverAnEarlierSecondaryPermission()
    {
        var requests = new List<RuntimeActionRequest>();
        var turns = ActiveTurn();
        var authorization = Authorization((request, _) => { requests.Add(request); return Task.CompletedTask; }, turns);
        Approved(await authorization.OnPermissionRequestAsync(
            PermissionRequest("view", "native-call-1"), CurrentInvocation()));
        turns.End();
        authorization.Clear();
        _ = turns.Begin(CancellationToken.None, Guid.NewGuid());
        Rejected(await authorization.OnPermissionRequestAsync(
            SecondaryPermission("view", "native-call-1"), CurrentInvocation()));
        Approved(await authorization.OnPermissionRequestAsync(
            PermissionRequest("view", "native-call-1"), CurrentInvocation()));
        var registration = RuntimeCopilotSessionTests.Registration() with { Revision = 2 };
        var recovered = new RuntimeCopilotToolCallAuthorization(
            registration, SessionId(), (request, _) => { requests.Add(request); return Task.CompletedTask; }, turns);
        Rejected(await recovered.OnPermissionRequestAsync(
            SecondaryPermission("view", "native-call-1"), CurrentInvocation()));
        Approved(await recovered.OnPermissionRequestAsync(
            PermissionRequest("view", "native-call-1"), CurrentInvocation()));
        Assert.Equal(3, requests.Select(request => request.EventId).Distinct().Count());
    }

    private static RuntimeCopilotToolCallAuthorization Authorization(
        Func<RuntimeActionRequest, CancellationToken, Task> authority, RuntimeCopilotTurnObserver? turns = null) =>
        new(RuntimeCopilotSessionTests.Registration(), SessionId(), authority, turns ?? ActiveTurn());

    private static RuntimeCopilotTurnObserver ActiveTurn()
    {
        var turns = new RuntimeCopilotTurnObserver();
        _ = turns.Begin(CancellationToken.None, Guid.NewGuid());
        return turns;
    }

    private static string SessionId() =>
        RuntimeContractValidation.NativeSessionId(RuntimeCopilotSessionTests.Registration().Binding);

    private static PermissionInvocation CurrentInvocation() => CreatePermissionInvocation(SessionId());

    private static void Approved(PermissionDecision decision) =>
        Assert.Equal(PermissionDecision.ApproveOnce().Kind, decision.Kind);

    private static void Rejected(PermissionDecision decision) =>
        Assert.Equal(PermissionDecision.Reject("denied").Kind, decision.Kind);

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
