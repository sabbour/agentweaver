using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Frozen;
using Agentweaver.Abstractions;

namespace Agentweaver.Identity;

/// <summary>Owner admission request binding one runtime action to its registration and input hash.</summary>
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

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? CostTurnId { get; init; }
}

/// <summary>
/// Model-turn admission request that carries the exact UTF-8 text whose SHA-256 digest is the request input hash.
/// The Orchestrator uses the text only to run the platform Guardrails checks and never stores it.
/// </summary>
public sealed record RuntimeActionContentRequest(RuntimeActionRequest Request, string Content);

/// <summary>Owner policy decision for a specific action request and receipt ID.</summary>
public sealed record RuntimeActionAdmission(
    int ContractVersion,
    RuntimeActionRequest Request,
    string GrantId,
    string GrantRevision,
    Guid PolicyReceiptId,
    PolicyEvaluationOutcome Outcome,
    PolicyEvaluationReasonCode ReasonCode);

/// <summary>Validation and stable action identifiers shared by runtime action admission clients.</summary>
public static class RuntimeActionContract
{
    public const string Purpose = "runtime.execution";
    public static readonly FrozenSet<string> ActionIds = new[]
    {
        "model.turn", "tool.read", "tool.write", "exec.shell",
        "exec.read", "exec.write", "exec.stop"
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Checks version, IDs, allowed action, dispatch fields, and the canonical input hash.</summary>
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
        if (request.CostTurnId is { } costTurnId &&
            (costTurnId == Guid.Empty || request.ActionId != "model.turn"))
            throw new RuntimeAuthorizationException("runtime_action_invalid");
        RuntimeContractValidation.ValidateHash(request.InputHash);
    }

    /// <summary>Requires an owner dispatch ID for bounded or hard-credit-limited native model turns.</summary>
    public static void RequireNativeDispatchAdmission(RuntimeBinding binding, RuntimeActionRequest request)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(request);
        if (request.ActionId == "model.turn" &&
            (binding.MaxModelTurns is not null ||
             binding.ModelSourceMode == ModelSourceMode.HostedCopilot &&
                 binding.CopilotHardCreditLimit is > 0) &&
            request.DispatchId is null)
            throw new RuntimeAuthorizationException("runtime_model_turn_admission_required");
    }

    /// <summary>Rejects invalid counters and stops new admissions when the configured capacity is reserved.</summary>
    public static void RequireInvocationCapacity(int? limit, long reserved, string exhaustedCode)
    {
        if (reserved < 0 || limit is < 1)
            throw new RuntimeAuthorizationException("runtime_numeric_limit_invalid");
        if (limit is { } maximum && reserved >= maximum)
            throw new RuntimeAuthorizationException(exhaustedCode);
    }

    /// <summary>Largest UTF-8 content, in bytes, that a model-turn content request can carry.</summary>
    public const int MaximumContentBytes = 262_144;

    /// <summary>Requires a valid model-turn request whose input hash is the SHA-256 digest of the UTF-8 content.</summary>
    public static byte[] ValidateContent(RuntimeActionContentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request.Request);
        // Only model turns carry content, and empty content has nothing to check.
        if (request.Request.ActionId != "model.turn" || string.IsNullOrEmpty(request.Content))
            throw new RuntimeAuthorizationException("runtime_action_invalid");
        // Strict encoding rejects lone surrogates instead of replacing them, which would change the bytes.
        byte[] content;
        try
        {
            content = new System.Text.UTF8Encoding(false, true).GetBytes(request.Content);
        }
        catch (System.Text.EncoderFallbackException)
        {
            throw new RuntimeAuthorizationException("runtime_action_invalid");
        }
        // Compare the content digest with the request's input hash. This proves the checked text is the same
        // text the runtime will send to the model, so a caller can't get clean text checked and run other input.
        if (content.Length > MaximumContentBytes ||
            RuntimeContractValidation.Hash(content) != request.Request.InputHash)
            throw new RuntimeAuthorizationException("runtime_action_invalid");
        return content;
    }

    /// <summary>Returns the SHA-256 digest of the validated serialized request.</summary>
    public static string Hash(RuntimeActionRequest request)
    {
        Validate(request);
        return RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(request));
    }
}
