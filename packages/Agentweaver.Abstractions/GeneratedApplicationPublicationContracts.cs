using System.Globalization;

namespace Agentweaver.Abstractions;

/// <summary>Version and credential purpose shared by generated-application publication owners.</summary>
public static class GeneratedApplicationPublicationContract
{
    /// <summary>Publication contract version currently accepted.</summary>
    public const int ContractVersion = 1;
    /// <summary>Identity redemption purpose for the configured registry publishing credential.</summary>
    public const string RegistryCredentialPurpose = "generated-application.registry-publish";
}

/// <summary>
/// Immutable source and authorization pins for publication. It binds the operation to
/// a captured repository output, current Environment fence, actor, selection, and owner revisions.
/// </summary>
public sealed record GeneratedApplicationPublicationSourceBinding
{
    /// <summary>Creates a binding after validating source identity, revisions, hashes, and Git object IDs.</summary>
    /// <exception cref="ArgumentException">A source pin is malformed or unbounded.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A revision, fence, or resource generation is invalid.</exception>
    public GeneratedApplicationPublicationSourceBinding(
        EnvironmentGenerationFence environmentFence,
        string sourceSessionId,
        string actorIssuer,
        string actorSubject,
        string acceptedSelectionHash,
        long projectRevision,
        long projectConfigurationRevision,
        long platformRuntimeRevision,
        string contextRevision,
        long executionFence,
        long ownerStateVersion,
        long decisionStateVersion,
        string captureId,
        string sourceProofSha256,
        string sourceManifestSha256,
        string sourcePackageSha256,
        string sourceControlPinId,
        string workspaceId,
        Guid workspaceIncarnationId,
        string branchName,
        string repositoryId,
        long resourceGeneration,
        string baseSha,
        string outputTreeSha)
    {
        EnvironmentFence = environmentFence ?? throw new ArgumentNullException(nameof(environmentFence));
        SourceSessionId = ValidateText(sourceSessionId, 256, nameof(sourceSessionId));
        ActorIssuer = ValidateText(actorIssuer, 512, nameof(actorIssuer));
        ActorSubject = ValidateText(actorSubject, 256, nameof(actorSubject));
        AcceptedSelectionHash = NormalizeHash(acceptedSelectionHash, nameof(acceptedSelectionHash));
        if (projectRevision < 0 || projectConfigurationRevision < 0 || platformRuntimeRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(projectRevision));
        ProjectRevision = projectRevision;
        ProjectConfigurationRevision = projectConfigurationRevision;
        PlatformRuntimeRevision = platformRuntimeRevision;
        ContextRevision = ValidateText(contextRevision, 256, nameof(contextRevision));
        if (executionFence < 1 || ownerStateVersion < 1 || decisionStateVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(executionFence));
        ExecutionFence = executionFence;
        OwnerStateVersion = ownerStateVersion;
        DecisionStateVersion = decisionStateVersion;
        if (captureId is not { Length: 42 } ||
            !captureId.StartsWith("sc-output-", StringComparison.Ordinal) ||
            captureId[10..].Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("A produced-run capture ID is required.", nameof(captureId));
        CaptureId = captureId;
        SourceProofSha256 = NormalizeHash(sourceProofSha256, nameof(sourceProofSha256));
        SourceManifestSha256 = NormalizeHash(sourceManifestSha256, nameof(sourceManifestSha256));
        SourcePackageSha256 = NormalizeHash(sourcePackageSha256, nameof(sourcePackageSha256));
        SourceControlPinId = ValidateIdentifier(sourceControlPinId, 128, nameof(sourceControlPinId));
        WorkspaceId = ValidateIdentifier(workspaceId, 128, nameof(workspaceId));
        if (workspaceIncarnationId == Guid.Empty)
            throw new ArgumentException("A workspace incarnation is required.", nameof(workspaceIncarnationId));
        WorkspaceIncarnationId = workspaceIncarnationId;
        BranchName = ValidateText(branchName, 255, nameof(branchName));
        RepositoryId = ValidateIdentifier(repositoryId, 256, nameof(repositoryId));
        if (resourceGeneration < 1)
            throw new ArgumentOutOfRangeException(nameof(resourceGeneration));
        ResourceGeneration = resourceGeneration;
        BaseSha = ValidateGitObjectId(baseSha, nameof(baseSha));
        OutputTreeSha = ValidateGitObjectId(outputTreeSha, nameof(outputTreeSha));
    }

    /// <summary>
    /// Builds a binding from a validated Source Control capture and current owner context,
    /// rejecting a different tenant, project, run, actor, or accepted selection.
    /// </summary>
    /// <exception cref="ArgumentException">The capture and current Environment authority do not match.</exception>
    public static GeneratedApplicationPublicationSourceBinding FromCurrentCapture(
        EnvironmentGenerationFence environmentFence,
        ProducedRunCaptureProof capture,
        string currentActorIssuer,
        string currentActorSubject,
        string currentAcceptedSelectionHash,
        long currentProjectRevision,
        long currentProjectConfigurationRevision,
        long currentPlatformRuntimeRevision,
        string currentContextRevision,
        long currentExecutionFence,
        long currentOwnerStateVersion,
        long currentDecisionStateVersion)
    {
        ArgumentNullException.ThrowIfNull(environmentFence);
        ArgumentNullException.ThrowIfNull(capture);
        ProducedRunCaptureContractValidation.Validate(capture);
        var owner = environmentFence.Owner;
        if (capture.Identity.ProjectId != owner.ProjectId ||
            capture.Identity.RunId != owner.RunId ||
            capture.TenantId != owner.TenantId ||
            currentActorIssuer != capture.ActorIssuer ||
            currentActorSubject != capture.ActorSubject ||
            !string.Equals(
                currentAcceptedSelectionHash,
                capture.AcceptedSelectionHash,
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                "The source capture and current Environment owner context are not bound to the same authority.",
                nameof(capture));

        return new(
            environmentFence,
            capture.Identity.SessionId,
            capture.ActorIssuer,
            capture.ActorSubject,
            capture.AcceptedSelectionHash,
            currentProjectRevision,
            currentProjectConfigurationRevision,
            currentPlatformRuntimeRevision,
            currentContextRevision,
            currentExecutionFence,
            currentOwnerStateVersion,
            currentDecisionStateVersion,
            capture.CaptureId,
            GeneratedApplicationPublicationValidation.ComputeSourceProofSha256(capture),
            capture.ManifestSha256,
            capture.PackageSha256,
            capture.SourceControlPinId,
            capture.WorkspaceId,
            capture.WorkspaceIncarnationId,
            capture.BranchName,
            capture.RepositoryId,
            capture.ResourceGeneration,
            capture.BaseSha,
            capture.OutputTreeSha);
    }

    public EnvironmentGenerationFence EnvironmentFence { get; }
    public string SourceSessionId { get; }
    public string ActorIssuer { get; }
    public string ActorSubject { get; }
    public string AcceptedSelectionHash { get; }
    public long ProjectRevision { get; }
    public long ProjectConfigurationRevision { get; }
    public long PlatformRuntimeRevision { get; }
    public string ContextRevision { get; }
    public long ExecutionFence { get; }
    public long OwnerStateVersion { get; }
    public long DecisionStateVersion { get; }
    public string CaptureId { get; }
    /// <summary>Digest of the full source proof, including all repository and output pins.</summary>
    public string SourceProofSha256 { get; }
    public string SourceManifestSha256 { get; }
    public string SourcePackageSha256 { get; }
    public string SourceControlPinId { get; }
    public string WorkspaceId { get; }
    public Guid WorkspaceIncarnationId { get; }
    public string BranchName { get; }
    public string RepositoryId { get; }
    public long ResourceGeneration { get; }
    public string BaseSha { get; }
    public string OutputTreeSha { get; }

    /// <summary>Compares every source and authority pin to prevent mutation between publication stages.</summary>
    public bool Matches(GeneratedApplicationPublicationSourceBinding? other) =>
        other is not null &&
        EnvironmentFence == other.EnvironmentFence &&
        SourceSessionId == other.SourceSessionId &&
        ActorIssuer == other.ActorIssuer &&
        ActorSubject == other.ActorSubject &&
        AcceptedSelectionHash == other.AcceptedSelectionHash &&
        ProjectRevision == other.ProjectRevision &&
        ProjectConfigurationRevision == other.ProjectConfigurationRevision &&
        PlatformRuntimeRevision == other.PlatformRuntimeRevision &&
        ContextRevision == other.ContextRevision &&
        ExecutionFence == other.ExecutionFence &&
        OwnerStateVersion == other.OwnerStateVersion &&
        DecisionStateVersion == other.DecisionStateVersion &&
        CaptureId == other.CaptureId &&
        SourceProofSha256 == other.SourceProofSha256 &&
        SourceManifestSha256 == other.SourceManifestSha256 &&
        SourcePackageSha256 == other.SourcePackageSha256 &&
        SourceControlPinId == other.SourceControlPinId &&
        WorkspaceId == other.WorkspaceId &&
        WorkspaceIncarnationId == other.WorkspaceIncarnationId &&
        BranchName == other.BranchName &&
        RepositoryId == other.RepositoryId &&
        ResourceGeneration == other.ResourceGeneration &&
        BaseSha == other.BaseSha &&
        OutputTreeSha == other.OutputTreeSha;

    /// <summary>Digest of the current owner identity and revision/fence proof.</summary>
    public string OwnerReadProofSha256 =>
        GeneratedApplicationPublicationValidation.ComputeOwnerReadProofSha256(this);

    /// <summary>Validates a SHA-256 string and stores its canonical lowercase representation.</summary>
    private static string NormalizeHash(string? value, string paramName)
    {
        if (value is not { Length: 64 } ||
            value.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("A SHA-256 hash is required.", paramName);
        return value.ToLowerInvariant();
    }

    /// <summary>Checks a bounded provider-style identifier.</summary>
    private static string ValidateIdentifier(string? value, int maximumLength, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                character is not ('-' or '_' or '.' or ':' or '/')))
            throw new ArgumentException("A bounded identifier is required.", paramName);
        return value;
    }

    /// <summary>Checks bounded nonempty text without control characters.</summary>
    private static string ValidateText(string? value, int maximumLength, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength ||
            value.Any(char.IsControl))
            throw new ArgumentException("A bounded text value is required.", paramName);
        return value;
    }

