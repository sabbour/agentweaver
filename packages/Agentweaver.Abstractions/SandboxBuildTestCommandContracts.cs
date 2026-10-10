using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Agentweaver.Abstractions;

public sealed record SandboxBuildTestCheckpointReference(
    string ProjectId,
    string RunId,
    string SessionId,
    string CheckpointId,
    string WorkPlanId,
    string StepId,
    long CheckpointRevision,
    long DecisionStateVersion,
    long ExecutionFence,
    string AcceptedSelectionHash)
{
    private static readonly Regex Hash = new(
        "^[a-fA-F0-9]{64}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public SandboxBuildTestCheckpointReference Validate()
    {
        ValidateIdentifier(ProjectId, nameof(ProjectId), 256);
        ValidateIdentifier(RunId, nameof(RunId), 256);
        ValidateIdentifier(SessionId, nameof(SessionId), 256);
        ValidateIdentifier(CheckpointId, nameof(CheckpointId), 128);
        ValidateIdentifier(WorkPlanId, nameof(WorkPlanId), 256);
        ValidateIdentifier(StepId, nameof(StepId), 128);
        if (CheckpointRevision < 1 ||
            DecisionStateVersion < 1 ||
            ExecutionFence < 1 ||
            AcceptedSelectionHash is null ||
            !Hash.IsMatch(AcceptedSelectionHash))
            throw new ArgumentException("The BuildTest MAF checkpoint reference is invalid.");
        return this;
    }

    internal static void ValidateIdentifier(string? value, string name, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > maximumLength ||
            value.Any(char.IsControl))
            throw new ArgumentException("A bounded opaque identifier is required.", name);
    }
}

public sealed record SandboxBuildTestOutputObligation(
    string Name,
    string RelativePath,
    bool Required,
    long MaximumBytes)
{
    public SandboxBuildTestOutputObligation Validate()
    {
        SandboxBuildTestCheckpointReference.ValidateIdentifier(Name, nameof(Name), 128);
        if (!SandboxBuildTestPaths.IsWorkspaceRelative(RelativePath) ||
            RelativePath == "." ||
            MaximumBytes is < 1 or > SandboxBuildTestLimits.MaximumFileOutputBytes)
            throw new ArgumentException("A bounded workspace-relative BuildTest output is required.");
        return this;
    }
}

