namespace Agentweaver.Abstractions;

public enum SessionMaterialKind { TurnContent, SdkCache }

public sealed record SessionMaterialBinding(
    int ContractVersion,
    SessionMaterialKind Kind,
    string TenantId,
    string Sha256,
    Guid RuntimeInstanceId,
    long RegistrationRevision,
    long ExecutionFence,
    string AcceptedSelectionHash,
    string SdkVersion,
    string RuntimeVersion,
    string ModelSelectionReference,
    string ModelId);

public sealed record SessionMaterialWriteRequest(
    int ContractVersion,
    Guid EventId,
    Guid RuntimeInstanceId,
    long RegistrationRevision,
    long ExecutionFence,
    SessionMaterialKind Kind,
    byte[] Bytes,
    string? Role = null,
    string? SdkVersion = null,
    string? ModelId = null)
{
    public override string ToString() => nameof(SessionMaterialWriteRequest) + " [REDACTED]";
}

public sealed record SessionMaterialAcknowledgment(
    int ContractVersion,
    SessionIdentity Identity,
    Guid EventId,
    long Position,
    SessionObjectReference Reference);

public sealed record SessionMaterialReadResult(SessionMaterialAcknowledgment Material, byte[] Bytes)
{
    public override string ToString() => nameof(SessionMaterialReadResult) + " [REDACTED]";
}

public static class SessionMaterialValidation
{
    public const int MaximumBytes = 1024 * 1024;

    public static void Validate(SessionMaterialWriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ContractVersion != 1 || request.EventId == Guid.Empty ||
            request.RuntimeInstanceId == Guid.Empty || request.RegistrationRevision <= 0 ||
            request.ExecutionFence <= 0 || !Enum.IsDefined(request.Kind) ||
            request.Bytes is not { Length: > 0 and <= MaximumBytes } ||
            request.Kind == SessionMaterialKind.TurnContent && request.Role is not ("user" or "assistant") ||
            request.Kind == SessionMaterialKind.SdkCache && request.Role is not null)
            throw new ArgumentException("A bounded typed execution session material request is required.");
        RequireIdentifier(request.SdkVersion);
        RequireIdentifier(request.ModelId);
    }

    public static void Validate(SessionObjectReference reference)
    {
        var material = reference.Material ?? throw new ArgumentException("Recorded session material is required.");
        if (material.ContractVersion != 1 || !Enum.IsDefined(material.Kind) ||
            material.RuntimeInstanceId == Guid.Empty || material.RegistrationRevision <= 0 ||
            material.ExecutionFence <= 0 || reference.ByteLength is not (> 0 and <= MaximumBytes) ||
            reference.Purpose != Purpose(material.Kind))
            throw new ArgumentException("The recorded session material binding is invalid.");
        RequireHash(material.Sha256);
        RequireHash(material.AcceptedSelectionHash);
        RequireIdentifier(material.TenantId);
        RequireIdentifier(material.SdkVersion);
        RequireIdentifier(material.RuntimeVersion);
        RequireIdentifier(material.ModelSelectionReference);
        RequireIdentifier(material.ModelId);
    }

    public static string Purpose(SessionMaterialKind kind) => kind switch
    {
        SessionMaterialKind.TurnContent => "runtime-turn-content",
        SessionMaterialKind.SdkCache => "runtime-sdk-cache",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static void RequireHash(string? hash)
    {
        if (hash is not { Length: 64 } ||
            hash.Any(character => character is not (>= 'a' and <= 'f' or >= '0' and <= '9')))
            throw new ArgumentException("A SHA-256 material digest is required.");
    }

    private static void RequireIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '_' or '-' or ':' or '+')))
            throw new ArgumentException("An exact SDK and model binding is required.");
    }
}