    /// <summary>Validates a Git object ID and stores its lowercase representation.</summary>
    private static string ValidateGitObjectId(string? value, string paramName)
    {
        if (value is not { Length: 40 or 64 } ||
            value.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("A Git object ID is required.", paramName);
        return value.ToLowerInvariant();
    }
}

/// <summary>OCI artifact reference and measured image identity exported by the Build/Test executor.</summary>
public sealed record GeneratedApplicationOciArtifact
{
    /// <summary>Creates an artifact reference and validates the image digest, platform, and compressed size.</summary>
    /// <exception cref="ArgumentException">An artifact reference or image identity is invalid.</exception>
    public GeneratedApplicationOciArtifact(
        string artifactStoreId,
        string handle,
        string imageDigest,
        string platform,
        long compressedBytes)
    {
        ArtifactStoreId = ValidateIdentifier(artifactStoreId, nameof(artifactStoreId));
        Handle = ValidateIdentifier(handle, nameof(handle));
        ImageDigest = imageDigest;
        Platform = platform;
        CompressedBytes = compressedBytes;
        _ = new SandboxImageIdentity(imageDigest, platform, compressedBytes).Validate();
    }

    public string ArtifactStoreId { get; }
    public string Handle { get; }
    public string ImageDigest { get; }
    public string Platform { get; }
    public long CompressedBytes { get; }

