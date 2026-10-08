using System.Text.Json;
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
    string InputHash);

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
            request.EventId == Guid.Empty || !ActionIds.Contains(request.ActionId))
            throw new RuntimeAuthorizationException("runtime_action_invalid");
        RuntimeContractValidation.ValidateHash(request.InputHash);
    }

    public static string Hash(RuntimeActionRequest request)
    {
        Validate(request);
        return RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(request));
    }
}
