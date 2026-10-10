using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Frozen;
using Agentweaver.Abstractions;

namespace Agentweaver.Identity;

public sealed record RuntimeActionRequest(
    int ContractVersion,
    Guid RuntimeInstanceId,
    long RegistrationRevision,
    long ExecutionFence,
    Guid EventId,
    string ActionId,
    string InputHash)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsToolInvocation { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? DispatchId { get; init; }
}

public sealed record RuntimeActionAdmission(
    int ContractVersion,
    RuntimeActionRequest Request,
    string GrantId,
    string GrantRevision,
    Guid PolicyReceiptId,
    PolicyEvaluationOutcome Outcome,
    PolicyEvaluationReasonCode ReasonCode);

public static class RuntimeActionContract
{
    public const string Purpose = "runtime.execution";
    public static readonly FrozenSet<string> ActionIds = new[]
    {
        "model.turn", "tool.read", "tool.write", "exec.shell",
        "exec.read", "exec.write", "exec.stop"
    }.ToFrozenSet(StringComparer.Ordinal);

    public static void Validate(RuntimeActionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ContractVersion != 1 || request.RuntimeInstanceId == Guid.Empty ||
            request.RegistrationRevision <= 0 || request.ExecutionFence <= 0 ||
            request.EventId == Guid.Empty || !ActionIds.Contains(request.ActionId) ||
            request.IsToolInvocation && request.ActionId == "model.turn" ||
            request.DispatchId is { } dispatchId &&
                (dispatchId == Guid.Empty || request.ActionId != "model.turn"))
            throw new RuntimeAuthorizationException("runtime_action_invalid");
        RuntimeContractValidation.ValidateHash(request.InputHash);
    }

    public static void RequireInvocationCapacity(int? limit, long reserved, string exhaustedCode)
    {
        if (reserved < 0 || limit is < 1)
            throw new RuntimeAuthorizationException("runtime_numeric_limit_invalid");
        if (limit is { } maximum && reserved >= maximum)
            throw new RuntimeAuthorizationException(exhaustedCode);
    }

    public static string Hash(RuntimeActionRequest request)
    {
        Validate(request);
        return RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(request));
    }
}
