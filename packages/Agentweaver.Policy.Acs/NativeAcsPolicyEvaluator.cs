using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net;
using AgentControlSpecification;
using Agentweaver.Abstractions;

namespace Agentweaver.Policy.Acs;

public sealed class NativeAcsPolicyEvaluator
{
    private readonly NativeAcsArtifactValidator _validator;
    private readonly string _workRoot;

    public NativeAcsPolicyEvaluator(NativeAcsArtifactValidator validator, string workRoot)
    {
        ArgumentNullException.ThrowIfNull(validator);
        if (string.IsNullOrWhiteSpace(workRoot) || !Path.IsPathFullyQualified(workRoot))
            throw new ArgumentException("A full caller-owned work root is required.", nameof(workRoot));

        _validator = validator;
        _workRoot = Path.GetFullPath(workRoot);
    }

    /// <summary>Validates inputs before native evaluation. A cleanup failure overrides even an allow or deny result with an error.</summary>
    public Task<NativeAcsEvaluationResult> EvaluateAsync(
        NativeAcsArtifact artifact,
        InterventionPoint interventionPoint,
        NativeAcsEvaluationSnapshot snapshot,
        CancellationToken cancellationToken = default) =>
        EvaluateCoreAsync(artifact, interventionPoint, snapshot, null, cancellationToken);

    public Task<NativeAcsEvaluationResult> EvaluateComposedAsync(
        NativeAcsArtifact artifact,
        NativeAcsDomainId domain,
        InterventionPoint interventionPoint,
        NativeAcsEvaluationSnapshot snapshot,
        CancellationToken cancellationToken = default) =>
        EvaluateCoreAsync(artifact, interventionPoint, snapshot, domain, cancellationToken);

    public Task<NativeAcsEvaluationResult> EvaluateComposedAsync(
        NativeAcsArtifact artifact,
        NativeAcsDomainId domain,
        InterventionPoint interventionPoint,
        NativeAcsEvaluationSnapshot snapshot,
        bool projectScope,
        CancellationToken cancellationToken) =>
        EvaluateCoreAsync(artifact, interventionPoint, snapshot, domain, cancellationToken, projectScope: projectScope);

    /// <summary>Evaluates a composed policy and retains the exact native input and approval handle for restart-safe resolution.</summary>
    public Task<NativeAcsEvaluationResult> EvaluateComposedForApprovalAsync(
        NativeAcsArtifact artifact,
        NativeAcsDomainId domain,
        InterventionPoint interventionPoint,
        NativeAcsEvaluationSnapshot snapshot,
        CancellationToken cancellationToken = default) =>
        EvaluateCoreAsync(artifact, interventionPoint, snapshot, domain, cancellationToken, retainApproval: true);

    /// <summary>Evaluates a composed policy and retains the exact native input and approval handle for restart-safe resolution.</summary>
    public Task<NativeAcsEvaluationResult> EvaluateComposedForApprovalAsync(
        NativeAcsArtifact artifact,
        NativeAcsDomainId domain,
        InterventionPoint interventionPoint,
        NativeAcsEvaluationSnapshot snapshot,
        bool projectScope,
        CancellationToken cancellationToken) =>
        EvaluateCoreAsync(artifact, interventionPoint, snapshot, domain, cancellationToken, retainApproval: true, projectScope: projectScope);

    /// <summary>Re-evaluates and resolves the retained native escalation only when its input, handle, and action identity still match.</summary>
    public Task<NativeAcsEvaluationResult> ResolveComposedApprovalAsync(
        NativeAcsArtifact artifact,
        NativeAcsDomainId domain,
        InterventionPoint interventionPoint,
        NativeAcsEvaluationSnapshot snapshot,
        JsonElement retainedPolicyInput,
        JsonElement? retainedApproval,
        bool approve,
        CancellationToken cancellationToken = default) =>
        EvaluateCoreAsync(
            artifact,
            interventionPoint,
            snapshot,
            domain,
            cancellationToken,
            approvalBinding: new(retainedPolicyInput, retainedApproval, approve));