public sealed record SandboxBuildTestAcceptedExecutionOptions(
    string ProfileId,
    string ImageReference,
    string ImagePlatform,
    ImmutableArray<string> AllowedExecutables,
    string CpuLimit,
    string MemoryLimit,
    string EphemeralStorageLimit,
    int TimeoutSeconds,
    int MaximumOutputBytes,
    long MaximumFileOutputBytes,
    string EgressProfile,
    string CollectorImageReference,
    string CollectorImagePlatform,
    string CollectorExecutableReference,
    ImmutableArray<string> CollectorAssemblyArguments,
    string CollectorMode,
    string CollectorContainerName)
{
    private static readonly Regex PinnedImage = new(
        "^.+@sha256:[a-f0-9]{64}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex CpuQuantity = new(
        "^(?:[1-9][0-9]*m|[1-9][0-9]*(?:\\.[0-9]+)?)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex StorageQuantity = new(
        "^[1-9][0-9]*(?:Ki|Mi|Gi|Ti)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public SandboxBuildTestAcceptedExecutionOptions Validate()
    {
        SandboxBuildTestCheckpointReference.ValidateIdentifier(ProfileId, nameof(ProfileId), 128);
        if (ImageReference is null ||
            ImageReference.Length > 512 ||
            ImageReference.Any(char.IsWhiteSpace) ||
            ImageReference.Any(char.IsControl) ||
            ImageReference.IndexOfAny(['\\', '"', '\'']) >= 0 ||
            ImageReference.Contains("://", StringComparison.Ordinal) ||
            !PinnedImage.IsMatch(ImageReference) ||
            ImagePlatform != "linux/amd64" ||
            AllowedExecutables.IsDefault ||
            AllowedExecutables.Length is < 1 or > 32 ||
            AllowedExecutables.Any(executable => !SandboxBuildTestPaths.IsAbsoluteExecutable(executable)) ||
            AllowedExecutables.Distinct(StringComparer.Ordinal).Count() != AllowedExecutables.Length ||
            !CpuQuantity.IsMatch(CpuLimit ?? string.Empty) ||
            !StorageQuantity.IsMatch(MemoryLimit ?? string.Empty) ||
            !StorageQuantity.IsMatch(EphemeralStorageLimit ?? string.Empty) ||
            TimeoutSeconds is < 1 or > 900 ||
            MaximumOutputBytes is < 1 or > SandboxBuildTestLimits.MaximumLogOutputBytes ||
            MaximumFileOutputBytes is < 0 or > SandboxBuildTestLimits.MaximumFileOutputBytes ||
            EgressProfile != SandboxBuildTestLimits.OfflineEgressProfile ||
            CollectorImageReference is null ||
            CollectorImageReference.Length > 512 ||
            CollectorImageReference.Any(char.IsWhiteSpace) ||
            CollectorImageReference.Any(char.IsControl) ||
            CollectorImageReference.IndexOfAny(['\\', '"', '\'']) >= 0 ||
            CollectorImageReference.Contains("://", StringComparison.Ordinal) ||
            !PinnedImage.IsMatch(CollectorImageReference) ||
            CollectorImagePlatform != "linux/amd64" ||
            CollectorExecutableReference != SandboxBuildTestLimits.OutputCollectorExecutable ||
            CollectorAssemblyArguments.IsDefault ||
            !CollectorAssemblyArguments.SequenceEqual(
                [SandboxBuildTestLimits.OutputCollectorAssembly],
                StringComparer.Ordinal) ||
            CollectorMode != SandboxBuildTestLimits.OutputCollectorMode ||
            CollectorContainerName != SandboxBuildTestLimits.OutputCollectorContainerName)
            throw new ArgumentException("The immutable BuildTest execution options are invalid.");
        return this with
        {
            AllowedExecutables = AllowedExecutables.ToImmutableArray(),
            CollectorAssemblyArguments = CollectorAssemblyArguments.ToImmutableArray()
        };
    }

}

public sealed record SandboxBuildTestAcceptedCommand(
    int ContractVersion,
    Guid OperationId,
    SandboxBuildTestCheckpointReference Checkpoint,
    string ExecutableReference,
    ImmutableArray<string> Arguments,
    string WorkingDirectory,
    ImmutableArray<SandboxBuildTestOutputObligation> Outputs,
    SandboxBuildTestAcceptedExecutionOptions ExecutionOptions,
    string ImmutableHash)
{
    private static readonly JsonWriterOptions CanonicalJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.Default,
        Indented = false,
        SkipValidation = false
    };

    public SandboxBuildTestAcceptedCommand Validate()
    {
        ArgumentNullException.ThrowIfNull(Checkpoint);
        ArgumentNullException.ThrowIfNull(ExecutionOptions);
        _ = Checkpoint.Validate();
        _ = ExecutionOptions.Validate();
        if (ContractVersion != 1 ||
            OperationId == Guid.Empty ||
            !SandboxBuildTestPaths.IsAbsoluteExecutable(ExecutableReference) ||
            !ExecutionOptions.AllowedExecutables.Contains(ExecutableReference, StringComparer.Ordinal) ||
            Arguments.IsDefault ||
            Arguments.Length > SandboxBuildTestLimits.MaximumArguments ||
            Arguments.Any(argument => argument is null ||
                argument.Length > SandboxBuildTestLimits.MaximumArgumentLength ||
                argument.Any(char.IsControl)) ||
            Arguments.Sum(argument => Encoding.UTF8.GetByteCount(argument)) >
                SandboxBuildTestLimits.MaximumArgumentBytes ||
            !SandboxBuildTestPaths.IsWorkspaceRelative(WorkingDirectory) ||
            WorkingDirectory != "." ||
            Outputs.IsDefault ||
            Outputs.Length > SandboxBuildTestLimits.MaximumOutputObligations ||
            Outputs.Any(output => output is null) ||
            Outputs.Select(output => output.Name).Distinct(StringComparer.Ordinal).Count() != Outputs.Length ||
            Outputs.Select(output => output.RelativePath).Distinct(StringComparer.Ordinal).Count() != Outputs.Length ||
            Outputs.Any(output => output.MaximumBytes > ExecutionOptions.MaximumFileOutputBytes) ||
            ImmutableHash is null ||
            !SandboxBuildTestHashes.IsPrefixedSha256(ImmutableHash) ||
            !string.Equals(ImmutableHash, ComputeImmutableHash(), StringComparison.Ordinal))
            throw new ArgumentException("The immutable accepted BuildTest command is invalid.");

        foreach (var output in Outputs)
            _ = output.Validate();

        return this with
        {
            Arguments = Arguments.ToImmutableArray(),
            Outputs = Outputs.ToImmutableArray(),
            ExecutionOptions = ExecutionOptions.Validate()
        };
    }

    public string ComputeImmutableHash()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, CanonicalJsonOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("contractVersion", ContractVersion);
            writer.WriteString("operationId", OperationId.ToString("D").ToLowerInvariant());
            WriteCheckpoint(writer, Checkpoint);
            writer.WriteString("executableReference", ExecutableReference);
            writer.WritePropertyName("arguments");
            writer.WriteStartArray();
            foreach (var argument in Arguments)
                writer.WriteStringValue(argument);
            writer.WriteEndArray();
            writer.WriteString("workingDirectory", WorkingDirectory);
            writer.WritePropertyName("outputs");
            writer.WriteStartArray();
            foreach (var output in Outputs)
            {
                writer.WriteStartObject();
                writer.WriteString("name", output.Name);
                writer.WriteString("relativePath", output.RelativePath);
                writer.WriteBoolean("required", output.Required);
                writer.WriteNumber("maximumBytes", output.MaximumBytes);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            WriteExecutionOptions(writer, ExecutionOptions);
            writer.WriteEndObject();
        }
        return SandboxBuildTestHashes.PrefixSha256(stream.ToArray());
    }

    internal static void WriteCheckpoint(
        Utf8JsonWriter writer,
        SandboxBuildTestCheckpointReference checkpoint)
    {
        writer.WritePropertyName("checkpoint");
        writer.WriteStartObject();
        writer.WriteString("projectId", checkpoint.ProjectId);
        writer.WriteString("runId", checkpoint.RunId);
        writer.WriteString("sessionId", checkpoint.SessionId);
        writer.WriteString("checkpointId", checkpoint.CheckpointId);
        writer.WriteString("workPlanId", checkpoint.WorkPlanId);
        writer.WriteString("stepId", checkpoint.StepId);
        writer.WriteNumber("checkpointRevision", checkpoint.CheckpointRevision);
        writer.WriteNumber("decisionStateVersion", checkpoint.DecisionStateVersion);
        writer.WriteNumber("executionFence", checkpoint.ExecutionFence);
        writer.WriteString("acceptedSelectionHash", checkpoint.AcceptedSelectionHash);
        writer.WriteEndObject();
    }

    internal static void WriteExecutionOptions(
        Utf8JsonWriter writer,
        SandboxBuildTestAcceptedExecutionOptions options)
    {
        writer.WritePropertyName("executionOptions");
        writer.WriteStartObject();
        writer.WriteString("profileId", options.ProfileId);
        writer.WriteString("imageReference", options.ImageReference);
        writer.WriteString("imagePlatform", options.ImagePlatform);
        writer.WritePropertyName("allowedExecutables");
        writer.WriteStartArray();
        foreach (var executable in options.AllowedExecutables)
            writer.WriteStringValue(executable);
        writer.WriteEndArray();
        writer.WriteString("cpuLimit", options.CpuLimit);
        writer.WriteString("memoryLimit", options.MemoryLimit);
        writer.WriteString("ephemeralStorageLimit", options.EphemeralStorageLimit);
        writer.WriteNumber("timeoutSeconds", options.TimeoutSeconds);
        writer.WriteNumber("maximumOutputBytes", options.MaximumOutputBytes);
        writer.WriteNumber("maximumFileOutputBytes", options.MaximumFileOutputBytes);
        writer.WriteString("egressProfile", options.EgressProfile);
        writer.WriteString("collectorImageReference", options.CollectorImageReference);
        writer.WriteString("collectorImagePlatform", options.CollectorImagePlatform);
        writer.WriteString("collectorExecutableReference", options.CollectorExecutableReference);
        writer.WritePropertyName("collectorAssemblyArguments");
        writer.WriteStartArray();
        foreach (var argument in options.CollectorAssemblyArguments)
            writer.WriteStringValue(argument);
        writer.WriteEndArray();
        writer.WriteString("collectorMode", options.CollectorMode);
        writer.WriteString("collectorContainerName", options.CollectorContainerName);
        writer.WriteEndObject();
    }
}