    /// <summary>Checks an opaque artifact-store identifier or handle.</summary>
    private static string ValidateIdentifier(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl))
            throw new ArgumentException("An opaque bounded artifact reference is required.", paramName);
        return value;
    }
}

/// <summary>Outcome of a generated application build and test operation.</summary>
public enum GeneratedApplicationBuildTestOutcome
{
    Succeeded,
    BuildFailed,
    TestsFailed
}

/// <summary>
/// Immutable Build/Test evidence for one publication operation. Only a successful outcome
/// may include an exported OCI artifact.
/// </summary>
public sealed record GeneratedApplicationBuildTestEvidence
{
    /// <summary>Creates evidence bound to an exact request fingerprint and source binding.</summary>
    /// <exception cref="ArgumentException">The evidence identity, fingerprint, outcome, or artifact does not match.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The outcome or completion time is invalid.</exception>
    public GeneratedApplicationBuildTestEvidence(
        Guid operationId,
        string operationRequestFingerprint,
        string evidenceReference,
        string evidenceSha256,
        GeneratedApplicationBuildTestOutcome outcome,
        GeneratedApplicationPublicationSourceBinding source,
        GeneratedApplicationOciArtifact? artifact,
        DateTimeOffset completedAt)
    {
        if (operationId == Guid.Empty)
            throw new ArgumentException("A publication operation ID is required.", nameof(operationId));
        OperationId = operationId;
        if (!GeneratedApplicationPublicationValidation.IsSha256(operationRequestFingerprint))
            throw new ArgumentException(
                "A publication operation request fingerprint is required.",
                nameof(operationRequestFingerprint));
        OperationRequestFingerprint = operationRequestFingerprint.ToLowerInvariant();
        EvidenceReference = ValidateReference(evidenceReference, nameof(evidenceReference));
        if (evidenceSha256 is not { Length: 64 } ||
            evidenceSha256.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("A SHA-256 evidence digest is required.", nameof(evidenceSha256));
        EvidenceSha256 = evidenceSha256.ToLowerInvariant();
        if (!Enum.IsDefined(outcome))
            throw new ArgumentOutOfRangeException(nameof(outcome));
        Outcome = outcome;
        Source = source ?? throw new ArgumentNullException(nameof(source));
        if ((outcome == GeneratedApplicationBuildTestOutcome.Succeeded) != (artifact is not null))
            throw new ArgumentException(
                "Only successful Build/Test evidence may reference an exported OCI artifact.",
                nameof(artifact));
        Artifact = artifact;
        if (completedAt == default)
            throw new ArgumentOutOfRangeException(nameof(completedAt));
        CompletedAt = completedAt;
    }

    public Guid OperationId { get; }
    public string OperationRequestFingerprint { get; }
    public string EvidenceReference { get; }
    public string EvidenceSha256 { get; }
    public GeneratedApplicationBuildTestOutcome Outcome { get; }
    public GeneratedApplicationPublicationSourceBinding Source { get; }
    public GeneratedApplicationOciArtifact? Artifact { get; }
    public DateTimeOffset CompletedAt { get; }

    /// <summary>Checks a bounded immutable evidence reference.</summary>
    private static string ValidateReference(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl))
            throw new ArgumentException("An immutable evidence reference is required.", paramName);
        return value;
    }
}

