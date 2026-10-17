using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Agentweaver.Abstractions;

/// <summary>
/// Immutable MAF checkpoint and accepted-run identity used to authorize a BuildTest command.
/// The decision version, execution fence, and accepted-selection hash prevent stale work.
/// </summary>
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

    /// <summary>Validates checkpoint identity, positive revisions/fence, and accepted-selection digest.</summary>
    /// <exception cref="ArgumentException">The checkpoint identity or accepted run pin is invalid.</exception>
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

    /// <summary>Checks a bounded opaque checkpoint identifier.</summary>
    internal static void ValidateIdentifier(string? value, string name, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > maximumLength ||
            value.Any(char.IsControl))
            throw new ArgumentException("A bounded opaque identifier is required.", name);
    }
}

/// <summary>One named output path and byte ceiling that a BuildTest command must collect.</summary>
public sealed record SandboxBuildTestOutputObligation(
    string Name,
    string RelativePath,
    bool Required,
    long MaximumBytes)
{
    /// <summary>Validates the output name, workspace-relative path, and maximum file size.</summary>
    /// <exception cref="ArgumentException">The path escapes the workspace or the size exceeds the contract.</exception>
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

/// <summary>
/// Owner-accepted execution profile for an offline BuildTest command and its separate
/// pinned output collector. Executable allowlist and resource/time bounds constrain the workload.
/// </summary>
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

    /// <summary>Validates image digests/platforms, executable allowlist, budgets, offline egress, and collector pins.</summary>
    /// <exception cref="ArgumentException">Any execution option falls outside the accepted profile contract.</exception>
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
            CollectorMode is not (SandboxBuildTestLimits.OutputCollectorMode or
                SandboxBuildTestLimits.WorkspaceCollectorMode) ||
            CollectorContainerName != SandboxBuildTestLimits.OutputCollectorContainerName)
            throw new ArgumentException("The immutable BuildTest execution options are invalid.");
        return this with
        {
            AllowedExecutables = AllowedExecutables.ToImmutableArray(),
            CollectorAssemblyArguments = CollectorAssemblyArguments.ToImmutableArray()
        };
    }

}

/// <summary>
/// Immutable owner-accepted BuildTest command. Its canonical hash binds the checkpoint,
/// executable, args, outputs, execution options, and for v2 the exact repository seed metadata.
/// </summary>
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
    /// <summary>Accepted Source Control workspace identity for version 2 commands; absent for legacy version 1.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SandboxBuildTestWorkspaceSource? WorkspaceSource { get; init; }

    /// <summary>Seed bytes used only by the trusted preparation flow and never serialized into command JSON.</summary>
    [JsonIgnore]
    public ImmutableArray<byte>? WorkspaceSeed { get; init; }

    private static readonly JsonWriterOptions CanonicalJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.Default,
        Indented = false,
        SkipValidation = false
    };

    /// <summary>Validates the accepted command and requires its version 2 seed bytes when applicable.</summary>
    /// <exception cref="ArgumentException">Command hash, workspace input, arguments, or outputs are invalid.</exception>
    public SandboxBuildTestAcceptedCommand Validate() => Validate(requireSeed: true);

    /// <summary>Validates command metadata without requiring the provider-preparation seed bytes.</summary>
    public SandboxBuildTestAcceptedCommand ValidateMetadata() => Validate(requireSeed: false);

    /// <summary>Checks contract version, immutable hash, workspace pins, arguments, and output obligations.</summary>
    private SandboxBuildTestAcceptedCommand Validate(bool requireSeed)
    {
        ArgumentNullException.ThrowIfNull(Checkpoint);
        ArgumentNullException.ThrowIfNull(ExecutionOptions);
        _ = Checkpoint.Validate();
        _ = ExecutionOptions.Validate();
        if (ContractVersion is not (1 or 2) ||
        ContractVersion == 1 && ExecutionOptions.CollectorMode != SandboxBuildTestLimits.OutputCollectorMode ||
        ContractVersion == 2 && ExecutionOptions.CollectorMode != SandboxBuildTestLimits.WorkspaceCollectorMode ||
            ContractVersion == 1 && (WorkspaceSource is not null || WorkspaceSeed is not null) ||
            ContractVersion == 2 && (WorkspaceSource is null || !WorkspaceSource.IsValid() ||
                WorkspaceSource.Identity.ProjectId != Checkpoint.ProjectId ||
                WorkspaceSource.Identity.RunId != Checkpoint.RunId ||
                WorkspaceSource.Identity.SessionId != Checkpoint.SessionId ||
                WorkspaceSource.AcceptedSelectionHash != Checkpoint.AcceptedSelectionHash ||
                requireSeed && WorkspaceSeed is null ||
                WorkspaceSeed is { } seed && (seed.IsDefault ||
                    seed.Length != WorkspaceSource.SeedByteLength ||
                    SandboxBuildTestHashes.UnprefixedSha256(seed.AsSpan()) != WorkspaceSource.SeedSha256)) ||
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
            ContractVersion == 2 && Outputs.Any(output => output.MaximumBytes > ProducedRunCaptureLimits.MaximumFileBytes) ||
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

    /// <summary>
    /// Computes the prefixed SHA-256 over deterministic JSON for all accepted command fields;
    /// version 2 includes the canonical source binding but not its raw seed bytes.
    /// </summary>
    /// <exception cref="ArgumentException">A version 2 command has no accepted workspace source.</exception>
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
            if (ContractVersion == 2)
                (WorkspaceSource ?? throw new ArgumentException(
                    "A version-2 BuildTest command requires its accepted repository seed."))
                    .WriteCanonical(writer);
            writer.WriteEndObject();
        }
        return SandboxBuildTestHashes.PrefixSha256(stream.ToArray());
    }

    /// <summary>Writes checkpoint fields in the stable form included in the accepted-command hash.</summary>
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

    /// <summary>Writes execution-profile fields in the stable form included in the accepted-command hash.</summary>
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

