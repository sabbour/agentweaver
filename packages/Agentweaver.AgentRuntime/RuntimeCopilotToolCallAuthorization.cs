using System.Security.Cryptography;
using System.Text;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;

#pragma warning disable GHCP001 // Permission callback contracts are pinned to SDK 1.0.18.
namespace Agentweaver.AgentRuntime;

internal sealed class RuntimeCopilotToolCallAuthorization
{
    private readonly RuntimeRegistration _registration;
    private readonly string _sessionId;
    private readonly Func<string, Guid, ReadOnlyMemory<byte>, CancellationToken, Task> _requireActionAuthority;
    private readonly Func<CancellationToken> _requireActiveToken;
    private readonly object _gate = new();
    private readonly Dictionary<string, RuntimeCopilotToolCall> _toolCalls = new(StringComparer.Ordinal);

    public RuntimeCopilotToolCallAuthorization(
        RuntimeRegistration registration,
        string sessionId,
        Func<string, Guid, ReadOnlyMemory<byte>, CancellationToken, Task> requireActionAuthority,
        Func<CancellationToken> requireActiveToken)
    {
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
        _requireActionAuthority = requireActionAuthority
            ?? throw new ArgumentNullException(nameof(requireActionAuthority));
        _requireActiveToken = requireActiveToken ?? throw new ArgumentNullException(nameof(requireActiveToken));
    }

    internal Task<PreToolUseHookOutput?> OnPreToolUseAsync(
        PreToolUseHookInput input, HookInvocation invocation)
    {
        var actionId = ActionForTool(input.ToolName);
        if (actionId is null || input.SessionId != _sessionId || invocation.SessionId != _sessionId ||
            !RuntimeActionContract.ActionIds.Contains(actionId) || actionId == "model.turn")
            return Task.FromResult<PreToolUseHookOutput?>(new PreToolUseHookOutput
            {
                PermissionDecision = "deny",
                PermissionDecisionReason = "runtime_tool_not_registered"
            });

        return Task.FromResult<PreToolUseHookOutput?>(
            new PreToolUseHookOutput { PermissionDecision = "ask" });
    }

    internal async Task<PermissionDecision> OnPermissionRequestAsync(
        PermissionRequest request, PermissionInvocation invocation)
    {
        if (invocation.SessionId != _sessionId)
            return PermissionDecision.Reject("runtime_tool_authority_unavailable");

        RuntimeCopilotToolCall? toolCall;
        if (request is PermissionRequestHook hook &&
            IsValidToolCallId(hook.ToolCallId) &&
            ActionForTool(hook.ToolName) is { } actionId &&
            RuntimeActionContract.ActionIds.Contains(actionId) && actionId != "model.turn")
        {
            var input = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(request);
            toolCall = GetOrAdd(
                hook.ToolCallId!,
                actionId,
                CreateEventId(_registration, actionId, hook.ToolCallId!),
                input);
        }
        else if (TryGetSecondaryPermission(request, out var permissionKind, out var toolCallId) &&
            toolCallId is { } id && IsValidToolCallId(id))
        {
            toolCall = FindSecondary(id, permissionKind);
        }
        else
        {
            return PermissionDecision.Reject("runtime_tool_authority_unavailable");
        }

        if (toolCall is null)
            return PermissionDecision.Reject("runtime_tool_authority_unavailable");

        try
        {
            await toolCall.Authorization.Value.ConfigureAwait(false);
            return PermissionDecision.ApproveOnce();
        }
        catch (RuntimeAuthorizationException failure)
        {
            Remove(toolCall);
            return PermissionDecision.Reject(failure.Code);
        }
        catch
        {
            Remove(toolCall);
            throw;
        }
    }

    internal void Clear()
    {
        lock (_gate)
            _toolCalls.Clear();
    }

    internal static string? ActionForTool(string? toolName) => toolName switch
    {
        "view" => "tool.read",
        "create" or "edit" => "tool.write",
        "bash" => "exec.shell",
        "read_bash" => "exec.read",
        "write_bash" => "exec.write",
        "stop_bash" => "exec.stop",
        _ => null
    };