/// <summary>Build/Test executor input with an operation ID and canonical publication fingerprint.</summary>
public sealed record GeneratedApplicationBuildTestRequest
{
    /// <summary>Creates a stable operation request after requiring a nonempty operation ID.</summary>
    /// <exception cref="ArgumentException">The operation ID is empty or publication request is null.</exception>
    public GeneratedApplicationBuildTestRequest(
        Guid operationId,
        GeneratedApplicationPublicationRequest publication)
    {
        if (operationId == Guid.Empty)
            throw new ArgumentException("A publication operation ID is required.", nameof(operationId));
        OperationId = operationId;
        Publication = publication ?? throw new ArgumentNullException(nameof(publication));
        RequestFingerprint = GeneratedApplicationPublicationValidation.Fingerprint(publication);
    }

    public Guid OperationId { get; }
    public GeneratedApplicationPublicationRequest Publication { get; }
    public string RequestFingerprint { get; }
}

/// <summary>Executes and returns evidence for the requested application build and tests.</summary>
public interface IGeneratedApplicationBuildTestExecutor
{
    /// <summary>Runs Build/Test for the exact publication request and returns its evidence.</summary>
    Task<GeneratedApplicationBuildTestEvidence> ExecuteAsync(
        GeneratedApplicationBuildTestRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Requested source-bound publication target and caller-provided idempotency key.</summary>
public sealed record GeneratedApplicationPublicationRequest
{
    /// <summary>Creates a publication request for one source capture and configured registry target.</summary>
    /// <exception cref="ArgumentException">The application, target, or idempotency key is invalid.</exception>
    public GeneratedApplicationPublicationRequest(
        GeneratedApplicationPublicationSourceBinding source,
        string applicationId,
        string registryTargetId,
        string idempotencyKey)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        ApplicationId = ValidateText(applicationId, 256, nameof(applicationId));
        RegistryTargetId = ValidateIdentifier(registryTargetId, nameof(registryTargetId));
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128 ||
            idempotencyKey.Any(char.IsControl))
            throw new ArgumentException("A bounded idempotency key is required.", nameof(idempotencyKey));
        IdempotencyKey = idempotencyKey;
    }

    public GeneratedApplicationPublicationSourceBinding Source { get; }
    public string ApplicationId { get; }
    public string RegistryTargetId { get; }
    public string IdempotencyKey { get; }

