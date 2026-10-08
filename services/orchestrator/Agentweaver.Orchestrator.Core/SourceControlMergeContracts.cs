using Agentweaver.Abstractions;

namespace Agentweaver.Orchestrator.Core;

public sealed record SourceControlCredentialReference
{
    public SourceControlCredentialReference(SecretRef secret, string purpose)
    {
        Secret = secret ?? throw new ArgumentNullException(nameof(secret));
        if (purpose is not (SourceControlSecretPurposes.Api or
            SourceControlSecretPurposes.Checkout or
            SourceControlSecretPurposes.Webhook or
            SourceControlSecretPurposes.GitHubAppPrivateKey))
            throw new ArgumentException("Unsupported SourceControl secret purpose.", nameof(purpose));
        Purpose = purpose;
    }

    public SecretRef Secret { get; }
    public string Purpose { get; }
}

public sealed record SourceControlGitHubAppBinding
{
    public SourceControlGitHubAppBinding(
        string identityConnectionId,
        long identityConnectionRevision,
        long installationId,
        string permissionDigest,
        string identityRepositorySelectionHash)
    {
        if (string.IsNullOrWhiteSpace(identityConnectionId) ||
            identityConnectionId.Length > 128 ||
            identityConnectionId.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new ArgumentException("A stable Identity connection ID is required.", nameof(identityConnectionId));
        if (identityConnectionRevision < 1)
            throw new ArgumentOutOfRangeException(nameof(identityConnectionRevision));
        if (installationId <= 0)
            throw new ArgumentOutOfRangeException(nameof(installationId));
        if (permissionDigest is null ||
            permissionDigest.Length != 64 ||
            !permissionDigest.All(Uri.IsHexDigit))
            throw new ArgumentException("GitHub App permission digest must be a SHA-256 hex digest.",
                nameof(permissionDigest));
        if (identityRepositorySelectionHash is null ||
            identityRepositorySelectionHash.Length != 64 ||
            !identityRepositorySelectionHash.All(Uri.IsHexDigit))
            throw new ArgumentException("Identity repository-selection hash must be a SHA-256 hex digest.",
                nameof(identityRepositorySelectionHash));

        IdentityConnectionId = identityConnectionId;
        IdentityConnectionRevision = identityConnectionRevision;
        InstallationId = installationId;
        PermissionDigest = permissionDigest.ToLowerInvariant();
        IdentityRepositorySelectionHash = identityRepositorySelectionHash.ToLowerInvariant();
    }

    public string IdentityConnectionId { get; }
    public long IdentityConnectionRevision { get; }
    public long InstallationId { get; }
    public string PermissionDigest { get; }
    public string IdentityRepositorySelectionHash { get; }
}

public sealed record SourceControlAcceptedRunBinding
{
    public SourceControlAcceptedRunBinding(
        string issuer,
        string subject,
        string tenantId,
        string projectId,
        string runId,
        string rootSessionId,
        string acceptedSelectionHash,
        long projectRevision,
        long projectConfigurationRevision,
        long platformRuntimeRevision,
        string contextRevision,
        long fence)
    {
        Issuer = RequireOpaqueReference(issuer, nameof(issuer));
        Subject = RequireOpaqueReference(subject, nameof(subject));
        TenantId = RequireOpaqueReference(tenantId, nameof(tenantId));
        ProjectId = RequireOpaqueReference(projectId, nameof(projectId));
        RunId = RequireOpaqueReference(runId, nameof(runId));
        RootSessionId = RequireOpaqueReference(rootSessionId, nameof(rootSessionId));
        if (acceptedSelectionHash is null ||
            acceptedSelectionHash.Length != 64 ||
            !acceptedSelectionHash.All(Uri.IsHexDigit))
            throw new ArgumentException("Accepted selection hash must be a SHA-256 hex digest.",
                nameof(acceptedSelectionHash));
        AcceptedSelectionHash = acceptedSelectionHash;
        if (projectRevision < 1 || projectConfigurationRevision < 1 ||
            platformRuntimeRevision < 1 || fence < 1)
            throw new ArgumentOutOfRangeException(
                nameof(projectRevision), "Accepted revisions and fence must be positive.");
        ProjectRevision = projectRevision;
        ProjectConfigurationRevision = projectConfigurationRevision;
        PlatformRuntimeRevision = platformRuntimeRevision;
        ContextRevision = RequireOpaqueReference(contextRevision, nameof(contextRevision));
        Fence = fence;
    }

