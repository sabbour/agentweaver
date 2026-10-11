using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Identity;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;

#pragma warning disable GHCP001 // Permission callbacks are pinned to SDK 1.0.18.
namespace Agentweaver.AgentRuntime;

internal sealed class RuntimeCopilotToolCallAuthorization(
    RuntimeRegistration registration,
    string sessionId,
    Func<RuntimeActionRequest, CancellationToken, Task> requireAuthority,
    RuntimeCopilotTurnObserver turns)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, NativeToolCall> _calls = new(StringComparer.Ordinal);
    private Guid? _messageId;

    internal async Task<PreToolUseHookOutput?> OnPreToolUseAsync(
        PreToolUseHookInput input, HookInvocation invocation)
    {
        if (input.SessionId != sessionId || invocation.SessionId != sessionId ||
            ActionForTool(input.ToolName) is not { } action)
            return Deny("runtime_tool_not_registered");
        try
        {
            var context = ReadContext();
            // This callback has no native call ID; it checks policy without reserving a tool slot.
            var request = CreateRequest(
                Guid.NewGuid(), action, RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(input)),
                isInvocation: false);
            await requireAuthority(request, context.Token).ConfigureAwait(false);
            RequireContext(context);
            return new PreToolUseHookOutput { PermissionDecision = "ask" };
        }
        catch (RuntimeAuthorizationException failure)
        {
            return Deny(failure.Code);
        }
    }

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
                var ownerRequest = CreateRequest(
                    CreateEventId(context.MessageId, hook.ToolCallId!, "invocation"),
                    action,
                    RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(request)),
                    isInvocation: true);
                call = GetOrAdd(context, hook.ToolCallId!, ownerRequest);
            }
            else if (TryReadSecondary(request, out var kind, out var callId) && IsValidCallId(callId))
            {
                call = ReadCall(context, callId!);
                if (!MatchesPermission(call.Request.ActionId, kind))
                    return PermissionDecision.Reject("runtime_permission_not_registered");
                secondary = GetSecondary(call, kind, RuntimeContractValidation.Hash(
                    JsonSerializer.SerializeToUtf8Bytes(request)));
            }
            else
            {
                return PermissionDecision.Reject("runtime_native_tool_call_identity_invalid");
            }

            RequireCall(context, call);
            await requireAuthority(call.Request, context.Token).ConfigureAwait(false);
            RequireCall(context, call);
            if (secondary is not null)
            {
                await requireAuthority(secondary, context.Token).ConfigureAwait(false);
                RequireCall(context, call);
            }
            return PermissionDecision.ApproveOnce();
        }
        catch (RuntimeAuthorizationException failure)
        {
            return PermissionDecision.Reject(failure.Code);
        }
    }

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
        var request = CreateRequest(
            CreateEventId(call.MessageId, call.CallId, "permission:" + kind),
            call.Request.ActionId, inputHash, isInvocation: false);
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
        return new(messageId, token);
    }

    private void RequireContext(ActiveContext expected)
    {
        expected.Token.ThrowIfCancellationRequested();
        if (ReadContext() != expected)
            throw new RuntimeAuthorizationException("runtime_native_turn_unavailable");
    }

    private RuntimeActionRequest CreateRequest(Guid eventId, string action, string inputHash, bool isInvocation)
    {
        var request = new RuntimeActionRequest(
            1, registration.RuntimeInstanceId, registration.Revision, registration.Binding.ExecutionFence,
            eventId, action, inputHash)
        {
            IsToolInvocation = isInvocation
        };
        RuntimeActionContract.Validate(request);
        return request;
    }

    private Guid CreateEventId(Guid messageId, string callId, string phase)
    {
        var value = FormattableString.Invariant(
            $"agentweaver:native-tool:v1\n{registration.RuntimeInstanceId:N}\n{registration.Revision}\n{registration.Binding.ExecutionFence}\n{sessionId}\n{registration.Binding.TurnId}\n{messageId:N}\n{callId}\n{phase}");
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        bytes[15] |= 1;
        return new Guid(bytes.AsSpan(0, 16));
    }

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

    private sealed record ActiveContext(Guid MessageId, CancellationToken Token);

    private sealed record NativeToolCall(Guid MessageId, string CallId, RuntimeActionRequest Request)
    {
        internal Dictionary<string, RuntimeActionRequest> Permissions { get; } = new(StringComparer.Ordinal);
    }
}

#pragma warning restore GHCP001