    /// <summary>Validates an identifier using the publication target's allowed character set.</summary>
    private static string ValidateIdentifier(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                character is not ('-' or '_' or '.' or ':' or '/')))
            throw new ArgumentException("A bounded identifier is required.", paramName);
        return value;
    }

    /// <summary>Checks a bounded human-readable application identifier.</summary>
    private static string ValidateText(string value, int maximumLength, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength ||
            value.Any(char.IsControl))
            throw new ArgumentException("A bounded text value is required.", paramName);
        return value;
    }
}

/// <summary>
/// Expiring, decision-backed approval for one exact source capture, application, registry
/// target, and successful Build/Test evidence digest.
/// </summary>
public sealed record GeneratedApplicationPublicationApproval
{
    /// <summary>Creates an approval only when its references and digests are well formed.</summary>
    /// <exception cref="ArgumentException">A decision, source, application, target, or evidence digest is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The approval expiry is missing.</exception>
    public GeneratedApplicationPublicationApproval(
        string decisionReference,
        string decisionSha256,
        GeneratedApplicationPublicationSourceBinding source,
        string applicationId,
        string registryTargetId,
        string buildTestEvidenceSha256,
        DateTimeOffset expiresAt)
    {
        if (string.IsNullOrWhiteSpace(decisionReference) || decisionReference.Length > 512 ||
            decisionReference.Any(char.IsControl))
            throw new ArgumentException("An immutable gate-decision reference is required.", nameof(decisionReference));
        DecisionReference = decisionReference;
        if (decisionSha256 is not { Length: 64 } ||
            decisionSha256.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("A SHA-256 decision digest is required.", nameof(decisionSha256));
        DecisionSha256 = decisionSha256.ToLowerInvariant();
        Source = source ?? throw new ArgumentNullException(nameof(source));
        if (string.IsNullOrWhiteSpace(applicationId) || applicationId.Length > 256 ||
            applicationId.Any(char.IsControl))
            throw new ArgumentException("A bounded application ID is required.", nameof(applicationId));
        ApplicationId = applicationId;
        if (string.IsNullOrWhiteSpace(registryTargetId) || registryTargetId.Length > 128 ||
            registryTargetId.Any(char.IsControl))
            throw new ArgumentException("A configured registry target ID is required.", nameof(registryTargetId));
        RegistryTargetId = registryTargetId;
        if (!GeneratedApplicationPublicationValidation.IsSha256(buildTestEvidenceSha256))
            throw new ArgumentException("A Build/Test evidence digest is required.", nameof(buildTestEvidenceSha256));
        BuildTestEvidenceSha256 = buildTestEvidenceSha256.ToLowerInvariant();
        if (expiresAt == default)
            throw new ArgumentOutOfRangeException(nameof(expiresAt));
        ExpiresAt = expiresAt;
    }

    public string DecisionReference { get; }
    public string DecisionSha256 { get; }
    public GeneratedApplicationPublicationSourceBinding Source { get; }
    public string ApplicationId { get; }
    public string RegistryTargetId { get; }
    public string BuildTestEvidenceSha256 { get; }
    public DateTimeOffset ExpiresAt { get; }

    /// <summary>Checks the approval is for the exact publication request and successful build evidence.</summary>
    public bool BindsTo(
        GeneratedApplicationPublicationRequest request,
        GeneratedApplicationBuildTestEvidence evidence) =>
        Source.Matches(request.Source) &&
        ApplicationId == request.ApplicationId &&
        RegistryTargetId == request.RegistryTargetId &&
        BuildTestEvidenceSha256 == evidence.EvidenceSha256;
}

/// <summary>Validated registry push request combining one publication, exact Build/Test evidence, and approval.</summary>
public sealed record GeneratedApplicationRegistryPublishRequest
{
    /// <summary>
    /// Creates a registry request only when operation fingerprint, successful artifact evidence,
    /// source binding, and approval all refer to the same operation.
    /// </summary>
    /// <exception cref="ArgumentException">The operation, evidence, artifact, or approval is stale or mismatched.</exception>
    public GeneratedApplicationRegistryPublishRequest(
        Guid operationId,
        string requestFingerprint,
        GeneratedApplicationPublicationRequest publication,
        GeneratedApplicationBuildTestEvidence evidence,
        GeneratedApplicationPublicationApproval approval)
    {
        if (operationId == Guid.Empty)
            throw new ArgumentException("A publication operation ID is required.", nameof(operationId));
        OperationId = operationId;
        if (!GeneratedApplicationPublicationValidation.IsSha256(requestFingerprint))
            throw new ArgumentException("A request fingerprint is required.", nameof(requestFingerprint));
        RequestFingerprint = requestFingerprint.ToLowerInvariant();
        Publication = publication ?? throw new ArgumentNullException(nameof(publication));
        Evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        Approval = approval ?? throw new ArgumentNullException(nameof(approval));
        if (RequestFingerprint != GeneratedApplicationPublicationValidation.Fingerprint(publication))
            throw new ArgumentException("The publication request fingerprint does not match.", nameof(requestFingerprint));
        if (evidence.OperationId != operationId ||
            evidence.OperationRequestFingerprint != RequestFingerprint ||
            evidence.Outcome != GeneratedApplicationBuildTestOutcome.Succeeded ||
            evidence.Artifact is null ||
            !evidence.Source.Matches(publication.Source) ||
            !approval.BindsTo(publication, evidence))
            throw new ArgumentException(
                "An approval and successful exact-operation Build/Test evidence are required.",
                nameof(evidence));
        Artifact = evidence.Artifact;
    }