public sealed record SandboxBuildTestExpectedBinding(
    EnvironmentGenerationFence Fence,
    Guid SandboxLeaseOperationId,
    ProviderResourceRef SandboxResource,
    long ProviderFencingGeneration,
    WorkspaceVolumeReference WorkspaceVolume,
    long DataGeneration,
    long NetworkPolicyGeneration)
{
    public SandboxProviderBindingSnapshot? SandboxProviderBinding { get; init; }

    public SandboxBuildTestExpectedBinding Validate()
    {
        ArgumentNullException.ThrowIfNull(Fence);
        ArgumentNullException.ThrowIfNull(SandboxResource);
        ArgumentNullException.ThrowIfNull(WorkspaceVolume);
        _ = WorkspaceVolume.Validate();
        if (SandboxLeaseOperationId == Guid.Empty ||
            SandboxResource.Seam != ProviderSeam.Sandbox ||
            SandboxResource.Generation < 1 ||
            string.IsNullOrWhiteSpace(SandboxResource.ProviderId) ||
            SandboxResource.ProviderId.Length > 256 ||
            SandboxResource.ProviderId.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(SandboxResource.ResourceId) ||
            SandboxResource.ResourceId.Length > 512 ||
            SandboxResource.ResourceId.Any(char.IsControl) ||
            ProviderFencingGeneration < 1 ||
            DataGeneration < 0 ||
            NetworkPolicyGeneration < 1 ||
            !string.Equals(WorkspaceVolume.ProjectId, Fence.Owner.ProjectId, StringComparison.Ordinal))
            throw new ArgumentException("The expected BuildTest environment binding is invalid.");
        return this;
    }

    internal void ValidateSandboxProviderBinding()
    {
        ArgumentNullException.ThrowIfNull(SandboxProviderBinding);
        _ = SandboxProviderBinding.ValidateFor(SandboxResource);
    }
}

public sealed record SandboxBuildTestApiRequest(
    SandboxBuildTestCheckpointReference Checkpoint,
    SandboxBuildTestExpectedBinding ExpectedBinding)
{
    public SandboxBuildTestApiRequest Validate()
    {
        ArgumentNullException.ThrowIfNull(Checkpoint);
        ArgumentNullException.ThrowIfNull(ExpectedBinding);
        _ = Checkpoint.Validate();
        _ = ExpectedBinding.Validate();
        ExpectedBinding.ValidateSandboxProviderBinding();
        if (!string.Equals(Checkpoint.ProjectId, ExpectedBinding.Fence.Owner.ProjectId, StringComparison.Ordinal) ||
            !string.Equals(Checkpoint.RunId, ExpectedBinding.Fence.Owner.RunId, StringComparison.Ordinal))
            throw new ArgumentException("The checkpoint project and run must match the expected owner binding.");
        return this;
    }

    public string ComputeRequestFingerprint(SandboxBuildTestAcceptedCommand acceptedCommand)
    {
        ArgumentNullException.ThrowIfNull(acceptedCommand);
        _ = Validate();
        _ = acceptedCommand.Validate();
        if (!SandboxBuildTestCanonicalJson.SameCheckpoint(Checkpoint, acceptedCommand.Checkpoint))
            throw new ArgumentException("The accepted command does not match the referenced MAF checkpoint.");

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, SandboxBuildTestCanonicalJson.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("immutableHash", acceptedCommand.ImmutableHash);
            writer.WritePropertyName("expectedBinding");
            SandboxBuildTestCanonicalJson.WriteExpectedBinding(writer, ExpectedBinding);
            writer.WriteEndObject();
        }
        return SandboxBuildTestHashes.UnprefixedSha256(stream.ToArray());
    }
}

public enum SandboxBuildTestOperationStatus
{
    Reserved,
    Running,
    Completed,
    Failed,
    Interrupted,
    ReconciliationRequired,
    Stale
}

public enum SandboxBuildTestPodState
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Unknown
}

public sealed record SandboxBuildTestPodReference(
    string KubernetesNamespace,
    string Name,
    string Uid,
    string ResourceVersion)
{
    public SandboxBuildTestPodReference Validate()
    {
        SandboxBuildTestCheckpointReference.ValidateIdentifier(
            KubernetesNamespace, nameof(KubernetesNamespace), 63);
        SandboxBuildTestCheckpointReference.ValidateIdentifier(Name, nameof(Name), 253);
        SandboxBuildTestCheckpointReference.ValidateIdentifier(Uid, nameof(Uid), 256);
        SandboxBuildTestCheckpointReference.ValidateIdentifier(ResourceVersion, nameof(ResourceVersion), 128);
        return this;
    }
}

public sealed record SandboxBuildTestCommandNetworkPolicyBinding(
    string PolicyUid,
    string SpecSha256,
    long NetworkPolicyGeneration,
    ImmutableDictionary<string, string> SelectorLabels)
{
    public SandboxBuildTestCommandNetworkPolicyBinding Validate()
    {
        SandboxBuildTestCheckpointReference.ValidateIdentifier(PolicyUid, nameof(PolicyUid), 256);
        ArgumentNullException.ThrowIfNull(SelectorLabels);
        if (!SandboxBuildTestHashes.IsSha256(SpecSha256) ||
            NetworkPolicyGeneration < 1 ||
            SelectorLabels.Count < 1 ||
            SelectorLabels.Count > 16 ||
            SelectorLabels.Any(pair =>
                string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value) ||
                pair.Key.Length > 253 || pair.Value.Length > 63 ||
                pair.Key.Any(char.IsControl) || pair.Value.Any(char.IsControl)))
            throw new ArgumentException("The BuildTest command-network-policy binding is invalid.");
        return this with { SelectorLabels = SelectorLabels.ToImmutableDictionary(StringComparer.Ordinal) };
    }

    internal SandboxBuildTestCommandNetworkPolicyBinding ValidateForOperation(
        Guid operationId,
        string expectedRole)
    {
        _ = Validate();
        if (operationId == Guid.Empty ||
            !SelectorLabels.TryGetValue(SandboxBuildTestLimits.PolicyOperationLabel, out var operationLabel) ||
            !string.Equals(operationLabel, operationId.ToString("N"), StringComparison.Ordinal) ||
            !SelectorLabels.TryGetValue(SandboxBuildTestLimits.PolicyRoleLabel, out var roleLabel) ||
            !string.Equals(roleLabel, expectedRole, StringComparison.Ordinal))
            throw new ArgumentException("The BuildTest network policy is not scoped to this operation and Pod role.");
        return this;
    }
}

public sealed record SandboxBuildTestObservedBinding(
    EnvironmentGenerationFence Fence,
    Guid SandboxLeaseOperationId,
    ProviderResourceRef SandboxResource,
    long ProviderFencingGeneration,
    WorkspaceVolumeReference WorkspaceVolume,
    long DataGeneration,
    long NetworkPolicyGeneration,
    SandboxBuildTestCommandNetworkPolicyBinding CommandPolicy)
{
    public SandboxBuildTestObservedBinding Validate()
    {
        ArgumentNullException.ThrowIfNull(Fence);
        ArgumentNullException.ThrowIfNull(SandboxResource);
        ArgumentNullException.ThrowIfNull(WorkspaceVolume);
        ArgumentNullException.ThrowIfNull(CommandPolicy);
        _ = new SandboxBuildTestExpectedBinding(
            Fence,
            SandboxLeaseOperationId,
            SandboxResource,
            ProviderFencingGeneration,
            WorkspaceVolume,
            DataGeneration,
            NetworkPolicyGeneration).Validate();
        _ = CommandPolicy.Validate();
        if (CommandPolicy.NetworkPolicyGeneration != NetworkPolicyGeneration)
            throw new ArgumentException("The observed command-policy generation does not match its binding.");
        return this;
    }
}