    /// <summary>Re-evaluates and resolves the retained native escalation only when its input, handle, and action identity still match.</summary>
    public Task<NativeAcsEvaluationResult> ResolveComposedApprovalAsync(
        NativeAcsArtifact artifact,
        NativeAcsDomainId domain,
        InterventionPoint interventionPoint,
        NativeAcsEvaluationSnapshot snapshot,
        JsonElement retainedPolicyInput,
        JsonElement? retainedApproval,
        bool approve,
        bool projectScope,
        CancellationToken cancellationToken) =>
        EvaluateCoreAsync(
            artifact,
            interventionPoint,
            snapshot,
            domain,
            cancellationToken,
            approvalBinding: new(retainedPolicyInput, retainedApproval, approve),
            projectScope: projectScope);

    private async Task<NativeAcsEvaluationResult> EvaluateCoreAsync(
        NativeAcsArtifact artifact,
        InterventionPoint interventionPoint,
        NativeAcsEvaluationSnapshot snapshot,
        NativeAcsDomainId? domain,
        CancellationToken cancellationToken,
        bool retainApproval = false,
        NativeAcsRetainedApprovalBinding? approvalBinding = null,
        bool projectScope = false)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(snapshot);

        if (cancellationToken.IsCancellationRequested)
            return Error("unavailable", "evaluation_cancelled");
        if (domain is { } selectedDomain &&
            (!Enum.IsDefined(selectedDomain) ||
             interventionPoint != InterventionPoint.PreModelCall ||
             projectScope && selectedDomain == NativeAcsDomainId.PlatformNetwork ||
             !NativeAcsUnifiedPolicyProfile.IsCandidate(artifact) &&
                 selectedDomain is NativeAcsDomainId.ModelUse or NativeAcsDomainId.Guardrails
                     or NativeAcsDomainId.ToolPermissions or NativeAcsDomainId.Skills))
            return Error("unavailable", "domain_intervention_point_unsupported");

        var validation = domain is null
            ? _validator.Validate(artifact)
            : projectScope
                ? _validator.ValidateComposedProject(artifact)
                : _validator.ValidateComposed(artifact);
        if (!validation.IsValid)
        {
            var failureCode = validation.DependencyFailure switch
            {
                NativeAcsValidationDependencyFailure.OpaUnavailable => "opa_unavailable",
                NativeAcsValidationDependencyFailure.NativeValidatorUnavailable => "native_validator_unavailable",
                _ => validation.Diagnostics.FirstOrDefault()?.Code ?? "artifact_invalid",
            };
            return Error("unavailable", failureCode);
        }

        var artifactHash = artifact.ContentSha256;

        if (!IsValidSnapshot(snapshot))
            return Error(artifactHash, "snapshot_invalid");

        if ((domain == NativeAcsDomainId.PlatformNetwork ||
             domain is null && NativeAcsFiniteNetworkProfile.IsCandidate(artifact)) &&
            snapshot.Network is { } network &&
            !IsValidNetworkFacts(network))
            return Deny(artifactHash);
        if (domain == NativeAcsDomainId.ModelUse &&
            snapshot.ModelUse is { } modelUse &&
            !IsValidModelUseFacts(modelUse))
            return Error(artifactHash, "snapshot_invalid");
        if (domain == NativeAcsDomainId.Guardrails &&
            snapshot.Guardrails is { } guardrails &&
            !IsValidGuardrailsFacts(guardrails))
            return Error(artifactHash, "snapshot_invalid");
        if (domain == NativeAcsDomainId.ToolPermissions &&
            (snapshot.Tool is not { } tool || !IsValidToolFacts(tool)))
            return Error(artifactHash, "snapshot_invalid");
        if (snapshot.Approval is { } approval &&
            !IsValidApprovalFacts(approval))
            return Error(artifactHash, "snapshot_invalid");
        if (domain == NativeAcsDomainId.Skills &&
            snapshot.Skills is not { TrustScore: >= 0 and <= 1000 })
            return Error(artifactHash, "snapshot_invalid");