    public string Issuer { get; }
    public string Subject { get; }
    public string TenantId { get; }
    public string ProjectId { get; }
    public string RunId { get; }
    public string RootSessionId { get; }
    public string AcceptedSelectionHash { get; }
    public long ProjectRevision { get; }
    public long ProjectConfigurationRevision { get; }
    public long PlatformRuntimeRevision { get; }
    public string ContextRevision { get; }
    public long Fence { get; }

    private static string RequireOpaqueReference(string? value, string paramName)
    {
        if (!WorkflowValidationSupport.IsOpaqueReference(value))
            throw new ArgumentException("A valid opaque identity is required.", paramName);
        return value!;
    }
}

public sealed record SourceControlRepositoryPin
{
    public SourceControlRepositoryPin(
        string pinId,
        SourceControlAcceptedRunBinding acceptedRun,
        PinnedProviderBinding providerBinding,
        SourceControlRepositoryIdentity repository,
        SourceControlCredentialReference? apiCredential,
        SourceControlCredentialReference? checkoutCredential,
        SourceControlCredentialReference? webhookCredential,
        long providerRepositoryId,
        string defaultBranch,
        bool isPrivate,
        DateTimeOffset pinnedAt,
        SourceControlGitHubAppBinding? githubAppBinding = null)
    {
        if (!WorkflowValidationSupport.IsStableId(pinId))
            throw new ArgumentException("Pin ID must be a stable identifier.", nameof(pinId));
        AcceptedRun = acceptedRun ?? throw new ArgumentNullException(nameof(acceptedRun));
        ProviderBinding = providerBinding ?? throw new ArgumentNullException(nameof(providerBinding));
        Repository = repository ?? throw new ArgumentNullException(nameof(repository));
        ApiCredential = apiCredential;
        CheckoutCredential = checkoutCredential;
        WebhookCredential = webhookCredential;
        GitHubAppBinding = githubAppBinding;
        if (githubAppBinding is null &&
            (apiCredential is null || apiCredential.Purpose != SourceControlSecretPurposes.Api ||
            checkoutCredential is not null &&
            checkoutCredential.Purpose != SourceControlSecretPurposes.Checkout ||
            isPrivate && checkoutCredential is null) ||
            githubAppBinding is not null &&
            (apiCredential is not null ||
             checkoutCredential is not null ||
             githubAppBinding.IdentityConnectionRevision < 1) ||
            webhookCredential is not null &&
            webhookCredential.Purpose != SourceControlSecretPurposes.Webhook)
            throw new ArgumentException(
                "Repository pins require either legacy API/checkout secrets or an exact GitHub App binding.",
                nameof(apiCredential));
        if (providerBinding.RunId != acceptedRun.RunId ||
            providerBinding.Seam != ProviderSeam.SourceControl ||
            providerBinding.Resource.Seam != ProviderSeam.SourceControl ||
            providerBinding.Resource.ProviderId != providerBinding.ProviderId ||
            !providerBinding.NegotiatedCapabilities.Contains(SourceControlCapabilities.RepositoryRead))
            throw new ArgumentException(
                "SourceControl provider binding must be pinned to this run and negotiated for repository reads.",
                nameof(providerBinding));
        if (providerRepositoryId <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(providerRepositoryId), "Provider repository ID must be positive.");
        if (string.IsNullOrWhiteSpace(defaultBranch) ||
            defaultBranch.Length > 256 ||
            defaultBranch.Any(char.IsControl))
            throw new ArgumentException("A valid default branch is required.", nameof(defaultBranch));
        if (pinnedAt.Offset != TimeSpan.Zero)
            throw new ArgumentException("Pin time must be UTC.", nameof(pinnedAt));

        PinId = pinId;
        ProviderRepositoryId = providerRepositoryId;
        DefaultBranch = defaultBranch;
        IsPrivate = isPrivate;
        PinnedAt = pinnedAt;
    }