public enum SandboxBuildTestTerminationKind
{
    Exited,
    Cancelled,
    TimedOut,
    OutputLimitExceeded
}

public sealed record SandboxBuildTestOutputCapture(
    string SourcePodUid,
    string ContainerName,
    ImmutableArray<byte> CapturedBytes,
    long CapturedByteCount,
    string CapturedSha256,
    bool Truncated,
    long AtLeastObservedBytes)
{
    public SandboxBuildTestOutputCapture Validate(int maximumOutputBytes)
    {
        SandboxBuildTestCheckpointReference.ValidateIdentifier(SourcePodUid, nameof(SourcePodUid), 256);
        SandboxBuildTestCheckpointReference.ValidateIdentifier(ContainerName, nameof(ContainerName), 128);
        if (maximumOutputBytes is < 1 or > SandboxBuildTestLimits.MaximumLogOutputBytes ||
            CapturedBytes.IsDefault ||
            CapturedBytes.Length > maximumOutputBytes ||
            CapturedByteCount != CapturedBytes.Length ||
            !SandboxBuildTestHashes.IsSha256(CapturedSha256) ||
            !string.Equals(
                CapturedSha256,
                SandboxBuildTestHashes.UnprefixedSha256(CapturedBytes.AsSpan()),
                StringComparison.Ordinal) ||
            (Truncated
                ? AtLeastObservedBytes <= CapturedByteCount
                : AtLeastObservedBytes != CapturedByteCount))
            throw new ArgumentException("The bounded BuildTest output capture is invalid.");
        return this;
    }
}

public sealed record SandboxBuildTestOutputEvidence(
    string CollectorPodUid,
    string CollectorContainerName,
    string Name,
    string RelativePath,
    bool Required,
    long MaximumBytes,
    bool Exists,
    long CapturedBytes,
    string? CapturedSha256);

public sealed record SandboxBuildTestOutputCollectorNetworkPolicyBinding(
    string PolicyUid,
    string SpecSha256,
    long NetworkPolicyGeneration,
    ImmutableDictionary<string, string> SelectorLabels)
{
    public SandboxBuildTestOutputCollectorNetworkPolicyBinding Validate()
    {
        SandboxBuildTestCheckpointReference.ValidateIdentifier(PolicyUid, nameof(PolicyUid), 256);
        ArgumentNullException.ThrowIfNull(SelectorLabels);
        if (!SandboxBuildTestHashes.IsSha256(SpecSha256) ||
            NetworkPolicyGeneration < 1 ||
            SelectorLabels.Count < 1 ||
            SelectorLabels.Count > 16 ||
            SelectorLabels.Any(pair =>
                string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value) ||
                pair.Key.Length > 253 || pair.Value.Length > 63 ||
                pair.Key.Any(char.IsControl) || pair.Value.Any(char.IsControl)))
            throw new ArgumentException("The BuildTest output-collector network-policy binding is invalid.");
        return this with { SelectorLabels = SelectorLabels.ToImmutableDictionary(StringComparer.Ordinal) };
    }

    internal SandboxBuildTestOutputCollectorNetworkPolicyBinding ValidateForOperation(Guid operationId)
    {
        _ = Validate();
        if (operationId == Guid.Empty ||
            !SelectorLabels.TryGetValue(SandboxBuildTestLimits.PolicyOperationLabel, out var operationLabel) ||
            !string.Equals(operationLabel, operationId.ToString("N"), StringComparison.Ordinal) ||
            !SelectorLabels.TryGetValue(SandboxBuildTestLimits.PolicyRoleLabel, out var roleLabel) ||
            !string.Equals(roleLabel, SandboxBuildTestLimits.CollectorPolicyRole, StringComparison.Ordinal))
            throw new ArgumentException("The BuildTest collector policy is not scoped to this operation and Pod role.");
        return this;
    }
}

public sealed record SandboxBuildTestOutputCollectorRequest(
    Guid OperationId,
    string ImmutableHash,
    string RequestFingerprint,
    SandboxBuildTestCheckpointReference Checkpoint,
    string WorkspaceMountPath,
    long MaximumTotalBytes,
    ImmutableArray<SandboxBuildTestOutputObligation> Outputs)
{
    public SandboxBuildTestOutputCollectorRequest Validate()
    {
        ArgumentNullException.ThrowIfNull(Checkpoint);
        _ = Checkpoint.Validate();
        if (OperationId == Guid.Empty ||
            !SandboxBuildTestHashes.IsPrefixedSha256(ImmutableHash) ||
            !SandboxBuildTestHashes.IsSha256(RequestFingerprint) ||
            !SandboxBuildTestPaths.IsAbsoluteContainerPath(WorkspaceMountPath) ||
            MaximumTotalBytes is < 1 or > SandboxBuildTestLimits.MaximumFileOutputBytes ||
            Outputs.IsDefaultOrEmpty ||
            Outputs.Length > SandboxBuildTestLimits.MaximumOutputObligations ||
            Outputs.Any(output => output is null) ||
            Outputs.Select(output => output.Name).Distinct(StringComparer.Ordinal).Count() != Outputs.Length ||
            Outputs.Select(output => output.RelativePath).Distinct(StringComparer.Ordinal).Count() != Outputs.Length ||
            Outputs.Any(output => output.MaximumBytes > MaximumTotalBytes))
            throw new ArgumentException("The BuildTest output-collector request is invalid.");
        foreach (var output in Outputs)
            _ = output.Validate();
        return this with { Outputs = Outputs.ToImmutableArray() };
    }
}