    private RuntimeCopilotToolCall? GetOrAdd(
        string toolCallId, string actionId, Guid eventId, byte[] input)
    {
        lock (_gate)
        {
            if (_toolCalls.TryGetValue(toolCallId, out var existing))
                return existing.Matches(actionId, eventId, input) ? existing : null;

            var authorization = new Lazy<Task>(
                () => _requireActionAuthority(actionId, eventId, input, _requireActiveToken()),
                LazyThreadSafetyMode.ExecutionAndPublication);
            var toolCall = new RuntimeCopilotToolCall(toolCallId, actionId, eventId, input, authorization);
            _toolCalls.Add(toolCallId, toolCall);
            return toolCall;
        }
    }

    private RuntimeCopilotToolCall? FindSecondary(
        string toolCallId, RuntimeToolPermissionKind permissionKind)
    {
        lock (_gate)
            return _toolCalls.TryGetValue(toolCallId, out var toolCall) &&
                MatchesPermission(toolCall.ActionId, permissionKind)
                    ? toolCall
                    : null;
    }

    private void Remove(RuntimeCopilotToolCall toolCall)
    {
        lock (_gate)
        {
            if (_toolCalls.TryGetValue(toolCall.ToolCallId, out var current) &&
                ReferenceEquals(current, toolCall))
                _toolCalls.Remove(toolCall.ToolCallId);
        }
    }

    private static Guid CreateEventId(RuntimeRegistration registration, string actionId, string toolCallId)
    {
        var value = FormattableString.Invariant(
            $"agentweaver:runtime-tool-call:v1\n{registration.RuntimeInstanceId:N}\n{registration.Revision}\n{registration.Binding.ExecutionFence}\n{actionId}\n{toolCallId}");
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        bytes[15] |= 1;
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static bool IsValidToolCallId(string? toolCallId) =>
        !string.IsNullOrWhiteSpace(toolCallId) && toolCallId.Length <= 256 &&
        !toolCallId.Any(char.IsControl);

    private static bool TryGetSecondaryPermission(
        PermissionRequest request,
        out RuntimeToolPermissionKind permissionKind,
        out string? toolCallId)
    {
        switch (request)
        {
            case PermissionRequestRead
                { RequestSandboxBypass: not true, ManagedApprovalRequired: not true } read:
                permissionKind = RuntimeToolPermissionKind.Read;
                toolCallId = read.ToolCallId;
                return true;
            case PermissionRequestWrite
                { RequestSandboxBypass: not true, ManagedApprovalRequired: not true } write:
                permissionKind = RuntimeToolPermissionKind.Write;
                toolCallId = write.ToolCallId;
                return true;
            case PermissionRequestShell
                { RequestSandboxBypass: not true, ManagedApprovalRequired: not true } shell:
                permissionKind = RuntimeToolPermissionKind.Shell;
                toolCallId = shell.ToolCallId;
                return true;
            default:
                permissionKind = default;
                toolCallId = null;
                return false;
        }
    }

    private static bool MatchesPermission(string actionId, RuntimeToolPermissionKind permissionKind) =>
        permissionKind switch
        {
            RuntimeToolPermissionKind.Read => actionId == "tool.read",
            RuntimeToolPermissionKind.Write => actionId == "tool.write",
            RuntimeToolPermissionKind.Shell =>
                actionId is "exec.shell" or "exec.read" or "exec.write" or "exec.stop",
            _ => false
        };
}

internal enum RuntimeToolPermissionKind
{
    Read,
    Write,
    Shell
}

internal sealed class RuntimeCopilotToolCall(
    string toolCallId,
    string actionId,
    Guid eventId,
    byte[] input,
    Lazy<Task> authorization)
{
    internal string ToolCallId { get; } = toolCallId;
    internal string ActionId { get; } = actionId;
    internal Guid EventId { get; } = eventId;
    internal Lazy<Task> Authorization { get; } = authorization;

    internal bool Matches(string candidateActionId, Guid candidateEventId, byte[] candidateInput) =>
        ActionId == candidateActionId && EventId == candidateEventId &&
        Input.AsSpan().SequenceEqual(candidateInput);

    private byte[] Input { get; } = input;
}

#pragma warning restore GHCP001