    public Guid OperationId { get; }
    public string RequestFingerprint { get; }
    public GeneratedApplicationPublicationRequest Publication { get; }
    public GeneratedApplicationBuildTestEvidence Evidence { get; }
    public GeneratedApplicationPublicationApproval Approval { get; }
    public GeneratedApplicationOciArtifact Artifact { get; }
}

/// <summary>Provider-measured registry digest and time for one publication operation.</summary>
public sealed record GeneratedApplicationRegistryPublishResult
{
    /// <summary>Creates a result after checking its operation fingerprint, target, image digest, and timestamp.</summary>
    /// <exception cref="ArgumentException">An operation or image identity is malformed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The timestamp is missing.</exception>
    public GeneratedApplicationRegistryPublishResult(
        Guid operationId,
        string requestFingerprint,
        string registryTargetId,
        string registryDigest,
        DateTimeOffset measuredAt)
    {
        if (operationId == Guid.Empty)
            throw new ArgumentException("A publication operation ID is required.", nameof(operationId));
        OperationId = operationId;
        if (!GeneratedApplicationPublicationValidation.IsSha256(requestFingerprint))
            throw new ArgumentException("A request fingerprint is required.", nameof(requestFingerprint));
        RequestFingerprint = requestFingerprint.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(registryTargetId) || registryTargetId.Length > 128 ||
            registryTargetId.Any(char.IsControl))
            throw new ArgumentException("A configured registry target ID is required.", nameof(registryTargetId));
        RegistryTargetId = registryTargetId;
        if (!GeneratedApplicationPublicationValidation.IsImageDigest(registryDigest))
            throw new ArgumentException("A measured registry image digest is required.", nameof(registryDigest));
        RegistryDigest = registryDigest;
        if (measuredAt == default)
            throw new ArgumentOutOfRangeException(nameof(measuredAt));
        MeasuredAt = measuredAt;
    }

    public Guid OperationId { get; }
    public string RequestFingerprint { get; }
    public string RegistryTargetId { get; }
    public string RegistryDigest { get; }
    public DateTimeOffset MeasuredAt { get; }
}

/// <summary>Registry observation states used when reconciling a publication with uncertain outcome.</summary>
public enum GeneratedApplicationRegistryObservationState
{
    ExactDigestPresent,
    NotFound,
    ConflictingDigest,
    Unknown
}