public sealed record SandboxBuildTestOutputCollectorReceipt(
    int ContractVersion,
    Guid OperationId,
    string ImmutableHash,
    string RequestFingerprint,
    SandboxBuildTestCheckpointReference Checkpoint,
    string CollectorPodUid,
    string CollectorContainerName,
    ImmutableArray<SandboxBuildTestOutputEvidence> Outputs,
    string ManifestSha256)
{
    public SandboxBuildTestOutputCollectorReceipt Validate(
        SandboxBuildTestOutputCollectorRequest request,
        SandboxBuildTestPodReference collectorPod)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(collectorPod);
        request.Validate();
        _ = collectorPod.Validate();
        ArgumentNullException.ThrowIfNull(Checkpoint);
        _ = Checkpoint.Validate();
        if (ContractVersion != 1 ||
            OperationId != request.OperationId ||
            !string.Equals(ImmutableHash, request.ImmutableHash, StringComparison.Ordinal) ||
            !string.Equals(RequestFingerprint, request.RequestFingerprint, StringComparison.Ordinal) ||
            !SandboxBuildTestCanonicalJson.SameCheckpoint(Checkpoint, request.Checkpoint) ||
            !string.Equals(CollectorPodUid, collectorPod.Uid, StringComparison.Ordinal) ||
            CollectorContainerName != SandboxBuildTestLimits.OutputCollectorContainerName ||
            Outputs.IsDefault ||
            Outputs.Length != request.Outputs.Length ||
            !SandboxBuildTestHashes.IsSha256(ManifestSha256) ||
            !string.Equals(
                ManifestSha256,
                SandboxBuildTestOutputCollectorCanonicalization.ComputeManifestSha256(this),
                StringComparison.Ordinal))
            throw new ArgumentException("The BuildTest output-collector receipt does not match its accepted request.");

        for (var index = 0; index < Outputs.Length; index++)
        {
            var evidence = Outputs[index];
            var obligation = request.Outputs[index];
            if (evidence is null ||
                !string.Equals(evidence.CollectorPodUid, collectorPod.Uid, StringComparison.Ordinal) ||
                !string.Equals(evidence.CollectorContainerName, CollectorContainerName, StringComparison.Ordinal) ||
                !string.Equals(evidence.Name, obligation.Name, StringComparison.Ordinal) ||
                !string.Equals(evidence.RelativePath, obligation.RelativePath, StringComparison.Ordinal) ||
                evidence.Required != obligation.Required ||
                evidence.MaximumBytes != obligation.MaximumBytes ||
                evidence.CapturedBytes < 0 ||
                evidence.CapturedBytes > obligation.MaximumBytes ||
                (evidence.Exists
                    ? !SandboxBuildTestHashes.IsSha256(evidence.CapturedSha256)
                    : evidence.CapturedBytes != 0 || evidence.CapturedSha256 is not null))
                throw new ArgumentException("The BuildTest output-collector evidence is invalid.");
        }
        return this;
    }
}