    public string PinId { get; }
    public SourceControlAcceptedRunBinding AcceptedRun { get; }
    public PinnedProviderBinding ProviderBinding { get; }
    public SourceControlRepositoryIdentity Repository { get; }
    public SourceControlCredentialReference? ApiCredential { get; }
    public SourceControlCredentialReference? CheckoutCredential { get; }
    public SourceControlCredentialReference? WebhookCredential { get; }
    public SourceControlGitHubAppBinding? GitHubAppBinding { get; }
    public long ProviderRepositoryId { get; }
    public string DefaultBranch { get; }
    public bool IsPrivate { get; }
    public DateTimeOffset PinnedAt { get; }
}

public sealed record SourceControlMergeIntent
{
    private SourceControlMergeIntent(
        string intentId,
        SourceControlAcceptedRunBinding acceptedRun,
        SourceControlRepositoryPin repositoryPin,
        string workflowId,
        string definitionRevision,
        string workPlanId,
        string workflowStepId,
        CoordinatorGateDecisionReceipt approvalReceipt,
        SourceControlPullRequest pullRequest,
        SourceControlMergeMethod method,
        DateTimeOffset createdAt)
    {
        IntentId = intentId;
        AcceptedRun = acceptedRun;
        RepositoryPin = repositoryPin;
        WorkflowId = workflowId;
        DefinitionRevision = definitionRevision;
        WorkPlanId = workPlanId;
        WorkflowStepId = workflowStepId;
        ApprovalReceipt = approvalReceipt;
        PullRequestNumber = pullRequest.Number;
        HeadBranch = pullRequest.HeadBranch;
        ExpectedHeadSha = pullRequest.HeadSha;
        BaseBranch = pullRequest.BaseBranch;
        ExpectedBaseSha = pullRequest.BaseSha;
        Method = method;
        CreatedAt = createdAt;
    }

    public string IntentId { get; }
    public SourceControlAcceptedRunBinding AcceptedRun { get; }
    public SourceControlRepositoryPin RepositoryPin { get; }
    public string WorkflowId { get; }
    public string DefinitionRevision { get; }
    public string WorkPlanId { get; }
    public string WorkflowStepId { get; }
    public CoordinatorGateDecisionReceipt ApprovalReceipt { get; }
    public SourceControlRepositoryIdentity Repository => RepositoryPin.Repository;
    public long PullRequestNumber { get; }
    public string HeadBranch { get; }
    public string ExpectedHeadSha { get; }
    public string BaseBranch { get; }
    public string ExpectedBaseSha { get; }
    public SourceControlMergeMethod Method { get; }
    public DateTimeOffset CreatedAt { get; }