        if (!Enum.IsDefined(interventionPoint))
            return Error(artifactHash, "intervention_point_invalid");

        var configuredOpaPath = Environment.GetEnvironmentVariable("ACS_OPA_PATH");
        if (!IsSamePath(configuredOpaPath, _validator.OpaPath))
            return Error(artifactHash, "opa_path_mismatch");

        var workDirectory = Path.Combine(_workRoot, Guid.NewGuid().ToString("N"));
        var cleanupFailed = false;
        var evaluation = Error(artifactHash, "native_evaluation_failed");
        try
        {
            Directory.CreateDirectory(workDirectory);
            WriteArtifact(workDirectory, artifact);

            var manifestPath = Path.Combine(workDirectory, "manifest.yaml");
            var control = AgentControl.FromPath(manifestPath);
            using var snapshotDocument = JsonDocument.Parse(
                JsonSerializer.SerializeToUtf8Bytes(CreateSnapshot(snapshot, domain, artifact)));
            var result = await control.EvaluateInterventionPointAsync(
                interventionPoint,
                snapshotDocument.RootElement,
                EnforcementMode.Enforce,
                cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            if (approvalBinding is { } binding)
            {
                var matches = result.PolicyInput is { } policyInput &&
                    !string.IsNullOrWhiteSpace(result.ActionIdentity) &&
                    JsonElement.DeepEquals(policyInput, binding.PolicyInput) &&
                    NullableJsonEquals(result.Verdict.Approval, binding.Approval) &&
                    result.ActionIdentity == AgentControl.ActionIdentity(binding.PolicyInput);
                evaluation = !matches
                    ? Error(artifactHash, "approval_binding_changed")
                    : binding.Approve
                        ? new(
                            PolicyEvaluationOutcome.Allow,
                            PolicyEvaluationReasonCode.Allowed,
                            artifactHash)
                        : new(
                            PolicyEvaluationOutcome.Deny,
                            PolicyEvaluationReasonCode.DefaultDeny,
                            artifactHash);
            }
            else if (result.Verdict.Decision == Decision.Allow)
                evaluation = new NativeAcsEvaluationResult(
                    PolicyEvaluationOutcome.Allow,
                    PolicyEvaluationReasonCode.Allowed,
                    artifactHash);
            else if ((result.Verdict.Decision == Decision.Escalate ||
                      result.Verdict.Decision == Decision.Deny &&
                          result.Verdict.Approval is not null) &&
                     retainApproval)
                evaluation = new NativeAcsEvaluationResult(
                    PolicyEvaluationOutcome.Error,
                    PolicyEvaluationReasonCode.DefaultDeny,
                    artifactHash,
                    DiagnosticCode: "approval_required",
                    result.PolicyInput,
                    result.Verdict.Approval);
            else if (result.Verdict.Decision == Decision.Deny)
                evaluation = cancellationToken.IsCancellationRequested
                    ? Error(artifactHash, "evaluation_cancelled")
                    : result.Verdict.Reason?.StartsWith("runtime_error:", StringComparison.Ordinal) == true
                        ? Error(artifactHash, "native_policy_runtime_error")
                        : new NativeAcsEvaluationResult(
                            PolicyEvaluationOutcome.Deny,
                            PolicyEvaluationReasonCode.DefaultDeny,
                            artifactHash);
            else
                evaluation = Error(artifactHash, "unsupported_verdict");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            evaluation = Error(
                artifactHash,
                exception is OperationCanceledException
                    ? cancellationToken.IsCancellationRequested ? "evaluation_cancelled" : "evaluation_timeout"
                    : "native_evaluation_failed");
        }
        finally
        {
            try
            {
                if (Directory.Exists(workDirectory))
                    Directory.Delete(workDirectory, recursive: true);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                cleanupFailed = true;
            }
        }

        return cleanupFailed ? Error(artifactHash, "artifact_cleanup_failed") : evaluation;
    }