public static class SandboxBuildTestOutputCollectorCanonicalization
{
    public static string ComputeManifestSha256(SandboxBuildTestOutputCollectorReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, SandboxBuildTestCanonicalJson.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("contractVersion", receipt.ContractVersion);
            writer.WriteString("operationId", receipt.OperationId.ToString("D").ToLowerInvariant());
            writer.WriteString("immutableHash", receipt.ImmutableHash);
            writer.WriteString("requestFingerprint", receipt.RequestFingerprint);
            SandboxBuildTestAcceptedCommand.WriteCheckpoint(writer, receipt.Checkpoint);
            writer.WriteString("collectorPodUid", receipt.CollectorPodUid);
            writer.WriteString("collectorContainerName", receipt.CollectorContainerName);
            writer.WritePropertyName("outputs");
            writer.WriteStartArray();
            foreach (var evidence in receipt.Outputs)
            {
                writer.WriteStartObject();
                writer.WriteString("name", evidence.Name);
                writer.WriteString("relativePath", evidence.RelativePath);
                writer.WriteBoolean("required", evidence.Required);
                writer.WriteNumber("maximumBytes", evidence.MaximumBytes);
                writer.WriteBoolean("exists", evidence.Exists);
                writer.WriteNumber("capturedBytes", evidence.CapturedBytes);
                if (evidence.CapturedSha256 is null)
                    writer.WriteNull("capturedSha256");
                else
                    writer.WriteString("capturedSha256", evidence.CapturedSha256);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return SandboxBuildTestHashes.UnprefixedSha256(stream.ToArray());
    }
}

public sealed record SandboxBuildTestTerminalEvidence(
    string SourcePodUid,
    string ContainerName,
    SandboxBuildTestTerminationKind Kind,
    int? ExitCode,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? Reason)
{
    public SandboxBuildTestTerminalEvidence Validate()
    {
        SandboxBuildTestCheckpointReference.ValidateIdentifier(SourcePodUid, nameof(SourcePodUid), 256);
        SandboxBuildTestCheckpointReference.ValidateIdentifier(ContainerName, nameof(ContainerName), 128);
        if (!Enum.IsDefined(Kind) ||
            ExitCode is null ||
            StartedAt is null ||
            FinishedAt is null ||
            FinishedAt < StartedAt ||
            Reason is { Length: > 512 } ||
            Reason?.Any(char.IsControl) == true)
            throw new ArgumentException("The terminal BuildTest Pod evidence is invalid.");
        return this;
    }
}

public sealed record SandboxBuildTestOperationSnapshot(
    Guid OperationId,
    string ImmutableHash,
    string RequestFingerprint,
    SandboxBuildTestOperationStatus Status,
    SandboxBuildTestExpectedBinding ExpectedBinding,
    SandboxBuildTestCommandNetworkPolicyBinding ExpectedCommandPolicy,
    SandboxBuildTestObservedBinding? CurrentBinding,
    SandboxBuildTestPodReference? Pod,
    SandboxBuildTestTerminalEvidence? Terminal,
    SandboxBuildTestOutputCapture? Output,
    SandboxBuildTestPodReference? CollectorPod,
    SandboxBuildTestTerminalEvidence? CollectorTerminal,
    SandboxBuildTestOutputCollectorNetworkPolicyBinding? CollectorPolicy,
    string? CollectorManifestSha256,
    ImmutableArray<SandboxBuildTestOutputEvidence> OutputEvidence,
    string? FailureCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public SandboxBuildTestOperationSnapshot Validate(
        SandboxBuildTestAcceptedCommand acceptedCommand,
        int maximumOutputBytes)
    {
        ArgumentNullException.ThrowIfNull(acceptedCommand);
        ArgumentNullException.ThrowIfNull(ExpectedBinding);
        ArgumentNullException.ThrowIfNull(ExpectedCommandPolicy);
        _ = acceptedCommand.Validate();
        _ = ExpectedBinding.Validate();
        ExpectedBinding.ValidateSandboxProviderBinding();
        _ = ExpectedCommandPolicy.ValidateForOperation(OperationId, SandboxBuildTestLimits.CommandPolicyRole);
        if (OperationId != acceptedCommand.OperationId ||
            !string.Equals(ImmutableHash, acceptedCommand.ImmutableHash, StringComparison.Ordinal) ||
            !SandboxBuildTestHashes.IsSha256(RequestFingerprint) ||
            ExpectedCommandPolicy.NetworkPolicyGeneration != ExpectedBinding.NetworkPolicyGeneration ||
            (acceptedCommand.Outputs.IsEmpty
                ? CollectorPolicy is not null
                : CollectorPolicy is null ||
                    CollectorPolicy.NetworkPolicyGeneration != ExpectedBinding.NetworkPolicyGeneration) ||
            !Enum.IsDefined(Status) ||
            UpdatedAt < CreatedAt ||
            OutputEvidence.IsDefault ||
            OutputEvidence.Any(evidence => evidence is null) ||
            FailureCode is { Length: > 128 } ||
            FailureCode?.Any(char.IsControl) == true ||
            Terminal is not null &&
                Status is SandboxBuildTestOperationStatus.Reserved or SandboxBuildTestOperationStatus.Running ||
            Terminal is { Kind: SandboxBuildTestTerminationKind.Exited, ExitCode: not 0 } &&
                Status != SandboxBuildTestOperationStatus.Failed ||
            Status == SandboxBuildTestOperationStatus.Interrupted &&
                !HasInterruptedEvidence(acceptedCommand) ||
            Terminal is { Kind: not SandboxBuildTestTerminationKind.Exited } &&
                Status != SandboxBuildTestOperationStatus.Interrupted ||
            Status == SandboxBuildTestOperationStatus.Completed &&
                (Pod is null ||
                 Terminal?.Kind != SandboxBuildTestTerminationKind.Exited ||
                 Terminal?.ExitCode != 0 ||
                 !string.Equals(Terminal.SourcePodUid, Pod.Uid, StringComparison.Ordinal) ||
                 Output is null ||
                 Output.Truncated ||
                 CurrentBinding is null ||
                 !SandboxBuildTestCanonicalJson.Matches(
                     ExpectedBinding, CurrentBinding, ExpectedCommandPolicy) ||
                 !CollectorEvidenceMatches(acceptedCommand))
            )
            throw new ArgumentException("The BuildTest operation snapshot is invalid.");
        if (Pod is not null)
            _ = Pod.Validate();
        if (Terminal is not null)
            _ = Terminal.Validate();
        if (CurrentBinding is not null)
            _ = CurrentBinding.Validate();
        if (CollectorPod is not null)
        {
            _ = CollectorPod.Validate();
            if (Pod is not null && string.Equals(Pod.Uid, CollectorPod.Uid, StringComparison.Ordinal))
                throw new ArgumentException("The BuildTest output collector must use a separate Pod.");
        }
        if (CollectorTerminal is not null)
        {
            _ = CollectorTerminal.Validate();
            if (CollectorPod is null ||
                !string.Equals(CollectorTerminal.SourcePodUid, CollectorPod.Uid, StringComparison.Ordinal) ||
                CollectorTerminal.ContainerName != SandboxBuildTestLimits.OutputCollectorContainerName)
                throw new ArgumentException("The BuildTest collector terminal evidence is not bound to its Pod.");
        }
        if (CollectorPolicy is not null)
            _ = CollectorPolicy.ValidateForOperation(OperationId);
        if (CollectorManifestSha256 is not null && !SandboxBuildTestHashes.IsSha256(CollectorManifestSha256))
            throw new ArgumentException("The BuildTest collector manifest digest is invalid.");
        if (Output is not null)
        {
            _ = Output.Validate(maximumOutputBytes);
            if (!string.Equals(Output.SourcePodUid, Pod?.Uid, StringComparison.Ordinal) ||
                Terminal is not null && !string.Equals(
                    Output.ContainerName, Terminal.ContainerName, StringComparison.Ordinal))
                throw new ArgumentException("The BuildTest output capture is bound to another Pod or container.");
        }
        foreach (var evidence in OutputEvidence)
        {
            SandboxBuildTestCheckpointReference.ValidateIdentifier(
                evidence.CollectorPodUid, nameof(evidence.CollectorPodUid), 256);
            SandboxBuildTestCheckpointReference.ValidateIdentifier(
                evidence.CollectorContainerName, nameof(evidence.CollectorContainerName), 128);
            SandboxBuildTestCheckpointReference.ValidateIdentifier(evidence.Name, nameof(evidence.Name), 128);
            if (!SandboxBuildTestPaths.IsWorkspaceRelative(evidence.RelativePath) ||
                evidence.RelativePath == "." ||
                evidence.CapturedBytes < 0 ||
                evidence.MaximumBytes is < 1 or > SandboxBuildTestLimits.MaximumFileOutputBytes ||
                evidence.CapturedBytes > evidence.MaximumBytes ||
                (evidence.Exists
                    ? !SandboxBuildTestHashes.IsSha256(evidence.CapturedSha256)
                    : evidence.CapturedBytes != 0 || evidence.CapturedSha256 is not null))
                throw new ArgumentException("BuildTest output evidence is invalid.");
        }
        return this;
    }

    private bool HasInterruptedEvidence(SandboxBuildTestAcceptedCommand acceptedCommand)
    {
        if (Terminal is { Kind: not SandboxBuildTestTerminationKind.Exited })
            return true;
        if (Terminal is not { Kind: SandboxBuildTestTerminationKind.Exited, ExitCode: 0 } ||
            acceptedCommand.Outputs.IsEmpty ||
            Pod is null ||
            CollectorPod is null ||
            CollectorTerminal is null ||
            string.Equals(Pod.Uid, CollectorPod.Uid, StringComparison.Ordinal) ||
            !string.Equals(CollectorTerminal.SourcePodUid, CollectorPod.Uid, StringComparison.Ordinal) ||
            !string.Equals(
                CollectorTerminal.ContainerName,
                SandboxBuildTestLimits.OutputCollectorContainerName,
                StringComparison.Ordinal))
            return false;
        return CollectorTerminal.Kind is SandboxBuildTestTerminationKind.Cancelled or
            SandboxBuildTestTerminationKind.TimedOut or
            SandboxBuildTestTerminationKind.OutputLimitExceeded;
    }

    private bool CollectorEvidenceMatches(SandboxBuildTestAcceptedCommand acceptedCommand)
    {
        if (acceptedCommand.Outputs.IsEmpty)
            return CollectorPod is null &&
                CollectorTerminal is null &&
                CollectorPolicy is null &&
                CollectorManifestSha256 is null &&
                OutputEvidence.IsEmpty;
        if (CollectorPod is null ||
            CollectorTerminal is not { Kind: SandboxBuildTestTerminationKind.Exited, ExitCode: 0 } ||
            CollectorPolicy is null ||
            CollectorManifestSha256 is null ||
            OutputEvidence.Length != acceptedCommand.Outputs.Length)
            return false;
        for (var index = 0; index < OutputEvidence.Length; index++)
        {
            var evidence = OutputEvidence[index];
            var obligation = acceptedCommand.Outputs[index];
            if (!string.Equals(evidence.CollectorPodUid, CollectorPod.Uid, StringComparison.Ordinal) ||
                evidence.CollectorContainerName != SandboxBuildTestLimits.OutputCollectorContainerName ||
                evidence.Name != obligation.Name ||
                evidence.RelativePath != obligation.RelativePath ||
                evidence.Required != obligation.Required ||
                evidence.MaximumBytes != obligation.MaximumBytes ||
                evidence.Required && !evidence.Exists)
                return false;
        }
        return true;
    }
}

public sealed record SandboxBuildTestPodObservation(
    SandboxBuildTestPodReference Pod,
    SandboxBuildTestPodState State,
    SandboxBuildTestTerminalEvidence? Terminal,
    SandboxBuildTestOutputCapture Output)
{
    public SandboxBuildTestPodObservation Validate(int maximumOutputBytes)
    {
        ArgumentNullException.ThrowIfNull(Pod);
        ArgumentNullException.ThrowIfNull(Output);
        _ = Pod.Validate();
        _ = Output.Validate(maximumOutputBytes);
        if (!Enum.IsDefined(State) ||
            ((State is SandboxBuildTestPodState.Succeeded or SandboxBuildTestPodState.Failed) !=
                (Terminal is not null)))
            throw new ArgumentException("The BuildTest Pod observation is invalid.");
        if (Terminal is { Kind: SandboxBuildTestTerminationKind.Exited } exited &&
                (State == SandboxBuildTestPodState.Succeeded && exited.ExitCode != 0 ||
                 State == SandboxBuildTestPodState.Failed && exited.ExitCode == 0) ||
            Terminal is { Kind: not SandboxBuildTestTerminationKind.Exited } &&
                State == SandboxBuildTestPodState.Succeeded ||
            Terminal is { Kind: SandboxBuildTestTerminationKind.OutputLimitExceeded } &&
                !Output.Truncated)
            throw new ArgumentException("The BuildTest Pod state contradicts its termination evidence.");
        if (Terminal is not null)
        {
            _ = Terminal.Validate();
            if (!string.Equals(Terminal.SourcePodUid, Pod.Uid, StringComparison.Ordinal))
                throw new ArgumentException("Terminal evidence is bound to another Pod UID.");
            if (!string.Equals(Output.ContainerName, Terminal.ContainerName, StringComparison.Ordinal))
                throw new ArgumentException("The BuildTest output capture is bound to another container.");
        }
        if (!string.Equals(Output.SourcePodUid, Pod.Uid, StringComparison.Ordinal))
            throw new ArgumentException("The BuildTest output capture is bound to another Pod UID.");
        return this;
    }
}

public sealed record SandboxBuildTestProviderRequest(
    SandboxBuildTestAcceptedCommand AcceptedCommand,
    SandboxBuildTestExpectedBinding ExpectedBinding,
    SandboxBuildTestCommandNetworkPolicyBinding CommandPolicy)
{
    public SandboxBuildTestOutputCollectorNetworkPolicyBinding? CollectorPolicy { get; init; }

    public SandboxBuildTestProviderRequest Validate()
    {
        ArgumentNullException.ThrowIfNull(AcceptedCommand);
        ArgumentNullException.ThrowIfNull(ExpectedBinding);
        ArgumentNullException.ThrowIfNull(CommandPolicy);
        _ = AcceptedCommand.Validate();
        _ = ExpectedBinding.Validate();
        ExpectedBinding.ValidateSandboxProviderBinding();
        _ = CommandPolicy.ValidateForOperation(
            AcceptedCommand.OperationId,
            SandboxBuildTestLimits.CommandPolicyRole);
        if (CommandPolicy.NetworkPolicyGeneration != ExpectedBinding.NetworkPolicyGeneration ||
            (AcceptedCommand.Outputs.IsEmpty
                ? CollectorPolicy is not null
                : CollectorPolicy is null ||
                    CollectorPolicy.NetworkPolicyGeneration != ExpectedBinding.NetworkPolicyGeneration))
            throw new ArgumentException("The command-policy generation does not match the expected network generation.");
        if (CollectorPolicy is not null)
            _ = CollectorPolicy.ValidateForOperation(AcceptedCommand.OperationId);
        return this;
    }
}

public sealed record SandboxBuildTestOperationResult(
    SandboxBuildTestOperationSnapshot Operation,
    bool Replayed);

public interface ISandboxBuildTestAcceptedCommandVerifier
{
    Task<SandboxBuildTestAcceptedCommand> ResolveAsync(
        SandboxBuildTestCheckpointReference checkpoint,
        CancellationToken cancellationToken);
}

public interface ISandboxBuildTestCommandProvider
{
    Task<SandboxBuildTestPodReference> CreateGatedAsync(
        SandboxBuildTestProviderRequest request,
        CancellationToken cancellationToken);