    public static SourceControlMergeIntent Create(
        string intentId,
        SourceControlAcceptedRunBinding acceptedRun,
        SourceControlRepositoryPin repositoryPin,
        CoordinatorDecisionState currentDecisionState,
        CoordinatorAssemblyRequestSnapshot assemblyRequest,
        CoordinatorGateDecisionReceipt approvalReceipt,
        SourceControlPullRequest pullRequest,
        SourceControlMergeMethod method,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(acceptedRun);
        ArgumentNullException.ThrowIfNull(repositoryPin);
        ArgumentNullException.ThrowIfNull(currentDecisionState);
        ArgumentNullException.ThrowIfNull(assemblyRequest);
        ArgumentNullException.ThrowIfNull(approvalReceipt);
        ArgumentNullException.ThrowIfNull(pullRequest);
        if (!WorkflowValidationSupport.IsStableId(intentId))
            throw new ArgumentException("Intent ID must be a stable identifier.", nameof(intentId));
        if (repositoryPin.AcceptedRun != acceptedRun ||
            currentDecisionState.Fence != acceptedRun.Fence ||
            currentDecisionState.SelectedWorkflow is null ||
            currentDecisionState.ConfirmedWorkPlan is null ||
            currentDecisionState.PendingGate is not null)
            throw new InvalidOperationException(
                "Merge intent must use the current accepted run, confirmed workflow and work plan.");

        var request = assemblyRequest.Request;
        var workflow = currentDecisionState.SelectedWorkflow.Definition;
        var plan = currentDecisionState.ConfirmedWorkPlan.Plan;
        if (request.Gate != WorkflowPlatformGate.Merge ||
            assemblyRequest.Step.Mode != WorkflowStepMode.Platform ||
            assemblyRequest.Step.PlatformGate != WorkflowPlatformGate.Merge ||
            request.WorkflowId != workflow.Id ||
            request.DefinitionRevision != workflow.Revision ||
            request.WorkPlanId != plan.Id ||
            request.WorkflowStepId != assemblyRequest.Step.Id ||
            !workflow.Steps.Any(step =>
                step.Id == request.WorkflowStepId &&
                step.Mode == WorkflowStepMode.Platform &&
                step.PlatformGate == WorkflowPlatformGate.Merge))
            throw new InvalidOperationException(
                "Merge intent must target the exact accepted workflow's platform Merge step.");

        if (approvalReceipt.Kind != CoordinatorGateKind.Approval ||
            approvalReceipt.SubjectId != intentId ||
            approvalReceipt.ActorId != acceptedRun.Subject ||
            approvalReceipt.Fence != acceptedRun.Fence ||
            approvalReceipt.ChoiceId != CoordinatorGateChoices.Approve ||
            approvalReceipt.FreeformAnswer is not null ||
            !currentDecisionState.DecisionReceipts.Contains(approvalReceipt))
            throw new InvalidOperationException(
                "Merge intent must be linked to the accepted approval receipt for this intent and fence.");
        if (!repositoryPin.ProviderBinding.NegotiatedCapabilities.Contains(SourceControlCapabilities.Merge))
            throw new InvalidOperationException(
                "The pinned SourceControl provider did not negotiate merge capability.");
        if (pullRequest.Number <= 0 ||
            !string.Equals(pullRequest.State, "open", StringComparison.OrdinalIgnoreCase) ||
            pullRequest.Merged ||
            !IsValidBranch(pullRequest.HeadBranch) ||
            !IsValidBranch(pullRequest.BaseBranch) ||
            !IsGitObjectId(pullRequest.HeadSha) ||
            !IsGitObjectId(pullRequest.BaseSha) ||
            !Enum.IsDefined(method))
            throw new ArgumentException(
                "Merge intent requires an open pull request with exact branch and commit identities.",
                nameof(pullRequest));
        if (createdAt.Offset != TimeSpan.Zero)
            throw new ArgumentException("Intent creation time must be UTC.", nameof(createdAt));

        return new SourceControlMergeIntent(
            intentId,
            acceptedRun,
            repositoryPin,
            workflow.Id,
            workflow.Revision,
            plan.Id,
            request.WorkflowStepId,
            approvalReceipt,
            pullRequest,
            method,
            createdAt);
    }

    private static bool IsValidBranch(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 256 &&
        !value.Any(character =>
            char.IsControl(character) ||
            character is ' ' or '~' or '^' or ':' or '?' or '*' or '[' or '\\');

    private static bool IsGitObjectId(string? value) =>
        value is { Length: 40 or 64 } &&
        value.All(Uri.IsHexDigit);
}
