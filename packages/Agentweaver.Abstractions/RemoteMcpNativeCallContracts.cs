namespace Agentweaver.Abstractions;

/// <summary>
/// Carries the exact bounded HTTP request bytes and the current-call proof from the
/// runtime to the trusted native Remote MCP caller.
/// </summary>
public sealed record RemoteMcpNativeCallRequest(
    int ContractVersion,
    RemoteMcpCurrentCallVerificationRequest CurrentCall,
    byte[] RequestBytes)
{
    /// <summary>Prevents request content from appearing in logs through default formatting.</summary>
    public override string ToString() => nameof(RemoteMcpNativeCallRequest) + " [REDACTED]";
}

/// <summary>Returns bounded native response bytes with the proof bound to the reviewed call.</summary>
public sealed record RemoteMcpNativeCallResult(
    int ContractVersion,
    RemoteToolResultProof Proof,
    byte[] ContentBytes)
{
    /// <summary>Prevents response content from appearing in logs through default formatting.</summary>
    public override string ToString() => nameof(RemoteMcpNativeCallResult) + " [REDACTED]";
}

/// <summary>Validates the byte-level request and result contract used by the native Remote MCP bridge.</summary>
public static class RemoteMcpNativeCallContract
{
    /// <summary>Wire-contract version accepted by this implementation.</summary>
    public const int CurrentVersion = 1;
    /// <summary>Maximum combined envelope size reserved for request and proof content.</summary>
    public const int MaximumEnvelopeBytes = 2 * SessionMaterialValidation.MaximumBytes;
    /// <summary>Internal dispatch route used by the trusted current-call bridge.</summary>
    public const string DispatchPath = "/internal/remote-mcp/current-call/dispatch";

    /// <summary>Returns an owned copy of bounded request bytes for transport.</summary>
    /// <exception cref="ArgumentException">The request has the wrong version or an empty/oversized body.</exception>
    public static byte[] CopyRequestBytes(RemoteMcpNativeCallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ContractVersion != CurrentVersion ||
            request.RequestBytes is not { Length: > 0 and <= SessionMaterialValidation.MaximumBytes })
            throw new ArgumentException("A versioned, bounded Remote MCP request is required.", nameof(request));
        return request.RequestBytes.ToArray();
    }

    /// <summary>
    /// Confirms the transport used the exact received bytes and that method, target, and
    /// call semantics match the current-call proof.
    /// </summary>
    /// <exception cref="ArgumentException">The version, bytes, or request proof does not match.</exception>
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

    /// <summary>Returns an owned copy of bounded response bytes for the caller.</summary>
    /// <exception cref="ArgumentException">The result has the wrong version or an empty/oversized body.</exception>
    public static byte[] CopyContentBytes(RemoteMcpNativeCallResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.ContractVersion != CurrentVersion ||
            result.ContentBytes is not { Length: > 0 and <= SessionMaterialValidation.MaximumBytes })
            throw new ArgumentException("A versioned, bounded Remote MCP result is required.", nameof(result));
        return result.ContentBytes.ToArray();
    }

    /// <summary>Checks that the response proof belongs to the exact reviewed request.</summary>
    /// <exception cref="ArgumentException">The request or result is invalid or the proof pins differ.</exception>
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
