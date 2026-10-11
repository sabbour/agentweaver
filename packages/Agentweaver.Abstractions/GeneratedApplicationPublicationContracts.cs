using System.Globalization;

namespace Agentweaver.Abstractions;

public static class GeneratedApplicationPublicationContract
{
    public const int ContractVersion = 1;
    public const string RegistryCredentialPurpose = "generated-application.registry-publish";
}

public sealed record GeneratedApplicationPublicationSourceBinding
{
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

    public string OwnerReadProofSha256 =>
        GeneratedApplicationPublicationValidation.ComputeOwnerReadProofSha256(this);

    private static string NormalizeHash(string? value, string paramName)
    {
        if (value is not { Length: 64 } ||
            value.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("A SHA-256 hash is required.", paramName);
        return value.ToLowerInvariant();
    }

    private static string ValidateIdentifier(string? value, int maximumLength, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                character is not ('-' or '_' or '.' or ':' or '/')))
            throw new ArgumentException("A bounded identifier is required.", paramName);
        return value;
    }

    private static string ValidateText(string? value, int maximumLength, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength ||
            value.Any(char.IsControl))
            throw new ArgumentException("A bounded text value is required.", paramName);
        return value;
    }

    private static string ValidateGitObjectId(string? value, string paramName)
    {
        if (value is not { Length: 40 or 64 } ||
            value.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("A Git object ID is required.", paramName);
        return value.ToLowerInvariant();
    }
}

public sealed record GeneratedApplicationOciArtifact
{
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

    private static string ValidateIdentifier(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl))
            throw new ArgumentException("An opaque bounded artifact reference is required.", paramName);
        return value;
    }
}

public enum GeneratedApplicationBuildTestOutcome
{
    Succeeded,
    BuildFailed,
    TestsFailed
}

public sealed record GeneratedApplicationBuildTestEvidence
{
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

    private static string ValidateReference(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl))
            throw new ArgumentException("An immutable evidence reference is required.", paramName);
        return value;
    }
}

public sealed record GeneratedApplicationBuildTestRequest
{
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

public interface IGeneratedApplicationBuildTestExecutor
{
    Task<GeneratedApplicationBuildTestEvidence> ExecuteAsync(
        GeneratedApplicationBuildTestRequest request,
        CancellationToken cancellationToken);
}

public sealed record GeneratedApplicationPublicationRequest
{
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

    private static string ValidateIdentifier(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                character is not ('-' or '_' or '.' or ':' or '/')))
            throw new ArgumentException("A bounded identifier is required.", paramName);
        return value;
    }

    private static string ValidateText(string value, int maximumLength, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength ||
            value.Any(char.IsControl))
            throw new ArgumentException("A bounded text value is required.", paramName);
        return value;
    }
}

public sealed record GeneratedApplicationPublicationApproval
{
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

    public bool BindsTo(
        GeneratedApplicationPublicationRequest request,
        GeneratedApplicationBuildTestEvidence evidence) =>
        Source.Matches(request.Source) &&
        ApplicationId == request.ApplicationId &&
        RegistryTargetId == request.RegistryTargetId &&
        BuildTestEvidenceSha256 == evidence.EvidenceSha256;
}

public sealed record GeneratedApplicationRegistryPublishRequest
{
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

public sealed record GeneratedApplicationRegistryPublishResult
{
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

public enum GeneratedApplicationRegistryObservationState
{
    ExactDigestPresent,
    NotFound,
    ConflictingDigest,
    Unknown
}

public sealed record GeneratedApplicationRegistryObservation
{
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

public interface IGeneratedApplicationRegistryPublisher
{
    Task<GeneratedApplicationRegistryPublishResult> PublishAsync(
        GeneratedApplicationRegistryPublishRequest request,
        SecretCredential credential,
        CancellationToken cancellationToken);

    Task<GeneratedApplicationRegistryObservation> ObserveAsync(
        GeneratedApplicationRegistryPublishRequest request,
        SecretCredential credential,
        CancellationToken cancellationToken);
}

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

public sealed record GeneratedApplicationPublicationReservation(
    GeneratedApplicationPublicationOperation Operation,
    bool Replayed);

public sealed record GeneratedApplicationPublicationCompletion(
    GeneratedApplicationPublicationOperation Operation,
    bool Replayed);

public static class GeneratedApplicationPublicationValidation
{
    public static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    public static bool IsImageDigest(string? value) =>
        value is { Length: 71 } &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value[7..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

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
