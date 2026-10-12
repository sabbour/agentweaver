namespace Agentweaver.Abstractions;

public sealed record RemoteMcpNativeCallRequest(
    int ContractVersion,
    RemoteMcpCurrentCallVerificationRequest CurrentCall,
    byte[] RequestBytes)
{
    public override string ToString() => nameof(RemoteMcpNativeCallRequest) + " [REDACTED]";
}

public sealed record RemoteMcpNativeCallResult(
    int ContractVersion,
    RemoteToolResultProof Proof,
    byte[] ContentBytes)
{
    public override string ToString() => nameof(RemoteMcpNativeCallResult) + " [REDACTED]";
}

public static class RemoteMcpNativeCallContract
{
    public const int CurrentVersion = 1;
    public const int MaximumEnvelopeBytes = 2 * SessionMaterialValidation.MaximumBytes;
    public const string DispatchPath = "/internal/remote-mcp/current-call/dispatch";

    public static byte[] CopyRequestBytes(RemoteMcpNativeCallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ContractVersion != CurrentVersion ||
            request.RequestBytes is not { Length: > 0 and <= SessionMaterialValidation.MaximumBytes })
            throw new ArgumentException("A versioned, bounded Remote MCP request is required.", nameof(request));
        return request.RequestBytes.ToArray();
    }

    public static void ValidateRequest(
        RemoteMcpNativeCallRequest request,
        string actualMethod,
        string actualTarget,
        ReadOnlyMemory<byte> actualRequestBytes)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ownedRequestBytes = request.RequestBytes;
        if (request.ContractVersion != CurrentVersion ||
            request.CurrentCall is null ||
            ownedRequestBytes is not { Length: > 0 and <= SessionMaterialValidation.MaximumBytes } ||
            !ownedRequestBytes.AsSpan().SequenceEqual(actualRequestBytes.Span))
            throw new ArgumentException("The actual request must match the bounded received bytes.", nameof(request));
        RemoteMcpCurrentCallContract.ValidateToolCallBytes(
            request.CurrentCall, actualMethod, actualTarget, actualRequestBytes);
    }

    public static byte[] CopyContentBytes(RemoteMcpNativeCallResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.ContractVersion != CurrentVersion ||
            result.ContentBytes is not { Length: > 0 and <= SessionMaterialValidation.MaximumBytes })
            throw new ArgumentException("A versioned, bounded Remote MCP result is required.", nameof(result));
        return result.ContentBytes.ToArray();
    }

    public static void ValidateResult(
        RemoteMcpNativeCallRequest request,
        RemoteMcpNativeCallResult result)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        if (request.ContractVersion != CurrentVersion || request.CurrentCall is null ||
            result.ContractVersion != CurrentVersion ||
            result.ContentBytes is not { Length: > 0 and <= SessionMaterialValidation.MaximumBytes })
            throw new ArgumentException("A versioned, bounded Remote MCP result is required.", nameof(result));
        SessionMaterialValidation.ValidateRemoteToolResultProof(request.CurrentCall.Proof);
        SessionMaterialValidation.ValidateRemoteToolResultProof(result.Proof);
        if (!SessionMaterialValidation.SameRemoteToolResultProof(
                result.Proof, request.CurrentCall.Proof with { ContentClass = result.Proof.ContentClass }))
            throw new ArgumentException("The result proof must match the exact reviewed call.", nameof(result));
    }
}