/// <summary>
/// Read-only registry observation for one operation. A measured digest is required only
/// when an object was found, whether exact or conflicting.
/// </summary>
public sealed record GeneratedApplicationRegistryObservation
{
    /// <summary>Creates an observation bound to the publication request and observed registry state.</summary>
    /// <exception cref="ArgumentException">The operation, target, state, or digest is inconsistent.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The state or observation time is invalid.</exception>
    public GeneratedApplicationRegistryObservation(
        Guid operationId,
        string requestFingerprint,
        string registryTargetId,
        GeneratedApplicationRegistryObservationState state,
        string? measuredDigest,
        DateTimeOffset observedAt)
    {
        if (operationId == Guid.Empty)
            throw new ArgumentException("A publication operation ID is required.", nameof(operationId));
        OperationId = operationId;
        if (!GeneratedApplicationPublicationValidation.IsSha256(requestFingerprint))
            throw new ArgumentException("A request fingerprint is required.", nameof(requestFingerprint));
        RequestFingerprint = requestFingerprint.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(registryTargetId) || registryTargetId.Length > 128 ||
            registryTargetId.Any(char.IsControl))
            throw new ArgumentException("A configured registry target ID is required.", nameof(registryTargetId));
        RegistryTargetId = registryTargetId;
        if (!Enum.IsDefined(state))
            throw new ArgumentOutOfRangeException(nameof(state));
        if ((state is GeneratedApplicationRegistryObservationState.ExactDigestPresent or
                GeneratedApplicationRegistryObservationState.ConflictingDigest) !=
            (measuredDigest is not null))
            throw new ArgumentException("A measured digest is required only for a registry object observation.",
                nameof(measuredDigest));
        if (measuredDigest is not null &&
            !GeneratedApplicationPublicationValidation.IsImageDigest(measuredDigest))
            throw new ArgumentException("A measured registry image digest is required.", nameof(measuredDigest));
        State = state;
        MeasuredDigest = measuredDigest;
        if (observedAt == default)
            throw new ArgumentOutOfRangeException(nameof(observedAt));
        ObservedAt = observedAt;
    }

    public Guid OperationId { get; }
    public string RequestFingerprint { get; }
    public string RegistryTargetId { get; }
    public GeneratedApplicationRegistryObservationState State { get; }
    public string? MeasuredDigest { get; }
    public DateTimeOffset ObservedAt { get; }
}

/// <summary>Provider boundary that publishes an approved artifact or observes registry state for reconciliation.</summary>
public interface IGeneratedApplicationRegistryPublisher
{
    /// <summary>Pushes the approved artifact using the supplied short-lived registry credential.</summary>
    Task<GeneratedApplicationRegistryPublishResult> PublishAsync(
        GeneratedApplicationRegistryPublishRequest request,
        SecretCredential credential,
        CancellationToken cancellationToken);

    /// <summary>Reads registry state without publishing, to resolve an uncertain previous publish attempt.</summary>
    Task<GeneratedApplicationRegistryObservation> ObserveAsync(
        GeneratedApplicationRegistryPublishRequest request,
        SecretCredential credential,
        CancellationToken cancellationToken);
}

/// <summary>Durable states of the owner-managed build, approval, publish, and reconciliation flow.</summary>
public enum GeneratedApplicationPublicationOperationState
{
    BuildTestReserved,
    BuildTestInFlight,
    BuildTestOutcomeUnknown,
    BuildTestSucceeded,
    BuildTestFailed,
    AwaitingApproval,
    ApprovalUnavailable,
    CredentialUnavailable,
    PublisherUnavailable,
    Publishing,
    PublicationOutcomeUnknown,
    ReconciliationRequired,
    Published,
    Failed
}