    /// <summary>Returns true when all required identifiers are valid and any human identity has valid IDs and the same tenant; otherwise false.</summary>
    private static bool IsValidSnapshot(NativeAcsEvaluationSnapshot snapshot)
    {
        if (!IsIdentifier(snapshot.Identity.ProjectId) ||
            !IsIdentifier(snapshot.Identity.RunId) ||
            !IsIdentifier(snapshot.Identity.SessionId) ||
            !IsIdentifier(snapshot.TenantId) ||
            !IsIdentifier(snapshot.AgentId) ||
            !IsIdentifier(snapshot.ActionId))
            return false;

        return snapshot.VerifiedHumanIdentity is not { } human ||
            (IsIdentifier(human.TenantId) &&
             IsIdentifier(human.ObjectId) &&
             string.Equals(human.TenantId, snapshot.TenantId, StringComparison.Ordinal));
    }

    private static bool IsValidModelUseFacts(NativeAcsModelUseEvaluationFacts facts) =>
        Enum.IsDefined(facts.SourceMode) &&
        IsIdentifier(facts.ModelSelectionReference) &&
        IsIdentifier(facts.ModelId) &&
        IsIdentifier(facts.ConfigurationRevision) &&
        facts.ConfigurationHash is { Length: 64 } &&
        facts.ConfigurationHash.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f') &&
        (facts.SourceMode == ModelSourceMode.HostedCopilot
            ? facts.ProviderType is null && facts.ProviderId is null
            : facts.ProviderType is "azure" or "openai" or "anthropic" &&
              (facts.ProviderId is null || IsIdentifier(facts.ProviderId)));

    private static bool IsValidGuardrailsFacts(NativeAcsGuardrailsEvaluationFacts facts) =>
        facts.Stage is "userInput" &&
        facts.ContentClass is "text" &&
        !facts.Checks.IsDefault &&
        facts.Checks.Length <= GuardrailsMaximumChecks &&
        facts.Checks.All(check =>
            IsIdentifier(check.ProviderId) &&
            Version.TryParse(check.AdapterVersion, out var version) &&
            version.ToString() == check.AdapterVersion &&
            check.OptionsSchemaVersion >= 1 &&
            IsIdentifier(check.OptionsRevision)) &&
        facts.Checks.Select(check => check.ProviderId).Distinct(StringComparer.Ordinal).Count() ==
            facts.Checks.Length &&
        !facts.Findings.IsDefault &&
        facts.Findings.Length <= GuardrailsMaximumFindings &&
        facts.Findings.All(finding =>
            IsIdentifier(finding.CheckId) &&
            IsIdentifier(finding.Code) &&
            finding.Severity is "low" or "medium" or "high" or "critical");

    private static bool IsValidToolFacts(NativeAcsToolEvaluationFacts facts) =>
        IsIdentifier(facts.ToolName) &&
        IsIdentifier(facts.CallId) &&
        IsIdentifier(facts.SdkSessionId) &&
        IsIdentifier(facts.MessageId) &&
        IsIdentifier(facts.TurnId) &&
        facts.Phase is "invocation" or "permission" &&
        IsSha256(facts.ArgumentDigest) &&
        (facts.PermissionKind is null or "read" or "write" or "shell") &&
        (facts.InvocationArgumentDigest is null || IsSha256(facts.InvocationArgumentDigest)) &&
        (facts.ReadAccess ? 1 : 0) + (facts.WriteAccess ? 1 : 0) +
            (facts.ShellAccess ? 1 : 0) == 1 &&
        (facts.Phase == "invocation"
            ? facts.PermissionKind is null && facts.InvocationArgumentDigest is null
            : facts.PermissionKind is not null && facts.InvocationArgumentDigest is not null) &&
        (facts.RemoteMcp is null || IsValidRemoteMcpFacts(facts.RemoteMcp));