/// <summary>
/// Expected Environment, Sandbox, Storage, network-policy, and data-generation pins used
/// before command execution. The optional provider binding makes Sandbox options immutable.
/// </summary>
public sealed record SandboxBuildTestExpectedBinding(
    EnvironmentGenerationFence Fence,
    Guid SandboxLeaseOperationId,
    ProviderResourceRef SandboxResource,
    long ProviderFencingGeneration,
    WorkspaceVolumeReference WorkspaceVolume,
    long DataGeneration,
    long NetworkPolicyGeneration)
{
    /// <summary>Provider/options snapshot for the exact Sandbox resource used by this operation.</summary>
    public SandboxProviderBindingSnapshot? SandboxProviderBinding { get; init; }

    /// <summary>Validates scope and exact positive resource, fencing, and network generations.</summary>
    /// <exception cref="ArgumentException">The resource does not match the owner scope.</exception>
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

    /// <summary>Requires a non-null provider binding that matches the pinned Sandbox resource.</summary>
    internal void ValidateSandboxProviderBinding()
    {
        ArgumentNullException.ThrowIfNull(SandboxProviderBinding);
        _ = SandboxProviderBinding.ValidateFor(SandboxResource);
    }
}

/// <summary>API input tying a request to a durable MAF checkpoint and current expected Environment binding.</summary>
public sealed record SandboxBuildTestApiRequest(
    SandboxBuildTestCheckpointReference Checkpoint,
    SandboxBuildTestExpectedBinding ExpectedBinding)
{
    /// <summary>Checks checkpoint and provider binding scope before owner calls.</summary>
    /// <exception cref="ArgumentException">Project/run identity differs between checkpoint and owner.</exception>
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

    /// <summary>Hashes the accepted command and expected binding into a stable operation request fingerprint.</summary>
    /// <exception cref="ArgumentException">The accepted command is not the exact referenced checkpoint.</exception>
    public string ComputeRequestFingerprint(SandboxBuildTestAcceptedCommand acceptedCommand)
    {
        ArgumentNullException.ThrowIfNull(acceptedCommand);
        _ = Validate();
        _ = acceptedCommand.ValidateMetadata();
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

/// <summary>Persisted state of a BuildTest operation from reservation through completion or reconciliation.</summary>
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

/// <summary>Provider-observed lifecycle state of a command or collector Pod.</summary>
public enum SandboxBuildTestPodState
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Unknown
}

/// <summary>Kubernetes namespace/name/UID/resource-version identity for an exact BuildTest Pod.</summary>
public sealed record SandboxBuildTestPodReference(
    string KubernetesNamespace,
    string Name,
    string Uid,
    string ResourceVersion)
{
    /// <summary>Validates all Pod identity fields with their Kubernetes length bounds.</summary>
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

/// <summary>
/// Applied Network Policy identity for a command Pod. Labels bind the policy selector to
/// one BuildTest operation and the command role.
/// </summary>
public sealed record SandboxBuildTestCommandNetworkPolicyBinding(
    string PolicyUid,
    string SpecSha256,
    long NetworkPolicyGeneration,
    ImmutableDictionary<string, string> SelectorLabels)
{
    /// <summary>Validates policy UID, exact spec digest, generation, and selector labels.</summary>
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

    /// <summary>Requires selector labels to bind this policy to the exact operation and Pod role.</summary>
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

/// <summary>Observed resource, fence, workspace, policy, and current Network Policy binding before execution.</summary>
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
    /// <summary>Checks observed values against the expected binding and exact command policy generation.</summary>
    /// <exception cref="ArgumentException">Observed provider or policy pins are inconsistent.</exception>
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

/// <summary>Reason a BuildTest command container stopped.</summary>
public enum SandboxBuildTestTerminationKind
{
    Exited,
    Cancelled,
    TimedOut,
    OutputLimitExceeded
}

/// <summary>Bounded stdout/stderr bytes and digest, including truncation lower-bound evidence.</summary>
public sealed record SandboxBuildTestOutputCapture(
    string SourcePodUid,
    string ContainerName,
    ImmutableArray<byte> CapturedBytes,
    long CapturedByteCount,
    string CapturedSha256,
    bool Truncated,
    long AtLeastObservedBytes)
{
    /// <summary>Validates byte count, digest, maximum size, and truncation evidence.</summary>
    /// <exception cref="ArgumentException">The capture bytes or count/digest evidence is inconsistent.</exception>
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

/// <summary>One collector's evidence for a declared output file, including existence and captured digest.</summary>
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

/// <summary>Applied policy binding for the separate BuildTest output-collector Pod.</summary>
public sealed record SandboxBuildTestOutputCollectorNetworkPolicyBinding(
    string PolicyUid,
    string SpecSha256,
    long NetworkPolicyGeneration,
    ImmutableDictionary<string, string> SelectorLabels)
{
    /// <summary>Validates policy UID, exact spec digest, generation, and collector selector labels.</summary>
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

    /// <summary>Requires policy labels to target this operation and the collector Pod role.</summary>
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

/// <summary>
/// Bounded, read-only collector request for required output files. Version 2 commands
/// can use an empty output list because their native workspace capture has separate evidence.
/// </summary>
public sealed record SandboxBuildTestOutputCollectorRequest(
    Guid OperationId,
    string ImmutableHash,
    string RequestFingerprint,
    SandboxBuildTestCheckpointReference Checkpoint,
    string WorkspaceMountPath,
    long MaximumTotalBytes,
    ImmutableArray<SandboxBuildTestOutputObligation> Outputs)
{
    /// <summary>Accepted command version; null preserves the legacy version 1 behavior.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? AcceptedCommandContractVersion { get; init; }

    /// <summary>Validates operation fingerprints, mount path, size ceiling, and unique output obligations.</summary>
    /// <exception cref="ArgumentException">The collector request is malformed or exceeds configured bounds.</exception>
    public SandboxBuildTestOutputCollectorRequest Validate()
    {
        ArgumentNullException.ThrowIfNull(Checkpoint);
        _ = Checkpoint.Validate();
        var commandVersion = AcceptedCommandContractVersion ?? 1;
        if (commandVersion is not (1 or 2) ||
            OperationId == Guid.Empty ||
            !SandboxBuildTestHashes.IsPrefixedSha256(ImmutableHash) ||
            !SandboxBuildTestHashes.IsSha256(RequestFingerprint) ||
            !SandboxBuildTestPaths.IsAbsoluteContainerPath(WorkspaceMountPath) ||
            MaximumTotalBytes is < 1 or > SandboxBuildTestLimits.MaximumFileOutputBytes ||
            Outputs.IsDefault ||
            commandVersion == 1 && Outputs.IsEmpty ||
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

/// <summary>Collector-produced manifest proving output file observations for one accepted request and Pod.</summary>
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
    /// <summary>Checks receipt identity, Pod, each output obligation, and canonical manifest digest.</summary>
    /// <exception cref="ArgumentException">The receipt does not match its exact request or collector Pod.</exception>
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

/// <summary>Computes the stable digest used to bind an output collector receipt to its evidence.</summary>
public static class SandboxBuildTestOutputCollectorCanonicalization
{
    /// <summary>Serializes receipt fields in canonical order and hashes the resulting manifest bytes.</summary>
    /// <exception cref="ArgumentNullException">The receipt is null.</exception>
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

/// <summary>Command container termination evidence bound to its Pod UID and container name.</summary>
public sealed record SandboxBuildTestTerminalEvidence(
    string SourcePodUid,
    string ContainerName,
    SandboxBuildTestTerminationKind Kind,
    int? ExitCode,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? Reason)
{
    /// <summary>Requires a known exit code and chronological start/finish timestamps.</summary>
    /// <exception cref="ArgumentException">Termination kind, timestamps, or reason is invalid.</exception>
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

/// <summary>
/// Owner's durable operation snapshot, containing expected/current bindings, Pod and
/// collector evidence, captured output, and completion or recovery state.
/// </summary>
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
    /// <summary>Provider receipt for version 2 repository seed preparation.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SandboxBuildTestBindingPreparation.SandboxBuildTestWorkspacePreparationReceipt? WorkspacePreparation
        { get; init; }
    /// <summary>Verified immutable output capture for version 2 workspace commands.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SandboxBuildTestWorkspaceCaptureReference? WorkspaceCapture { get; init; }
    /// <summary>Journal acknowledgment that the produced-output proof was durably recorded.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProducedRunCaptureJournalEntry? WorkspaceCaptureAcknowledgment { get; init; }

    /// <summary>
    /// Checks the snapshot against the accepted command and validates all terminal, policy,
    /// output-collector, workspace-capture, and journal-acknowledgment evidence.
    /// </summary>
    /// <exception cref="ArgumentException">The operation state and its required evidence are inconsistent.</exception>
    public SandboxBuildTestOperationSnapshot Validate(
        SandboxBuildTestAcceptedCommand acceptedCommand,
        int maximumOutputBytes)
    {
        ArgumentNullException.ThrowIfNull(acceptedCommand);
        ArgumentNullException.ThrowIfNull(ExpectedBinding);
        ArgumentNullException.ThrowIfNull(ExpectedCommandPolicy);
        _ = acceptedCommand.ValidateMetadata();
        _ = ExpectedBinding.Validate();
        ExpectedBinding.ValidateSandboxProviderBinding();
        _ = ExpectedCommandPolicy.ValidateForOperation(OperationId, SandboxBuildTestLimits.CommandPolicyRole);
        if (OperationId != acceptedCommand.OperationId ||
            !string.Equals(ImmutableHash, acceptedCommand.ImmutableHash, StringComparison.Ordinal) ||
            !SandboxBuildTestHashes.IsSha256(RequestFingerprint) ||
            ExpectedCommandPolicy.NetworkPolicyGeneration != ExpectedBinding.NetworkPolicyGeneration ||
            (acceptedCommand.Outputs.IsEmpty && acceptedCommand.WorkspaceSource is null
                ? CollectorPolicy is not null
                : CollectorPolicy is null ||
                    CollectorPolicy.NetworkPolicyGeneration != ExpectedBinding.NetworkPolicyGeneration) ||
            !Enum.IsDefined(Status) ||
            UpdatedAt < CreatedAt ||
            OutputEvidence.IsDefault ||
            OutputEvidence.Any(evidence => evidence is null) ||
            FailureCode is { Length: > 128 } ||
            FailureCode?.Any(char.IsControl) == true ||
            WorkspaceCaptureAcknowledgment is not null && Status != SandboxBuildTestOperationStatus.Completed ||
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
        if (WorkspacePreparation is not null)
            _ = WorkspacePreparation.Validate(acceptedCommand, RequestFingerprint,
                Pod ?? throw new ArgumentException("A native preparation requires its exact command Pod."));
        if (WorkspaceCapture is not null)
            _ = WorkspaceCapture.Validate(acceptedCommand, RequestFingerprint,
                CollectorPod ?? throw new ArgumentException("A native capture requires its exact collector Pod."));
        if (WorkspaceCaptureAcknowledgment is not null)
            (WorkspaceCapture ?? throw new ArgumentException("A journal acknowledgment requires a native capture."))
                .ValidateAcknowledgment(acceptedCommand.WorkspaceSource ??
                    throw new ArgumentException("A journal acknowledgment requires version 2 input."),
                    WorkspaceCaptureAcknowledgment);
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

    /// <summary>Recognizes only terminal states with the separate collector evidence required after interruption.</summary>
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

    /// <summary>Checks collector evidence according to whether outputs use the v1 collector or v2 native capture.</summary>
    private bool CollectorEvidenceMatches(SandboxBuildTestAcceptedCommand acceptedCommand)
    {
        if (acceptedCommand.WorkspaceSource is not null)
            return WorkspacePreparation is not null && WorkspaceCapture is not null &&
                CollectorPod is not null && CollectorPolicy is not null && CollectorTerminal is null &&
                CollectorManifestSha256 == WorkspaceCapture.ManifestSha256 &&
                OutputEvidence.SequenceEqual(WorkspaceCapture.Outputs);
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

/// <summary>Observed Pod state, terminal evidence, and bounded captured output from the provider.</summary>
public sealed record SandboxBuildTestPodObservation(
    SandboxBuildTestPodReference Pod,
    SandboxBuildTestPodState State,
    SandboxBuildTestTerminalEvidence? Terminal,
    SandboxBuildTestOutputCapture Output)
{
    /// <summary>Validates state against terminal evidence and binds output bytes to the same Pod/container.</summary>
    /// <exception cref="ArgumentException">Pod state, termination evidence, or output binding conflicts.</exception>
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

/// <summary>Provider call input with accepted command, exact Environment binding, and command/collector policies.</summary>
public sealed record SandboxBuildTestProviderRequest(
    SandboxBuildTestAcceptedCommand AcceptedCommand,
    SandboxBuildTestExpectedBinding ExpectedBinding,
    SandboxBuildTestCommandNetworkPolicyBinding CommandPolicy)
{
    /// <summary>Separate collector policy, required whenever output collection runs.</summary>
    public SandboxBuildTestOutputCollectorNetworkPolicyBinding? CollectorPolicy { get; init; }

    /// <summary>Validates command, provider binding, operation labels, and exact applied policy generations.</summary>
    /// <exception cref="ArgumentException">The provider request is not bound to its accepted state.</exception>
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
            (AcceptedCommand.Outputs.IsEmpty && AcceptedCommand.WorkspaceSource is null
                ? CollectorPolicy is not null
                : CollectorPolicy is null ||
                    CollectorPolicy.NetworkPolicyGeneration != ExpectedBinding.NetworkPolicyGeneration))
            throw new ArgumentException("The command-policy generation does not match the expected network generation.");
        if (CollectorPolicy is not null)
            _ = CollectorPolicy.ValidateForOperation(AcceptedCommand.OperationId);
        return this;
    }
}

/// <summary>Operation snapshot and whether the BuildTest request was an idempotent replay.</summary>
public sealed record SandboxBuildTestOperationResult(
    SandboxBuildTestOperationSnapshot Operation,
    bool Replayed);

/// <summary>Trusted owner boundary for resolving, rechecking, and acknowledging accepted BuildTest commands.</summary>
public interface ISandboxBuildTestAcceptedCommandVerifier
{
    /// <summary>Loads the accepted command for the exact durable checkpoint reference.</summary>
    Task<SandboxBuildTestAcceptedCommand> ResolveAsync(
        SandboxBuildTestCheckpointReference checkpoint,
        CancellationToken cancellationToken);

    /// <summary>Rechecks that the accepted command remains current before its effect is committed.</summary>
    Task VerifyCurrentAsync(
        SandboxBuildTestAcceptedCommand acceptedCommand,
        CancellationToken cancellationToken);

    /// <summary>Verifies that produced workspace output was acknowledged by the Sessions owner.</summary>
    Task VerifyWorkspaceCaptureAcknowledgmentAsync(
        SandboxBuildTestAcceptedCommand acceptedCommand,
        SandboxBuildTestWorkspaceCaptureAcknowledgmentVerification verification,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The BuildTest capture acknowledgment verifier is not configured.");
}

/// <summary>
/// Provider boundary for gated BuildTest command/collector Pods, observed execution output,
/// and native version 2 workspace preparation/capture.
/// </summary>
public interface ISandboxBuildTestCommandProvider
{
    /// <summary>Prepares the accepted repository seed inside the trusted command workspace.</summary>
    Task<SandboxBuildTestBindingPreparation.SandboxBuildTestWorkspacePreparationReceipt?> PrepareWorkspaceAsync(
        SandboxBuildTestProviderRequest request,
        SandboxBuildTestPodReference pod,
        CancellationToken cancellationToken) =>
        throw new SandboxProviderException("buildtest_workspace_provider_unconfigured",
            "The selected provider has no native workspace preparation path.", false);

    /// <summary>Captures immutable output metadata from the prepared workspace after command completion.</summary>
    Task<SandboxBuildTestWorkspaceCaptureReference?> CaptureWorkspaceAsync(
        SandboxBuildTestProviderRequest request,
        SandboxBuildTestPodReference pod,
        SandboxBuildTestBindingPreparation.SandboxBuildTestWorkspacePreparationReceipt preparation,
        CancellationToken cancellationToken) =>
        throw new SandboxProviderException("buildtest_workspace_provider_unconfigured",
            "The selected provider has no complete native workspace capture path.", false);

    /// <summary>Reads the bytes associated with the exact verified native workspace capture.</summary>
    Task<ImmutableArray<byte>> ReadWorkspaceCaptureAsync(
        SandboxBuildTestProviderRequest request,
        SandboxBuildTestPodReference pod,
        SandboxBuildTestBindingPreparation.SandboxBuildTestWorkspacePreparationReceipt preparation,
        SandboxBuildTestWorkspaceCaptureReference capture,
        CancellationToken cancellationToken) =>
        throw new SandboxProviderException("buildtest_workspace_provider_unconfigured",
            "The selected provider cannot export the complete native workspace bytes.", false);

    /// <summary>Creates the offline command Pod behind its network-policy scheduling gate.</summary>
    Task<SandboxBuildTestPodReference> CreateGatedAsync(
        SandboxBuildTestProviderRequest request,
        CancellationToken cancellationToken);

    /// <summary>Creates the separate read-only output collector Pod behind its own policy gate.</summary>
    Task<SandboxBuildTestPodReference> CreateOutputCollectorGatedAsync(
        SandboxBuildTestProviderRequest request,
        SandboxBuildTestOutputCollectorRequest collectorRequest,
        CancellationToken cancellationToken);

    /// <summary>Reads the command Pod for an operation, or null if it has not been created.</summary>
    Task<SandboxBuildTestPodReference?> GetAsync(
        SandboxBuildTestProviderRequest request,
        CancellationToken cancellationToken);

    /// <summary>Reads the separate output collector Pod, or null if it has not been created.</summary>
    Task<SandboxBuildTestPodReference?> GetOutputCollectorAsync(
        SandboxBuildTestProviderRequest request,
        CancellationToken cancellationToken);

    /// <summary>Releases the scheduling gate only after required policy and owner checks pass.</summary>
    Task<SandboxBuildTestPodReference> ReleaseSchedulingGateAsync(
        SandboxBuildTestProviderRequest request,
        SandboxBuildTestPodReference pod,
        CancellationToken cancellationToken);

    /// <summary>Reads current Pod state and bounded output for the exact provider request.</summary>
    Task<SandboxBuildTestPodObservation> ObserveAsync(
        SandboxBuildTestProviderRequest request,
        SandboxBuildTestPodReference pod,
        int maximumOutputBytes,
        CancellationToken cancellationToken);

    /// <summary>Stops execution in the requested Pod.</summary>
    Task StopAsync(
        SandboxBuildTestProviderRequest request,
        SandboxBuildTestPodReference pod,
        CancellationToken cancellationToken);

    /// <summary>Deletes the requested Pod after its output and terminal evidence have been handled.</summary>
    Task DeleteAsync(
        SandboxBuildTestProviderRequest request,
        SandboxBuildTestPodReference pod,
        CancellationToken cancellationToken);
}

/// <summary>
/// Fixed identifiers and resource bounds shared by BuildTest producers, providers,
/// collector images, and validators.
/// </summary>
public static class SandboxBuildTestLimits
{
    /// <summary>Only egress profile accepted for command Pods.</summary>
    public const string OfflineEgressProfile = "offline";
    /// <summary>Network Policy label that binds a Pod to its operation UUID.</summary>
    public const string PolicyOperationLabel = "agentweaver.dev/buildtest-operation";
    /// <summary>Network Policy label that distinguishes command and collector roles.</summary>
    public const string PolicyRoleLabel = "agentweaver.dev/buildtest-role";
    /// <summary>Role value for the command Pod policy.</summary>
    public const string CommandPolicyRole = "command";
    /// <summary>Role value for the collector Pod policy.</summary>
    public const string CollectorPolicyRole = "collector";
    /// <summary>Approved executable used by the collector image.</summary>
    public const string OutputCollectorExecutable = "dotnet";
    /// <summary>Approved assembly used by the collector image.</summary>
    public const string OutputCollectorAssembly = "Agentweaver.AgentHost.dll";
    /// <summary>Legacy output collector mode argument.</summary>
    public const string OutputCollectorMode = "--build-test-output-collector-v1";
    /// <summary>Native workspace collector mode argument for version 2.</summary>
    public const string WorkspaceCollectorMode = "--build-test-workspace-collector-v2";
    /// <summary>Container name used to prepare repository seed bytes.</summary>
    public const string WorkspacePreparationContainerName = "buildtest-workspace-prepare";
    /// <summary>Container name used to capture version 2 workspace output.</summary>
    public const string WorkspaceCaptureContainerName = "buildtest-workspace-collector";
    /// <summary>Container name used by the legacy output collector.</summary>
    public const string OutputCollectorContainerName = "buildtest-collector";
    /// <summary>Maximum serialized collector request size in bytes.</summary>
    public const int MaximumCollectorRequestBytes = 65_536;
    /// <summary>Maximum total file output size in bytes.</summary>
    public const long MaximumFileOutputBytes = 67_108_864;
    /// <summary>Maximum captured log bytes.</summary>
    public const int MaximumLogOutputBytes = 1_048_576;
    /// <summary>Maximum command argument count.</summary>
    public const int MaximumArguments = 256;
    /// <summary>Maximum length of one argument string.</summary>
    public const int MaximumArgumentLength = 4096;
    /// <summary>Maximum combined UTF-8 bytes across command arguments.</summary>
    public const int MaximumArgumentBytes = 32_768;
    /// <summary>Maximum requested output-file count.</summary>
    public const int MaximumOutputObligations = 32;
}

/// <summary>Path-shape checks shared by BuildTest command, workspace, and collector contracts.</summary>
internal static class SandboxBuildTestPaths
{
    /// <summary>Returns whether a path uses safe workspace-relative segments.</summary>
    internal static bool IsWorkspaceRelative(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 512 &&
        !value.StartsWith("/", StringComparison.Ordinal) &&
        !value.Contains('\\') &&
        !value.Contains(':') &&
        !value.Any(char.IsControl) &&
        (value == "." || value.Split('/').All(segment =>
            segment.Length > 0 && segment is not ("." or "..")));

    /// <summary>Returns whether an executable is a safe absolute POSIX path.</summary>
    internal static bool IsAbsoluteExecutable(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length is > 1 and <= 512 &&
        value.StartsWith("/", StringComparison.Ordinal) &&
        !value.Contains('\\') &&
        !value.Contains(':') &&
        !value.Any(char.IsControl) &&
        value.Split('/').Skip(1).All(segment =>
            segment.Length > 0 && segment is not ("." or ".."));

    /// <summary>Returns whether a mount path is a safe absolute container path.</summary>
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

/// <summary>Hash-format checks and canonical SHA-256 helpers for BuildTest contracts.</summary>
internal static class SandboxBuildTestHashes
{
    /// <summary>Checks a canonical lowercase 64-character SHA-256 value.</summary>
    internal static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>Checks a lowercase SHA-256 value with the conventional <c>sha256:</c> prefix.</summary>
    internal static bool IsPrefixedSha256(string? value) =>
        value is { Length: 71 } &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        IsSha256(value[7..]);

    /// <summary>Hashes bytes and returns the digest with a <c>sha256:</c> prefix.</summary>
    internal static string PrefixSha256(ReadOnlySpan<byte> bytes) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>Hashes bytes and returns the lowercase digest without a prefix.</summary>
    internal static string UnprefixedSha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

/// <summary>Canonical JSON writer and exact equality helpers shared by BuildTest owner and provider flows.</summary>
internal static class SandboxBuildTestCanonicalJson
{
    /// <summary>Stable UTF-8 JSON output settings used for BuildTest hashes.</summary>
    internal static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.Default,
        Indented = false,
        SkipValidation = false
    };

    /// <summary>Compares every checkpoint identity, version, fence, and accepted-selection pin.</summary>
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

    /// <summary>Compares expected and observed runtime resources, generations, and command policy.</summary>
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

    /// <summary>Compares policy identity, digest, generation, and selector labels.</summary>
    private static bool SameCommandPolicy(
        SandboxBuildTestCommandNetworkPolicyBinding expected,
        SandboxBuildTestCommandNetworkPolicyBinding actual) =>
        expected.PolicyUid == actual.PolicyUid &&
        expected.SpecSha256 == actual.SpecSha256 &&
        expected.NetworkPolicyGeneration == actual.NetworkPolicyGeneration &&
        expected.SelectorLabels.Count == actual.SelectorLabels.Count &&
        expected.SelectorLabels.All(pair =>
            actual.SelectorLabels.TryGetValue(pair.Key, out var value) && value == pair.Value);

    /// <summary>Writes the expected provider and owner binding in deterministic JSON field order.</summary>
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

    /// <summary>Writes JSON recursively with object properties sorted ordinally.</summary>
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

/// <summary>Computes stable digests for applied Cilium policy specifications.</summary>
public static class SandboxBuildTestCommandPolicyCanonicalization
{
    /// <summary>Hashes a JSON policy object after recursively sorting its object properties.</summary>
    /// <exception cref="ArgumentException">The policy specification is not a JSON object.</exception>
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

/// <summary>Public hash helpers for accepted commands and their API requests.</summary>
public static class SandboxBuildTestCanonicalization
{
    /// <summary>Returns the immutable canonical hash of an accepted command.</summary>
    public static string ComputeImmutableHash(SandboxBuildTestAcceptedCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return command.ComputeImmutableHash();
    }

    /// <summary>Returns the canonical request fingerprint for an accepted command and expected binding.</summary>
    public static string ComputeRequestFingerprint(
        SandboxBuildTestApiRequest request,
        SandboxBuildTestAcceptedCommand acceptedCommand) =>
        request.ComputeRequestFingerprint(acceptedCommand);
}