    Task<SandboxBuildTestPodReference> CreateOutputCollectorGatedAsync(
        SandboxBuildTestProviderRequest request,
        SandboxBuildTestOutputCollectorRequest collectorRequest,
        CancellationToken cancellationToken);

    Task<SandboxBuildTestPodReference?> GetAsync(
        SandboxBuildTestProviderRequest request,
        CancellationToken cancellationToken);

    Task<SandboxBuildTestPodReference?> GetOutputCollectorAsync(
        SandboxBuildTestProviderRequest request,
        CancellationToken cancellationToken);

    Task<SandboxBuildTestPodReference> ReleaseSchedulingGateAsync(
        SandboxBuildTestProviderRequest request,
        SandboxBuildTestPodReference pod,
        CancellationToken cancellationToken);

    Task<SandboxBuildTestPodObservation> ObserveAsync(
        SandboxBuildTestProviderRequest request,
        SandboxBuildTestPodReference pod,
        int maximumOutputBytes,
        CancellationToken cancellationToken);

    Task StopAsync(
        SandboxBuildTestProviderRequest request,
        SandboxBuildTestPodReference pod,
        CancellationToken cancellationToken);

    Task DeleteAsync(
        SandboxBuildTestProviderRequest request,
        SandboxBuildTestPodReference pod,
        CancellationToken cancellationToken);
}

public static class SandboxBuildTestLimits
{
    public const string OfflineEgressProfile = "offline";
    public const string PolicyOperationLabel = "agentweaver.dev/buildtest-operation";
    public const string PolicyRoleLabel = "agentweaver.dev/buildtest-role";
    public const string CommandPolicyRole = "command";
    public const string CollectorPolicyRole = "collector";
    public const string OutputCollectorExecutable = "dotnet";
    public const string OutputCollectorAssembly = "Agentweaver.AgentHost.dll";
    public const string OutputCollectorMode = "--build-test-output-collector-v1";
    public const string OutputCollectorContainerName = "buildtest-collector";
    public const int MaximumCollectorRequestBytes = 65_536;
    public const long MaximumFileOutputBytes = 67_108_864;
    public const int MaximumLogOutputBytes = 1_048_576;
    public const int MaximumArguments = 256;
    public const int MaximumArgumentLength = 4096;
    public const int MaximumArgumentBytes = 32_768;
    public const int MaximumOutputObligations = 32;
}

internal static class SandboxBuildTestPaths
{
    internal static bool IsWorkspaceRelative(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 512 &&
        !value.StartsWith("/", StringComparison.Ordinal) &&
        !value.Contains('\\') &&
        !value.Contains(':') &&
        !value.Any(char.IsControl) &&
        (value == "." || value.Split('/').All(segment =>
            segment.Length > 0 && segment is not ("." or "..")));

    internal static bool IsAbsoluteExecutable(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length is > 1 and <= 512 &&
        value.StartsWith("/", StringComparison.Ordinal) &&
        !value.Contains('\\') &&
        !value.Contains(':') &&
        !value.Any(char.IsControl) &&
        value.Split('/').Skip(1).All(segment =>
            segment.Length > 0 && segment is not ("." or ".."));

    internal static bool IsAbsoluteContainerPath(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 512 &&
        value.StartsWith("/", StringComparison.Ordinal) &&
        !value.Contains('\\') &&
        !value.Contains(':') &&
        !value.Any(char.IsControl) &&
        (value == "/" || value.Split('/').Skip(1).All(segment =>
            segment.Length > 0 && segment is not ("." or "..")));
}

internal static class SandboxBuildTestHashes
{
    internal static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static bool IsPrefixedSha256(string? value) =>
        value is { Length: 71 } &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        IsSha256(value[7..]);

    internal static string PrefixSha256(ReadOnlySpan<byte> bytes) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal static string UnprefixedSha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

internal static class SandboxBuildTestCanonicalJson
{
    internal static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.Default,
        Indented = false,
        SkipValidation = false
    };

    internal static bool SameCheckpoint(
        SandboxBuildTestCheckpointReference left,
        SandboxBuildTestCheckpointReference right) =>
        left.ProjectId == right.ProjectId &&
        left.RunId == right.RunId &&
        left.SessionId == right.SessionId &&
        left.CheckpointId == right.CheckpointId &&
        left.WorkPlanId == right.WorkPlanId &&
        left.StepId == right.StepId &&
        left.CheckpointRevision == right.CheckpointRevision &&
        left.DecisionStateVersion == right.DecisionStateVersion &&
        left.ExecutionFence == right.ExecutionFence &&
        string.Equals(left.AcceptedSelectionHash, right.AcceptedSelectionHash, StringComparison.Ordinal);

    internal static bool Matches(
        SandboxBuildTestExpectedBinding expected,
        SandboxBuildTestObservedBinding observed,
        SandboxBuildTestCommandNetworkPolicyBinding expectedCommandPolicy) =>
        expected.Fence == observed.Fence &&
        expected.SandboxLeaseOperationId == observed.SandboxLeaseOperationId &&
        expected.SandboxResource == observed.SandboxResource &&
        expected.ProviderFencingGeneration == observed.ProviderFencingGeneration &&
        expected.WorkspaceVolume == observed.WorkspaceVolume &&
        expected.DataGeneration == observed.DataGeneration &&
        expected.NetworkPolicyGeneration == observed.NetworkPolicyGeneration &&
        SameCommandPolicy(expectedCommandPolicy, observed.CommandPolicy);

    private static bool SameCommandPolicy(
        SandboxBuildTestCommandNetworkPolicyBinding expected,
        SandboxBuildTestCommandNetworkPolicyBinding actual) =>
        expected.PolicyUid == actual.PolicyUid &&
        expected.SpecSha256 == actual.SpecSha256 &&
        expected.NetworkPolicyGeneration == actual.NetworkPolicyGeneration &&
        expected.SelectorLabels.Count == actual.SelectorLabels.Count &&
        expected.SelectorLabels.All(pair =>
            actual.SelectorLabels.TryGetValue(pair.Key, out var value) && value == pair.Value);

    internal static void WriteExpectedBinding(
        Utf8JsonWriter writer,
        SandboxBuildTestExpectedBinding binding)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("fence");
        writer.WriteStartObject();
        writer.WritePropertyName("owner");
        writer.WriteStartObject();
        writer.WriteString("tenantId", binding.Fence.Owner.TenantId);
        writer.WriteString("projectId", binding.Fence.Owner.ProjectId);
        writer.WriteString("runId", binding.Fence.Owner.RunId);
        writer.WriteString("environmentId", binding.Fence.Owner.EnvironmentId);
        writer.WriteEndObject();
        writer.WriteNumber("lifecycleGeneration", binding.Fence.LifecycleGeneration);
        writer.WriteEndObject();
        writer.WriteString("sandboxLeaseOperationId", binding.SandboxLeaseOperationId.ToString("D").ToLowerInvariant());
        writer.WritePropertyName("sandboxResource");
        writer.WriteStartObject();
        writer.WriteString("seam", binding.SandboxResource.Seam.ToString());
        writer.WriteString("providerId", binding.SandboxResource.ProviderId);
        writer.WriteString("resourceId", binding.SandboxResource.ResourceId);
        writer.WriteNumber("generation", binding.SandboxResource.Generation);
        writer.WriteEndObject();
        writer.WriteNumber("providerFencingGeneration", binding.ProviderFencingGeneration);
        writer.WritePropertyName("workspaceVolume");
        writer.WriteStartObject();
        writer.WriteString("projectId", binding.WorkspaceVolume.ProjectId);
        writer.WriteString("volumeId", binding.WorkspaceVolume.VolumeId);
        writer.WriteNumber("resourceGeneration", binding.WorkspaceVolume.ResourceGeneration);
        writer.WriteEndObject();
        writer.WriteNumber("dataGeneration", binding.DataGeneration);
        writer.WriteNumber("networkPolicyGeneration", binding.NetworkPolicyGeneration);
        writer.WritePropertyName("sandboxProviderBinding");
        writer.WriteStartObject();
        writer.WriteString("providerId", binding.SandboxProviderBinding!.ProviderId);
        writer.WriteString("adapterVersion", binding.SandboxProviderBinding.AdapterVersion);
        writer.WriteNumber("optionsSchemaVersion", binding.SandboxProviderBinding.OptionsSchemaVersion);
        writer.WriteString("optionsRevision", binding.SandboxProviderBinding.OptionsRevision);
        writer.WritePropertyName("optionsSnapshot");
        WriteJsonElement(writer, binding.SandboxProviderBinding.OptionsSnapshot);
        writer.WritePropertyName("releaseDescriptor");
        WriteJsonElement(writer, binding.SandboxProviderBinding.ReleaseDescriptor);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    internal static void WriteJsonElement(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(
                    property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteJsonElement(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteJsonElement(writer, item);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}

public static class SandboxBuildTestCommandPolicyCanonicalization
{
    public static string ComputeSpecSha256(JsonElement specification)
    {
        if (specification.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("A Cilium policy specification object is required.", nameof(specification));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, SandboxBuildTestCanonicalJson.WriterOptions))
            SandboxBuildTestCanonicalJson.WriteJsonElement(writer, specification);
        return SandboxBuildTestHashes.UnprefixedSha256(stream.ToArray());
    }
}

public static class SandboxBuildTestCanonicalization
{
    public static string ComputeImmutableHash(SandboxBuildTestAcceptedCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return command.ComputeImmutableHash();
    }

    public static string ComputeRequestFingerprint(
        SandboxBuildTestApiRequest request,
        SandboxBuildTestAcceptedCommand acceptedCommand) =>
        request.ComputeRequestFingerprint(acceptedCommand);
}