/// <summary>Persisted publication request state and evidence owned by the publication service.</summary>
public sealed record GeneratedApplicationPublicationOperation(
    Guid OperationId,
    GeneratedApplicationPublicationRequest Request,
    GeneratedApplicationPublicationOperationState State,
    long Revision,
    GeneratedApplicationBuildTestEvidence? BuildTestEvidence,
    GeneratedApplicationPublicationApproval? Approval,
    string? RegistryDigest,
    string? FailureCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Reservation response for a publication operation, including idempotent replay state.</summary>
public sealed record GeneratedApplicationPublicationReservation(
    GeneratedApplicationPublicationOperation Operation,
    bool Replayed);

/// <summary>Completion response for a publication operation, including idempotent replay state.</summary>
public sealed record GeneratedApplicationPublicationCompletion(
    GeneratedApplicationPublicationOperation Operation,
    bool Replayed);

/// <summary>Fingerprint and digest functions shared by generated-application publication owners.</summary>
public static class GeneratedApplicationPublicationValidation
{
    /// <summary>Returns whether the input is a 64-character hexadecimal SHA-256 digest.</summary>
    public static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    public static bool IsImageDigest(string? value) =>
        value is { Length: 71 } &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value[7..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>Computes a deterministic digest over target and every source/owner pin in the request.</summary>
    /// <exception cref="ArgumentNullException">The request is null.</exception>
    public static string Fingerprint(GeneratedApplicationPublicationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var source = request.Source;
        var material = string.Join('\0',
            request.ApplicationId,
            request.RegistryTargetId,
            source.EnvironmentFence.Owner.TenantId,
            source.EnvironmentFence.Owner.ProjectId,
            source.EnvironmentFence.Owner.RunId,
            source.EnvironmentFence.Owner.EnvironmentId,
            source.EnvironmentFence.LifecycleGeneration.ToString(CultureInfo.InvariantCulture),
            source.SourceSessionId,
            source.ActorIssuer,
            source.ActorSubject,
            source.AcceptedSelectionHash,
            source.ProjectRevision.ToString(CultureInfo.InvariantCulture),
            source.ProjectConfigurationRevision.ToString(CultureInfo.InvariantCulture),
            source.PlatformRuntimeRevision.ToString(CultureInfo.InvariantCulture),
            source.ContextRevision,
            source.ExecutionFence.ToString(CultureInfo.InvariantCulture),
            source.OwnerStateVersion.ToString(CultureInfo.InvariantCulture),
            source.DecisionStateVersion.ToString(CultureInfo.InvariantCulture),
            source.CaptureId,
            source.SourceProofSha256,
            source.SourceManifestSha256,
            source.SourcePackageSha256,
            source.SourceControlPinId,
            source.WorkspaceId,
            source.WorkspaceIncarnationId.ToString("N"),
            source.BranchName,
            source.RepositoryId,
            source.ResourceGeneration.ToString(CultureInfo.InvariantCulture),
            source.BaseSha,
            source.OutputTreeSha);
        return Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(material)));
    }

    /// <summary>Hashes a validated capture's source and output evidence for approval binding.</summary>
    /// <exception cref="ArgumentException">The capture proof is invalid.</exception>
    public static string ComputeSourceProofSha256(ProducedRunCaptureProof capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ProducedRunCaptureContractValidation.Validate(capture);
        var identity = capture.Identity;
        var material = string.Join('\0',
            capture.ContractVersion.ToString(CultureInfo.InvariantCulture),
            identity.ProjectId,
            identity.RunId,
            identity.SessionId,
            capture.CaptureId,
            capture.EventId.ToString("N"),
            capture.ActorIssuer,
            capture.ActorSubject,
            capture.TenantId,
            capture.SourceControlPinId,
            capture.AcceptedSelectionHash.ToLowerInvariant(),
            capture.WorkspaceId,
            capture.WorkspaceIncarnationId.ToString("N"),
            capture.BranchName,
            capture.RepositoryId,
            capture.ResourceGeneration.ToString(CultureInfo.InvariantCulture),
            capture.BaseSha.ToLowerInvariant(),
            capture.OutputTreeSha.ToLowerInvariant(),
            capture.ManifestSha256.ToLowerInvariant(),
            capture.ManifestByteLength.ToString(CultureInfo.InvariantCulture),
            capture.PatchSha256.ToLowerInvariant(),
            capture.PatchByteLength.ToString(CultureInfo.InvariantCulture),
            capture.PackageSha256.ToLowerInvariant(),
            capture.PackageByteLength.ToString(CultureInfo.InvariantCulture),
            capture.CapturedAt.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(material)));
    }

    /// <summary>Hashes current Environment owner and revision pins for a read-proof check.</summary>
    public static string ComputeOwnerReadProofSha256(
        GeneratedApplicationPublicationSourceBinding source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var owner = source.EnvironmentFence.Owner;
        var material = string.Join('\0',
            owner.TenantId,
            owner.ProjectId,
            owner.RunId,
            owner.EnvironmentId,
            source.EnvironmentFence.LifecycleGeneration.ToString(CultureInfo.InvariantCulture),
            source.SourceSessionId,
            source.ActorIssuer,
            source.ActorSubject,
            source.AcceptedSelectionHash,
            source.ProjectRevision.ToString(CultureInfo.InvariantCulture),
            source.ProjectConfigurationRevision.ToString(CultureInfo.InvariantCulture),
            source.PlatformRuntimeRevision.ToString(CultureInfo.InvariantCulture),
            source.ContextRevision,
            source.ExecutionFence.ToString(CultureInfo.InvariantCulture),
            source.OwnerStateVersion.ToString(CultureInfo.InvariantCulture),
            source.DecisionStateVersion.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(material)));
    }
}