    private static bool IsValidRemoteMcpFacts(NativeAcsRemoteMcpEvaluationFacts facts) =>
        IsIdentifier(facts.AgentId) &&
        IsIdentifier(facts.NodeId) &&
        IsIdentifier(facts.SnapshotId) &&
        IsIdentifier(facts.ConnectionId) &&
        IsIdentifier(facts.TransportProfile) &&
        facts.ConnectionConfigurationRevision > 0 &&
        facts.ConnectionDiscoveryRevision > 0 &&
        facts.GatewayEnforcementGeneration > 0 &&
        IsSha256(facts.SnapshotDigest) &&
        IsSha256(facts.ConfigurationSha256) &&
        IsSha256(facts.CatalogDigest) &&
        IsIdentifier(facts.ToolRevision) &&
        IsSha256(facts.ToolDigest) &&
        IsSha256(facts.TransportOptionsSha256) &&
        IsSha256(facts.GatewayDescriptorSha256) &&
        facts.ScanEvidence is not null;

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } &&
        value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsValidApprovalFacts(NativeAcsApprovalEvaluationFacts facts) =>
        IsIdentifier(facts.TurnId) &&
        facts.ArgumentsDigest is { Length: 64 } &&
        facts.ArgumentsDigest.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private const int GuardrailsMaximumChecks = 16;
    private const int GuardrailsMaximumFindings = 48;

    /// <summary>Returns true for a non-blank identifier of at most 256 ASCII letters, digits, dot, underscore, hyphen, or colon; otherwise false.</summary>
    private static bool IsIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 256 &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':');

    /// <summary>Returns true only for packet or known-destination checks over TCP on the fixed HTTPS port and a canonical DNS name or IP address; otherwise false. Null input throws.</summary>
    private static bool IsValidNetworkFacts(NativeAcsNetworkEvaluationFacts network)
    {
        if (network.CheckKind is not ("packet" or "known_destination_use") ||
            network.Protocol != "TCP" ||
            network.Port != PlatformEgressHostValidator.HttpsPort ||
            network.Destination is not { } destination)
            return false;

        return destination.Kind switch
        {
            "dns" => IsCanonicalDnsName(destination.Value),
            "ip" => IPAddress.TryParse(destination.Value, out var address) &&
                string.Equals(address.ToString(), destination.Value, StringComparison.Ordinal),
            _ => false
        };
    }

    /// <summary>Returns true for a lower-case multi-label DNS name with valid 1-to-63 character labels, no final dot, and a non-numeric final label; otherwise false.</summary>
    private static bool IsCanonicalDnsName(string? value)
    {
        if (string.IsNullOrEmpty(value) ||
            value.Length > 253 ||
            value.EndsWith('.') ||
            IPAddress.TryParse(value, out _) ||
            !value.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character is '.' or '-'))
            return false;

        var labels = value.Split('.');
        return labels.Length >= 2 &&
            !labels[^1].All(char.IsAsciiDigit) &&
            labels.All(label =>
                label.Length is >= 1 and <= 63 &&
                label[0] != '-' &&
                label[^1] != '-' &&
                label.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-'));
    }

    private static NativeAcsSnapshotDocument CreateSnapshot(
        NativeAcsEvaluationSnapshot snapshot,
        NativeAcsDomainId? domain,
        NativeAcsArtifact artifact)
    {
        var verifiedHuman = snapshot.VerifiedHumanIdentity;
        return new NativeAcsSnapshotDocument(
            new NativeAcsModelRequest(
                snapshot.TenantId,
                snapshot.Identity.ProjectId,
                snapshot.Identity.RunId,
                snapshot.Identity.SessionId,
                snapshot.AgentId,
                snapshot.ActionId,
                verifiedHuman is not null,
                verifiedHuman?.ObjectId,
                verifiedHuman?.TenantId,
                snapshot.Network is { } network
                    ? new NativeAcsNetworkFacts(
                        network.CheckKind,
                        network.Protocol,
                        network.Port,
                        network.Destination is { } destination
                            ? new NativeAcsNetworkDestinationFacts(destination.Kind, destination.Value)
                            : null)
                    : null,
                snapshot.ModelUse is { } modelUse
                    ? new NativeAcsModelUseFacts(
                        modelUse.ModelSelectionReference,
                        modelUse.ModelId,
                        modelUse.SourceMode == ModelSourceMode.HostedCopilot ? "hostedCopilot" : "byok",
                        modelUse.ProviderType,
                        modelUse.ProviderId,
                        modelUse.ConfigurationRevision,
                        modelUse.ConfigurationHash)
                    : null,
                snapshot.Guardrails is { } guardrails
                    ? new NativeAcsGuardrailsFacts(
                        guardrails.Stage,
                        guardrails.ContentClass,
                        guardrails.Checks.Select(check => new NativeAcsGuardrailsCheck(
                            check.ProviderId,
                            check.AdapterVersion,
                            check.OptionsSchemaVersion,
                            check.OptionsRevision)).ToArray(),
                        guardrails.Findings.Select(finding => new NativeAcsGuardrailsFinding(
                            finding.CheckId, finding.Code, finding.Severity)).ToArray())
                    : null,
                snapshot.Tool is { } tool
                    ? new NativeAcsToolFacts(
                        tool.ToolName, tool.CallId, tool.Phase, tool.ArgumentDigest,
                        tool.SdkSessionId, tool.MessageId, tool.TurnId,
                        tool.ReadAccess, tool.WriteAccess, tool.ShellAccess,
                        tool.PermissionKind, tool.InvocationArgumentDigest,
                        tool.RemoteMcp is { } remote
                            ? new NativeAcsRemoteMcpFacts(
                                remote.AgentId, remote.NodeId, remote.SnapshotId, remote.SnapshotDigest,
                                remote.ConnectionId, remote.ConnectionConfigurationRevision,
                                remote.ConnectionDiscoveryRevision, remote.ConfigurationSha256,
                                remote.CatalogDigest, remote.ToolRevision, remote.ToolDigest,
                                remote.TransportProfile, remote.TransportOptionsSha256,
                                remote.GatewayDescriptorSha256, remote.GatewayEnforcementGeneration,
                                remote.ScanEvidence)
                            : null)
                    : null,
                snapshot.Approval is { } approval
                    ? new NativeAcsApprovalFacts(approval.TurnId, approval.ArgumentsDigest)
                    : null,
                snapshot.Skills is { } skills
                    ? new NativeAcsSkillsFacts(
                        skills.TrustScore,
                        skills.EvidenceComplete,
                        skills.BuiltInExempt)
                    : null,
                domain is { } selectedDomain
                    ? NativeAcsUnifiedPolicyProfile.IsCandidate(artifact)
                        ? NativeAcsUnifiedPolicyProfile.GetPolicyId(selectedDomain)
                        : NativeAcsComposableDomainsProfile.GetPolicyId(selectedDomain)
                    : null));
    }

    private static void WriteArtifact(string workDirectory, NativeAcsArtifact artifact)
    {
        File.WriteAllBytes(Path.Combine(workDirectory, "manifest.yaml"), artifact.ManifestBytes.ToArray());
        foreach (var file in artifact.Files)
        {
            var destination = Path.Combine(workDirectory, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            output.Write(file.Bytes.AsSpan());
        }
    }

    /// <summary>Returns true when <paramref name="left"/> is fully qualified and <c>Path.GetFullPath(left)</c> equals <paramref name="right"/>, ignoring case only on Windows; otherwise false. Invalid paths can throw.</summary>
    private static bool IsSamePath(string? left, string right) =>
        !string.IsNullOrWhiteSpace(left) &&
        Path.IsPathFullyQualified(left) &&
        string.Equals(
            Path.GetFullPath(left),
            right,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static NativeAcsEvaluationResult Error(string artifactHash, string code) =>
        new(
            PolicyEvaluationOutcome.Error,
            PolicyEvaluationReasonCode.EvaluationFailed,
            artifactHash,
            code);

    private static NativeAcsEvaluationResult Deny(string artifactHash) =>
        new(
            PolicyEvaluationOutcome.Deny,
            PolicyEvaluationReasonCode.DefaultDeny,
            artifactHash);

    private static bool NullableJsonEquals(JsonElement? left, JsonElement? right) =>
        left.HasValue == right.HasValue &&
        (!left.HasValue || JsonElement.DeepEquals(
            left.GetValueOrDefault(), right.GetValueOrDefault()));

    private sealed class NativeAcsRetainedApprovalBinding(
        JsonElement policyInput,
        JsonElement? approval,
        bool approve)
    {
        public JsonElement PolicyInput { get; } = policyInput.Clone();
        public JsonElement? Approval { get; } = approval?.Clone();
        public bool Approve { get; } = approve;
    }

    private sealed record NativeAcsSnapshotDocument(
        [property: JsonPropertyName("model_request")] NativeAcsModelRequest ModelRequest);

    private sealed record NativeAcsModelRequest(
        [property: JsonPropertyName("tenant_id")] string TenantId,
        [property: JsonPropertyName("project_id")] string ProjectId,
        [property: JsonPropertyName("run_id")] string RunId,
        [property: JsonPropertyName("session_id")] string SessionId,
        [property: JsonPropertyName("agent_id")] string AgentId,
        [property: JsonPropertyName("action_id")] string ActionId,
        [property: JsonPropertyName("human_available")] bool HumanAvailable,
        [property: JsonPropertyName("human_oid"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? HumanObjectId,
        [property: JsonPropertyName("human_tenant_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? HumanTenantId,
        [property: JsonPropertyName("network"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        NativeAcsNetworkFacts? Network,
        [property: JsonPropertyName("model_use"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        NativeAcsModelUseFacts? ModelUse,
        [property: JsonPropertyName("guardrails"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        NativeAcsGuardrailsFacts? Guardrails,
        [property: JsonPropertyName("tool"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        NativeAcsToolFacts? Tool,
        [property: JsonPropertyName("approval"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        NativeAcsApprovalFacts? Approval,
        [property: JsonPropertyName("skills"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        NativeAcsSkillsFacts? Skills,
        [property: JsonPropertyName("domain_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? DomainId);

    private sealed record NativeAcsToolFacts(
        [property: JsonPropertyName("tool_name")] string ToolName,
        [property: JsonPropertyName("call_id")] string CallId,
        [property: JsonPropertyName("phase")] string Phase,
        [property: JsonPropertyName("argument_digest")] string ArgumentDigest,
        [property: JsonPropertyName("sdk_session_id")] string SdkSessionId,
        [property: JsonPropertyName("message_id")] string MessageId,
        [property: JsonPropertyName("turn_id")] string TurnId,
        [property: JsonPropertyName("read_access")] bool ReadAccess,
        [property: JsonPropertyName("write_access")] bool WriteAccess,
        [property: JsonPropertyName("shell_access")] bool ShellAccess,
        [property: JsonPropertyName("permission_kind")] string? PermissionKind,
        [property: JsonPropertyName("invocation_argument_digest")] string? InvocationArgumentDigest,
        [property: JsonPropertyName("remote_mcp"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        NativeAcsRemoteMcpFacts? RemoteMcp);

    private sealed record NativeAcsRemoteMcpFacts(
        [property: JsonPropertyName("agent_id")] string AgentId,
        [property: JsonPropertyName("node_id")] string NodeId,
        [property: JsonPropertyName("snapshot_id")] string SnapshotId,
        [property: JsonPropertyName("snapshot_digest")] string SnapshotDigest,
        [property: JsonPropertyName("connection_id")] string ConnectionId,
        [property: JsonPropertyName("connection_configuration_revision")] long ConnectionConfigurationRevision,
        [property: JsonPropertyName("connection_discovery_revision")] long ConnectionDiscoveryRevision,
        [property: JsonPropertyName("configuration_sha256")] string ConfigurationSha256,
        [property: JsonPropertyName("catalog_digest")] string CatalogDigest,
        [property: JsonPropertyName("tool_revision")] string ToolRevision,
        [property: JsonPropertyName("tool_digest")] string ToolDigest,
        [property: JsonPropertyName("transport_profile")] string TransportProfile,
        [property: JsonPropertyName("transport_options_sha256")] string TransportOptionsSha256,
        [property: JsonPropertyName("gateway_descriptor_sha256")] string GatewayDescriptorSha256,
        [property: JsonPropertyName("gateway_enforcement_generation")] long GatewayEnforcementGeneration,
        [property: JsonPropertyName("scan_evidence")] RemoteMcpCatalogScanEvidence ScanEvidence);

    private sealed record NativeAcsNetworkFacts(
        [property: JsonPropertyName("check_kind")] string CheckKind,
        [property: JsonPropertyName("protocol")] string Protocol,
        [property: JsonPropertyName("port")] int Port,
        [property: JsonPropertyName("destination")] NativeAcsNetworkDestinationFacts? Destination);

    private sealed record NativeAcsNetworkDestinationFacts(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("value")] string Value);

    private sealed record NativeAcsModelUseFacts(
        [property: JsonPropertyName("model_selection_reference")] string ModelSelectionReference,
        [property: JsonPropertyName("model_id")] string ModelId,
        [property: JsonPropertyName("source_mode")] string SourceMode,
        [property: JsonPropertyName("provider_type")] string? ProviderType,
        [property: JsonPropertyName("provider_id")] string? ProviderId,
        [property: JsonPropertyName("configuration_revision")] string ConfigurationRevision,
        [property: JsonPropertyName("configuration_hash")] string ConfigurationHash);

    private sealed record NativeAcsGuardrailsFacts(
        [property: JsonPropertyName("stage")] string Stage,
        [property: JsonPropertyName("content_class")] string ContentClass,
        [property: JsonPropertyName("checks")] NativeAcsGuardrailsCheck[] Checks,
        [property: JsonPropertyName("findings")] NativeAcsGuardrailsFinding[] Findings);

    private sealed record NativeAcsApprovalFacts(
        [property: JsonPropertyName("turn_id")] string TurnId,
        [property: JsonPropertyName("arguments_digest")] string ArgumentsDigest);

    private sealed record NativeAcsGuardrailsCheck(
        [property: JsonPropertyName("provider_id")] string ProviderId,
        [property: JsonPropertyName("adapter_version")] string AdapterVersion,
        [property: JsonPropertyName("options_schema_version")] int OptionsSchemaVersion,
        [property: JsonPropertyName("options_revision")] string OptionsRevision);

    private sealed record NativeAcsGuardrailsFinding(
        [property: JsonPropertyName("check_id")] string CheckId,
        [property: JsonPropertyName("code")] string Code,
        [property: JsonPropertyName("severity")] string Severity);

    private sealed record NativeAcsSkillsFacts(
        [property: JsonPropertyName("trust_score")] int TrustScore,
        [property: JsonPropertyName("evidence_complete")] bool EvidenceComplete,
        [property: JsonPropertyName("built_in_exempt")] bool BuiltInExempt);
}
