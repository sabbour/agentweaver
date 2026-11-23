using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Identity;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;

#pragma warning disable GHCP001 // Permission callbacks are pinned to SDK 1.0.18.
namespace Agentweaver.AgentRuntime;

/// <summary>
/// Converts SDK tool and permission callbacks into deterministic, turn-bound owner action
/// requests. It approves a callback only after the owner confirms the matching authority.
/// </summary>
internal sealed class RuntimeCopilotToolCallAuthorization(
    RuntimeRegistration registration,
    string sessionId,
    Func<RuntimeActionRequest, CancellationToken, Task> requireAuthority,
    RuntimeCopilotTurnObserver turns,
    RuntimeNativeCallSequence nativeCalls,
    string workspaceRoot,
    Func<RuntimeNativeToolPermissionRejectionRequest, CancellationToken, Task>?
        recordToolPermissionRejection = null)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, NativeToolCall> _calls = new(StringComparer.Ordinal);
    private Guid? _messageId;

    /// <summary>Checks tool policy before invocation without reserving a native tool-call slot.</summary>
    internal Task<PreToolUseHookOutput?> OnPreToolUseAsync(
        PreToolUseHookInput input, HookInvocation invocation)
    {
        if (input.SessionId != sessionId || invocation.SessionId != sessionId ||
            ActionForTool(input.ToolName) is not { } action)
            return Task.FromResult<PreToolUseHookOutput?>(Deny("runtime_tool_not_registered"));
        // Generation may inspect files, but it must not inherit this run's write or shell capabilities.
        if (turns.ReadOnlyToolsOnly && input.ToolName != "view")
            return Task.FromResult<PreToolUseHookOutput?>(Deny("runtime_tool_read_only"));
        try
        {
            _ = ReadContext();
            RuntimeNativeWorkspaceContainment.RequirePreToolUse(workspaceRoot, input);
            return Task.FromResult<PreToolUseHookOutput?>(new PreToolUseHookOutput
            {
                PermissionDecision = "defer-to-hook",
                PermissionDecisionReason = "runtime_tool_permission_hook_required"
            });
        }
        catch (RuntimeAuthorizationException failure)
        {
            return Task.FromResult<PreToolUseHookOutput?>(Deny(failure.Code));
        }
    }

    /// <summary>
    /// Binds native permission requests to one tool call, rechecks owner authority before
    /// approval, and rejects unsupported or mismatched callbacks.
    /// </summary>
    internal async Task<PermissionDecision> OnPermissionRequestAsync(
        PermissionRequest request, PermissionInvocation invocation)
    {
        if (invocation.SessionId != sessionId)
            return PermissionDecision.Reject("runtime_tool_authority_unavailable");
        try
        {
            var context = ReadContext();
            NativeToolCall call;
            RuntimeActionRequest? secondary = null;
            if (request is PermissionRequestHook hook &&
                IsValidCallId(hook.ToolCallId) &&
                ActionForTool(hook.ToolName) is { } action)
            {
                // Recheck the restriction at permission time; a prior pre-tool hook is not an approval.
                if (context.ReadOnlyToolsOnly && hook.ToolName != "view")
                    return PermissionDecision.Reject("runtime_tool_read_only");
                RuntimeNativeWorkspaceContainment.RequirePermissionRequest(workspaceRoot, request);
                var argumentDigest = RuntimeContractValidation.Hash(
                    JsonSerializer.SerializeToUtf8Bytes(hook.ToolArgs));
                var ownerRequest = CreateRequest(
                    CreateEventId(context.MessageId, hook.ToolCallId!, "invocation"),
                    action,
                    RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(request)),
                    isInvocation: true,
                    new RuntimeActionToolOwnerProjection(
                        1, sessionId, hook.ToolName!, hook.ToolCallId!, "invocation",
                        argumentDigest, context.MessageId, registration.Binding.TurnId, action,
                        action == "tool.read", action == "tool.write",
                        action.StartsWith("exec.", StringComparison.Ordinal)));
                call = GetOrAdd(context, hook.ToolCallId!, ownerRequest);
            }
            else if (TryReadSecondary(request, out var kind, out var callId) && IsValidCallId(callId))
            {
                call = ReadCall(context, callId!);
                if (context.ReadOnlyToolsOnly && call.Request.ActionId != "tool.read")
                    return PermissionDecision.Reject("runtime_tool_read_only");
                if (!MatchesPermission(call.Request.ActionId, kind))
                    return PermissionDecision.Reject("runtime_permission_not_registered");
                RuntimeNativeWorkspaceContainment.RequirePermissionRequest(workspaceRoot, request);
                secondary = GetSecondary(call, kind, RuntimeContractValidation.Hash(
                    JsonSerializer.SerializeToUtf8Bytes(request)));
            }
            else
            {
                return PermissionDecision.Reject("runtime_native_tool_call_identity_invalid");
            }

            RequireCall(context, call);
            try
            {
                await requireAuthority(call.Request, context.Token).ConfigureAwait(false);
            }
            catch (RuntimeAuthorizationException failure) when (
                request is PermissionRequestHook &&
                failure.Code == RuntimeNativeToolPermissionRejectionContract.NumericToolLimitReasonCode &&
                recordToolPermissionRejection is not null)
            {
                var rejection = PermissionDecision.Reject(failure.Code);
                var evidence = new RuntimeNativeToolPermissionRejectionRequest(
                    1,
                    call.Request.RuntimeInstanceId,
                    call.Request.RegistrationRevision,
                    call.Request.ExecutionFence,
                    call.Request.EventId,
                    context.MessageId,
                    RuntimeActionContract.Hash(call.Request),
                    failure.Code);
                try
                {
                    await recordToolPermissionRejection(evidence, context.Token).ConfigureAwait(false);
                }
                catch (Exception writeFailure) when (writeFailure is not OutOfMemoryException)
                {
                    // Rejection must not become an approval or a callback error when evidence cannot be saved.
                }
                return rejection;
            }
            RequireCall(context, call);
            if (secondary is not null)
            {
                await requireAuthority(secondary, context.Token).ConfigureAwait(false);
                RequireCall(context, call);
            }
            RuntimeNativeWorkspaceContainment.RequirePermissionRequest(workspaceRoot, request);
            if (request is PermissionRequestHook)
                nativeCalls.RecordToolEffectCall();
            return PermissionDecision.ApproveOnce();
        }
        catch (RuntimeAuthorizationException failure)
        {
            return PermissionDecision.Reject(failure.Code);
        }
    }

    /// <summary>Clears all native call bindings when the SDK turn ends or fails.</summary>
    internal void Clear()
    {
        lock (_gate)
        {
            _calls.Clear();
            _messageId = null;
        }
    }

    private NativeToolCall GetOrAdd(ActiveContext context, string callId, RuntimeActionRequest request)
    {
        lock (_gate)
        {
            RequireContext(context);
            if (_messageId != context.MessageId)
            {
                _calls.Clear();
                _messageId = context.MessageId;
            }
            if (_calls.TryGetValue(callId, out var existing))
            {
                if (existing.Request != request)
                    throw new RuntimeAuthorizationException("runtime_native_tool_call_conflict");
                return existing;
            }
            var call = new NativeToolCall(context.MessageId, callId, request);
            _calls.Add(callId, call);
            return call;
        }
    }

    private NativeToolCall ReadCall(ActiveContext context, string callId)
    {
        lock (_gate)
        {
            RequireContext(context);
            return _messageId == context.MessageId && _calls.TryGetValue(callId, out var call)
                ? call : throw new RuntimeAuthorizationException("runtime_native_tool_call_unavailable");
        }
    }

    private RuntimeActionRequest GetSecondary(NativeToolCall call, string kind, string inputHash)
    {
        var owner = call.Request.ToolOwner!;
        var request = CreateRequest(
            CreateEventId(call.MessageId, call.CallId, "permission:" + kind),
            call.Request.ActionId, inputHash, isInvocation: false,
            owner with
            {
                Phase = "permission",
                PermissionKind = kind,
                ArgumentDigest = inputHash,
                InvocationArgumentDigest = owner.ArgumentDigest,
                InvocationEventId = call.Request.EventId,
                InvocationRequestHash = RuntimeActionContract.Hash(call.Request)
            });
        lock (_gate)
        {
            if (call.Permissions.TryGetValue(kind, out var existing))
            {
                if (existing != request)
                    throw new RuntimeAuthorizationException("runtime_native_tool_call_conflict");
                return existing;
            }
            call.Permissions.Add(kind, request);
            return request;
        }
    }

    private void RequireCall(ActiveContext context, NativeToolCall call)
    {
        lock (_gate)
        {
            RequireContext(context);
            if (_messageId != context.MessageId ||
                !_calls.TryGetValue(call.CallId, out var current) || !ReferenceEquals(call, current))
                throw new RuntimeAuthorizationException("runtime_native_tool_call_unavailable");
        }
    }

    private ActiveContext ReadContext()
    {
        var token = turns.RequireActiveToken();
        if (turns.ActiveA2AMessageId is not { } messageId || messageId == Guid.Empty)
            throw new RuntimeAuthorizationException("runtime_native_turn_unavailable");
        token.ThrowIfCancellationRequested();
        return new(messageId, token, turns.ReadOnlyToolsOnly);
    }

    private void RequireContext(ActiveContext expected)
    {
        expected.Token.ThrowIfCancellationRequested();
        if (ReadContext() != expected)
            throw new RuntimeAuthorizationException("runtime_native_turn_unavailable");
    }

    private RuntimeActionRequest CreateRequest(
        Guid eventId, string action, string inputHash, bool isInvocation,
        RuntimeActionToolOwnerProjection? toolOwner = null)
    {
        var request = new RuntimeActionRequest(
            1, registration.RuntimeInstanceId, registration.Revision, registration.Binding.ExecutionFence,
            eventId, action, inputHash)
        {
            IsToolInvocation = isInvocation,
            IsNativeToolCallback = true,
            ToolOwner = toolOwner
        };
        RuntimeActionContract.Validate(request);
        return request;
    }

    /// <summary>Derives a stable event ID from the runtime, turn, message, call, and permission phase.</summary>
    private Guid CreateEventId(Guid messageId, string callId, string phase)
    {
        var value = FormattableString.Invariant(
            $"agentweaver:native-tool:v1\n{registration.RuntimeInstanceId:N}\n{registration.Revision}\n{registration.Binding.ExecutionFence}\n{sessionId}\n{registration.Binding.TurnId}\n{messageId:N}\n{callId}\n{phase}");
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        bytes[15] |= 1;
        return new Guid(bytes.AsSpan(0, 16));
    }

    /// <summary>Maps only the SDK tools enabled by this runtime to owner action names.</summary>
    private static string? ActionForTool(string? name) => name switch
    {
        "view" => "tool.read",
        "create" or "edit" => "tool.write",
        "bash" => "exec.shell",
        "read_bash" => "exec.read",
        "write_bash" => "exec.write",
        "stop_bash" => "exec.stop",
        _ => null
    };

    private static bool IsValidCallId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && !value.Any(char.IsControl);

    /// <summary>Accepts only ordinary read, write, or shell permission requests with no bypass flags.</summary>
    private static bool TryReadSecondary(PermissionRequest request, out string kind, out string? callId)
    {
        switch (request)
        {
            case PermissionRequestRead { RequestSandboxBypass: not true, ManagedApprovalRequired: not true } read:
                kind = "read";
                callId = read.ToolCallId;
                return true;
            case PermissionRequestWrite { RequestSandboxBypass: not true, ManagedApprovalRequired: not true } write:
                kind = "write";
                callId = write.ToolCallId;
                return true;
            case PermissionRequestShell { RequestSandboxBypass: not true, ManagedApprovalRequired: not true } shell:
                kind = "shell";
                callId = shell.ToolCallId;
                return true;
            default:
                kind = "";
                callId = null;
                return false;
        }
    }

    private static bool MatchesPermission(string action, string kind) => kind switch
    {
        "read" => action == "tool.read",
        "write" => action == "tool.write",
        "shell" => action is "exec.shell" or "exec.read" or "exec.write" or "exec.stop",
        _ => false
    };

    private static PreToolUseHookOutput Deny(string reason) =>
        new() { PermissionDecision = "deny", PermissionDecisionReason = reason };

    private sealed record ActiveContext(Guid MessageId, CancellationToken Token, bool ReadOnlyToolsOnly);

    private sealed record NativeToolCall(Guid MessageId, string CallId, RuntimeActionRequest Request)
    {
        /// <summary>Owner requests already issued for the call's secondary SDK permission checks.</summary>
        internal Dictionary<string, RuntimeActionRequest> Permissions { get; } = new(StringComparer.Ordinal);
    }
}

#pragma warning restore GHCP001
