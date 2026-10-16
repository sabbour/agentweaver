using Agentweaver.Abstractions;

namespace Agentweaver.Identity;

/// <summary>Verifies that stored remote tool-result material matches its owner-issued proof.</summary>
public static class RemoteToolResultReceiptValidation
{
    /// <summary>Checks acknowledgment scope and the exact returned bytes against their recorded digest.</summary>
    public static void ValidateAcknowledgment(
        SessionMaterialAcknowledgment acknowledgment, RemoteToolResultProof proof, ReadOnlySpan<byte> bytes)
    {
        ValidateBinding(acknowledgment, proof);
        if (bytes.Length is <= 0 or > SessionMaterialValidation.MaximumBytes ||
            acknowledgment.Reference.ByteLength != bytes.Length ||
            acknowledgment.Reference.Material!.Sha256 != RuntimeContractValidation.Hash(bytes))
            throw new RuntimeAuthorizationException("runtime_remote_tool_result_acknowledgment_invalid");
    }

    /// <summary>Validates a read result and returns its stored proof after checking the content acknowledgment.</summary>
    public static RemoteToolResultProof ValidateRead(
        SessionMaterialReadResult result, RemoteToolResultProof template)
    {
        SessionMaterialValidation.ValidateRemoteToolResultProof(template);
        if (result?.Material?.Reference?.Material?.RemoteToolResultProof is not { } recorded ||
            result.Bytes is null)
            throw new RuntimeAuthorizationException("runtime_remote_tool_result_read_invalid");
        // The result's content class is not known until the first call completes.
        var expected = template with { ContentClass = recorded.ContentClass };
        ValidateAcknowledgment(result.Material, expected, result.Bytes);
        return recorded;
    }

    /// <summary>Checks that acknowledged material was produced by the same SDK and model source.</summary>
    public static void ValidateSource(SessionMaterialAcknowledgment acknowledgment, SdkSessionFacts source)
    {
        var material = acknowledgment.Reference.Material
            ?? throw new RuntimeAuthorizationException("runtime_remote_tool_result_source_invalid");
        ValidateSource(material, source);
    }

    /// <summary>Checks SDK version, runtime version, selected model, and context limit.</summary>
    public static void ValidateSource(SessionMaterialBinding material, SdkSessionFacts source)
    {
        if (material.SdkVersion != source.SdkVersion || material.RuntimeVersion != source.RuntimeVersion ||
            material.ModelSelectionReference != source.ModelSelectionReference ||
            material.ModelId != source.ModelId || material.MaxPromptTokens != source.MaxPromptTokens)
            throw new RuntimeAuthorizationException("runtime_remote_tool_result_source_invalid");
    }

    /// <summary>Checks proof-bound identity, event, revision, and execution fence fields.</summary>
    private static void ValidateBinding(SessionMaterialAcknowledgment acknowledgment, RemoteToolResultProof proof)
    {
        SessionMaterialValidation.ValidateRemoteToolResultProof(proof);
        var reference = acknowledgment?.Reference;
        var material = reference?.Material;
        if (acknowledgment is null || acknowledgment.ContractVersion != 1 ||
            acknowledgment.Identity != new SessionIdentity(proof.ProjectId, proof.RunId, proof.SessionId) ||
            acknowledgment.EventId != SessionMaterialValidation.RemoteToolResultEventId(proof) ||
            acknowledgment.Position <= 0 || material is null ||
            material.ContractVersion != 1 || material.Kind != SessionMaterialKind.RemoteToolResult ||
            material.RuntimeInstanceId != proof.RuntimeInstanceId ||
            material.RegistrationRevision != proof.RegistrationRevision ||
            material.ExecutionFence != proof.ExecutionFence ||
            material.AcceptedSelectionHash != proof.AcceptedSelectionHash || material.TenantId != proof.TenantId ||
            !SessionMaterialValidation.SameRemoteToolResultProof(material.RemoteToolResultProof, proof))
            throw new RuntimeAuthorizationException("runtime_remote_tool_result_acknowledgment_invalid");
        SessionMaterialValidation.Validate(reference!);
    }
}
