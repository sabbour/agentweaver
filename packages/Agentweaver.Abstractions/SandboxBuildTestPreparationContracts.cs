using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace Agentweaver.Abstractions;

public sealed record SandboxBuildTestBindingPreparation(
    string SessionId,
    string ExecutionProfileReference,
    string ProviderOptionsRevision,
    string ExecutionOptionsSha256,
    SandboxBuildTestExpectedBinding ExpectedBinding,
    SandboxBuildTestAcceptedExecutionOptions ExecutionOptions)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public SandboxBuildTestBindingPreparation Validate(
        string sessionId,
        string executionProfileReference)
    {
        ValidateIdentifier(sessionId, nameof(SessionId), 256);
        ValidateIdentifier(executionProfileReference, nameof(ExecutionProfileReference), 128);
        ValidateIdentifier(ProviderOptionsRevision, nameof(ProviderOptionsRevision), 128);
        ArgumentNullException.ThrowIfNull(ExpectedBinding);
        ArgumentNullException.ThrowIfNull(ExecutionOptions);
        _ = ExpectedBinding.Validate();
        ExpectedBinding.ValidateSandboxProviderBinding();
        _ = ExecutionOptions.Validate();
        if (!string.Equals(SessionId, sessionId, StringComparison.Ordinal) ||
            !string.Equals(
                ExecutionProfileReference,
                executionProfileReference,
                StringComparison.Ordinal) ||
            !string.Equals(
                ExecutionOptions.ProfileId,
                executionProfileReference,
                StringComparison.Ordinal) ||
            !string.Equals(
                ExecutionOptionsSha256,
                ComputeExecutionOptionsSha256(ExecutionOptions),
                StringComparison.Ordinal))
            throw new ArgumentException("The BuildTest preparation does not match its session or pinned profile.");
        return this;
    }

    [System.Text.Json.Serialization.JsonUnmappedMemberHandling(
        System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
    public sealed record SandboxBuildTestWorkspaceRequest(
        Guid OperationId,
        string ImmutableHash,
        string RequestFingerprint,
        SandboxBuildTestCheckpointReference Checkpoint,
        SandboxBuildTestWorkspaceSource Source,
        string WorkspaceMountPath,
        bool PreparingWorkspace,
        ImmutableArray<SandboxBuildTestOutputObligation> Outputs)
    {
        public SandboxBuildTestWorkspaceRequest Validate()
        {
            ArgumentNullException.ThrowIfNull(Source);
            _ = Source.Validate();
            _ = new SandboxBuildTestOutputCollectorRequest(
                OperationId, ImmutableHash, RequestFingerprint, Checkpoint, WorkspaceMountPath,
                SandboxBuildTestLimits.MaximumFileOutputBytes, Outputs).Validate();
            if (Source.Identity.ProjectId != Checkpoint.ProjectId ||
                Source.Identity.RunId != Checkpoint.RunId ||
                Source.Identity.SessionId != Checkpoint.SessionId ||
                Source.AcceptedSelectionHash != Checkpoint.AcceptedSelectionHash)
                throw new ArgumentException("The workspace request does not match its accepted source.");
            return this;
        }
    }

    public sealed record SandboxBuildTestWorkspacePreparationReceipt(
        Guid OperationId,
        string ImmutableHash,
        string RequestFingerprint,
        SandboxBuildTestCheckpointReference Checkpoint,
        string PodUid,
        string RepositoryRelativePath,
        string SeedSha256,
        string BaselineTreeSha)
    {
        public SandboxBuildTestWorkspacePreparationReceipt Validate(
            SandboxBuildTestAcceptedCommand command,
            string requestFingerprint,
            SandboxBuildTestPodReference pod)
        {
            _ = command.ValidateMetadata();
            _ = pod.Validate();
            var source = command.WorkspaceSource ??
                throw new ArgumentException("A workspace preparation receipt requires version 2 source input.");
            if (OperationId != command.OperationId || ImmutableHash != command.ImmutableHash ||
                RequestFingerprint != requestFingerprint || Checkpoint != command.Checkpoint ||
                PodUid != pod.Uid || RepositoryRelativePath != source.PreparedRepositoryRelativePath ||
                SeedSha256 != source.SeedSha256 || BaselineTreeSha != source.BaselineTreeSha)
                throw new ArgumentException("The preparation receipt differs from its accepted workspace.");
            return this;
        }
    }

    public static string ComputeExecutionOptionsSha256(
        SandboxBuildTestAcceptedExecutionOptions executionOptions)
    {
        ArgumentNullException.ThrowIfNull(executionOptions);
        _ = executionOptions.Validate();
        return Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(executionOptions, JsonOptions))).ToLowerInvariant();
    }

    private static void ValidateIdentifier(string? value, string name, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > maximumLength ||
            value.Any(char.IsControl))
            throw new ArgumentException("A bounded opaque identifier is required.", name);
    }
}
