using System.Collections.Immutable;
using System.Net.Http;
using System.Security.Cryptography;
using Agentweaver.Abstractions;

namespace Agentweaver.Orchestrator.Core;

public enum GuardrailsContentStage
{
    UserInput,
    ModelInput,
    ToolResult,
    ModelOutput,
    McpDiscovery
}

public enum GuardrailsContentClass
{
    Text,
    StructuredJson,
    ToolSchema,
    Image,
    Audio
}

public enum GuardrailsFindingSeverity
{
    Low,
    Medium,
    High,
    Critical
}

public enum GuardrailsClassifierOutcome
{
    Clear,
    Findings,
    Error
}

public enum GuardrailsClassifierFailure
{
    Unavailable,
    InvalidResponse,
    ProviderFailure
}

public enum GuardrailsCheckOutcome
{
    Clear,
    Findings,
    Failed
}

public enum GuardrailsDecision
{
    Allow,
    Withhold
}

public enum GuardrailsReasonCode
{
    Allowed,
    PolicyDenied,
    PolicyEvaluationFailed,
    MissingDependency,
    InvalidRequest,
    InvalidConfiguration,
    UnsupportedStageOrContent,
    ProviderUnavailable,
    ClassifierTimedOut,
    ClassifierFailed,
    AuthorityUnavailable,
    AuthorityRevoked,
    AuthorityStale,
    JournalUnavailable,
    JournalDuplicate
}

public enum GuardrailsAuthorityStatus
{
    Current,
    Revoked,
    Stale,
    Unavailable
}

public enum GuardrailsJournalAppendStatus
{
    Appended,
    Duplicate,
    Unavailable
}

public static class GuardrailsProviderCapabilities
{
    public const string PromptInjectionDetection = "guardrails.prompt-injection.detect";
    public const string UserInputText = "guardrails.stage.user-input.content.text";
    public const string ModelInputText = "guardrails.stage.model-input.content.text";
    public const string ToolResultText = "guardrails.stage.tool-result.content.text";
    public const string ModelOutputText = "guardrails.stage.model-output.content.text";
    public const string McpDiscoveryToolSchema = "guardrails.stage.mcp-discovery.content.tool-schema";
    public const string ToolResultStructuredJson = "guardrails.stage.tool-result.content.structured-json";
    public const string ToolResultImage = "guardrails.stage.tool-result.content.image";
    public const string ToolResultAudio = "guardrails.stage.tool-result.content.audio";

    public static ImmutableHashSet<string> PromptShieldsSupported { get; } =
        ImmutableHashSet.Create(StringComparer.Ordinal, PromptInjectionDetection, UserInputText, ToolResultText);

    public static string? For(GuardrailsContentStage stage, GuardrailsContentClass contentClass) =>
        (stage, contentClass) switch
        {
            (GuardrailsContentStage.UserInput, GuardrailsContentClass.Text) => UserInputText,
            (GuardrailsContentStage.ModelInput, GuardrailsContentClass.Text) => ModelInputText,
            (GuardrailsContentStage.ToolResult, GuardrailsContentClass.Text) => ToolResultText,
            (GuardrailsContentStage.ModelOutput, GuardrailsContentClass.Text) => ModelOutputText,
            (GuardrailsContentStage.McpDiscovery, GuardrailsContentClass.ToolSchema) => McpDiscoveryToolSchema,
            (GuardrailsContentStage.ToolResult, GuardrailsContentClass.StructuredJson) => ToolResultStructuredJson,
            (GuardrailsContentStage.ToolResult, GuardrailsContentClass.Image) => ToolResultImage,
            (GuardrailsContentStage.ToolResult, GuardrailsContentClass.Audio) => ToolResultAudio,
            _ => null
        };
}

public static class GuardrailsPolicyNarrowing
{
    public static AgtPolicyEvaluationResult Apply(
        AgtPolicyEvaluationResult baseline,
        AgtPolicyEvaluationResult withFindings)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(withFindings);
        if (baseline.ProviderId != withFindings.ProviderId ||
            baseline.AdapterVersion != withFindings.AdapterVersion ||
            baseline.OptionsSchemaVersion != withFindings.OptionsSchemaVersion ||
            baseline.OptionsRevision != withFindings.OptionsRevision)
            throw new ArgumentException("Guardrails policy evaluations must use the same pinned AGT binding.");
        if (!Enum.IsDefined(baseline.Outcome) || !Enum.IsDefined(withFindings.Outcome))
            throw new ArgumentException("Guardrails policy evaluation outcomes are invalid.");

        return baseline.Outcome == PolicyEvaluationOutcome.Allow
            ? withFindings
            : baseline;
    }
}

public abstract record GuardrailsProviderOptions(
    string ProviderId,
    Version AdapterVersion,
    int OptionsSchemaVersion,
    string OptionsRevision,
    TimeSpan RequestTimeout)
{
    public virtual void Validate()
    {
        if (!AgtPolicyProvider.IsIdentifier(ProviderId) ||
            AdapterVersion is null ||
            OptionsSchemaVersion < 1 ||
            !AgtPolicyProvider.IsIdentifier(OptionsRevision) ||
            RequestTimeout <= TimeSpan.Zero ||
            RequestTimeout > TimeSpan.FromMinutes(2))
            throw new ArgumentException("Guardrails provider options are invalid.");
    }
}

public sealed record GuardrailsFinding(string Code, GuardrailsFindingSeverity Severity);

public sealed record GuardrailsVersionedFinding(
    int CheckOrder,
    string ProviderId,
    string AdapterVersion,
    int OptionsSchemaVersion,
    string OptionsRevision,
    string Code,
    GuardrailsFindingSeverity Severity);

public sealed record GuardrailsClassifierRequest(
    GuardrailsContentStage Stage,
    GuardrailsContentClass ContentClass,
    string ContentDigest,
    ImmutableArray<byte> CanonicalContent);

public sealed record GuardrailsClassifierResult(
    GuardrailsClassifierOutcome Outcome,
    ImmutableArray<GuardrailsFinding> Findings,
    GuardrailsClassifierFailure? Failure = null)
{
    public static GuardrailsClassifierResult Clear() =>
        new(GuardrailsClassifierOutcome.Clear, []);

    public static GuardrailsClassifierResult Detected(ImmutableArray<GuardrailsFinding> findings) =>
        new(GuardrailsClassifierOutcome.Findings, findings);

    public static GuardrailsClassifierResult Failed(GuardrailsClassifierFailure failure) =>
        new(GuardrailsClassifierOutcome.Error, [], failure);
}

public interface IGuardrailsClassifier<TOptions> where TOptions : GuardrailsProviderOptions
{
    string ProviderId { get; }
    Version AdapterVersion { get; }
    int OptionsSchemaVersion { get; }
    ImmutableHashSet<string> Capabilities { get; }

    Task<GuardrailsClassifierResult> ClassifyAsync(
        TOptions options,
        GuardrailsClassifierRequest request,
        CancellationToken cancellationToken);
}

public interface IGuardrailsClassifierBinding
{
    PinnedProviderBinding Pin { get; }
    GuardrailsProviderOptions Options { get; }
    string ProviderId { get; }
    Version AdapterVersion { get; }
    int OptionsSchemaVersion { get; }
    ImmutableHashSet<string> Capabilities { get; }

    Task<GuardrailsClassifierResult> ClassifyAsync(
        GuardrailsClassifierRequest request,
        CancellationToken cancellationToken);
}

public sealed class GuardrailsClassifierBinding<TOptions> : IGuardrailsClassifierBinding
    where TOptions : GuardrailsProviderOptions
{
    private readonly IGuardrailsClassifier<TOptions> _classifier;

    public GuardrailsClassifierBinding(
        PinnedProviderBinding pin,
        TOptions options,
        IGuardrailsClassifier<TOptions> classifier)
    {
        Pin = pin ?? throw new ArgumentNullException(nameof(pin));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
    }

    public PinnedProviderBinding Pin { get; }
    public GuardrailsProviderOptions Options { get; }
    public string ProviderId => _classifier.ProviderId;
    public Version AdapterVersion => _classifier.AdapterVersion;
    public int OptionsSchemaVersion => _classifier.OptionsSchemaVersion;
    public ImmutableHashSet<string> Capabilities => _classifier.Capabilities;

    public Task<GuardrailsClassifierResult> ClassifyAsync(
        GuardrailsClassifierRequest request,
        CancellationToken cancellationToken) =>
        _classifier.ClassifyAsync((TOptions)Options, request, cancellationToken);
}

public sealed record GuardrailsAcceptedStagePlan(
    GuardrailsContentStage Stage,
    GuardrailsContentClass ContentClass,
    PinnedOrderedProviderBinding Binding,
    ImmutableArray<IGuardrailsClassifierBinding> Checks);

public sealed record GuardrailsAcceptedRunBinding(
    string ActorIssuer,
    string ActorId,
    string TenantId,
    string ProjectId,
    string RunId,
    string SessionId,
    string ActionId,
    string Purpose,
    string AcceptedSelectionRevision,
    string AcceptedSelectionHash,
    string GrantId,
    string GrantRevision,
    long ExecutionFence,
    PinnedProviderBinding PolicyBinding,
    AgtPolicyProviderOptions PolicyOptions,
    ImmutableArray<string> ProjectPolicyDocuments,
    ImmutableArray<GuardrailsAcceptedStagePlan> StagePlans);

public sealed record GuardrailsContentRequest(
    Guid OperationId,
    string SnapshotId,
    string SnapshotDigest,
    GuardrailsContentStage Stage,
    GuardrailsContentClass ContentClass,
    GuardrailsRemoteToolCallIdentity? RemoteToolCall,
    ImmutableArray<byte> CanonicalContent);

public sealed record GuardrailsRemoteToolCallIdentity(
    string AgentId,
    string NodeId,
    string ToolId,
    string ToolSchemaDigest,
    string PermissionMetadataDigest,
    string ArgumentsDigest);

public sealed record GuardrailsAuthorityBinding(
    string ActorIssuer,
    string ActorId,
    string TenantId,
    string ProjectId,
    string RunId,
    string SessionId,
    string ActionId,
    string Purpose,
    string AcceptedSelectionRevision,
    string AcceptedSelectionHash,
    string GrantId,
    string GrantRevision,
    long ExecutionFence);

public sealed record GuardrailsAuthorityCheckRequest(
    GuardrailsAuthorityBinding AcceptedAuthority,
    Guid OperationId,
    string SnapshotId,
    string SnapshotDigest,
    string ContentDigest,
    GuardrailsContentStage Stage,
    GuardrailsContentClass ContentClass,
    GuardrailsRemoteToolCallIdentity? RemoteToolCall);

public interface IGuardrailsCurrentAuthority
{
    Task<GuardrailsAuthorityStatus> CheckCurrentAsync(
        GuardrailsAuthorityCheckRequest request,
        CancellationToken cancellationToken);
}

public sealed record GuardrailsCheckEvidence(
    int Order,
    string ProviderId,
    string AdapterVersion,
    int OptionsSchemaVersion,
    string OptionsRevision,
    GuardrailsCheckOutcome Outcome,
    int FindingCount);

public sealed record GuardrailsEvaluationEvidence(
    Guid OperationId,
    string SnapshotId,
    string SnapshotDigest,
    string ContentDigest,
    string ActorIssuer,
    string ActorId,
    string TenantId,
    string ProjectId,
    string RunId,
    string SessionId,
    string ActionId,
    string Purpose,
    string AcceptedSelectionRevision,
    string AcceptedSelectionHash,
    string GrantId,
    string GrantRevision,
    long ExecutionFence,
    GuardrailsContentStage Stage,
    GuardrailsContentClass ContentClass,
    ImmutableArray<GuardrailsCheckEvidence> Checks,
    PolicyEvaluationOutcome BaselinePolicyOutcome,
    PolicyEvaluationReasonCode BaselinePolicyReasonCode,
    PolicyEvaluationOutcome PolicyOutcome,
    PolicyEvaluationReasonCode PolicyReasonCode,
    GuardrailsReasonCode ReasonCode);

public interface IGuardrailsEvidenceJournal
{
    Task<GuardrailsJournalAppendStatus> AppendAsync(
        GuardrailsEvaluationEvidence evidence,
        CancellationToken cancellationToken);
}

public sealed record GuardrailsGateResult(
    GuardrailsDecision Decision,
    GuardrailsReasonCode ReasonCode,
    PolicyEvaluationOutcome? PolicyOutcome,
    ImmutableArray<GuardrailsVersionedFinding> Findings);

public sealed class GuardrailsContentGate(
    AgtPolicyProvider? policyProvider,
    IGuardrailsCurrentAuthority? currentAuthority,
    IGuardrailsEvidenceJournal? evidenceJournal)
{
    public const int MaximumContentBytes = 262_144;
    public const int MaximumChecks = 16;
    public const int MaximumFindings = 48;
    public const string MaximumFindingsContextKey = "guardrails_findings";

    public async Task<GuardrailsGateResult> EvaluateAsync(
        GuardrailsAcceptedRunBinding acceptedRun,
        GuardrailsContentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(acceptedRun);
        ArgumentNullException.ThrowIfNull(request);

        if (policyProvider is null || currentAuthority is null || evidenceJournal is null)
            return Withhold(GuardrailsReasonCode.MissingDependency);
        if (!ValidateRequest(acceptedRun, request))
            return Withhold(GuardrailsReasonCode.InvalidRequest);

        var matchingPlans = acceptedRun.StagePlans
            .Where(item => item is not null &&
                item.Stage == request.Stage &&
                item.ContentClass == request.ContentClass)
            .Take(2)
            .ToArray();
        if (matchingPlans.Length == 0)
            return Withhold(GuardrailsReasonCode.UnsupportedStageOrContent);
        if (matchingPlans.Length != 1)
            return Withhold(GuardrailsReasonCode.InvalidConfiguration);
        var stagePlan = matchingPlans[0];

        try
        {
            ValidateAcceptedRun(acceptedRun, stagePlan, request);
            acceptedRun.PolicyOptions.Validate();
        }
        catch (ArgumentException)
        {
            return Withhold(GuardrailsReasonCode.InvalidConfiguration);
        }
        catch (InvalidOperationException)
        {
            return Withhold(GuardrailsReasonCode.InvalidConfiguration);
        }

        var contentDigest = ComputeContentDigest(request.CanonicalContent);
        var authority = await CheckAuthorityAsync(acceptedRun, request, contentDigest, cancellationToken)
            .ConfigureAwait(false);
        if (authority is not null)
            return Withhold(authority.Value);

        var checkEvidence = ImmutableArray.CreateBuilder<GuardrailsCheckEvidence>(stagePlan.Checks.Length);
        var findings = ImmutableArray.CreateBuilder<GuardrailsVersionedFinding>();
        var checkFailure = GuardrailsReasonCode.Allowed;
        var classifierRequest = new GuardrailsClassifierRequest(
            request.Stage, request.ContentClass, contentDigest, request.CanonicalContent);

        for (var index = 0; index < stagePlan.Checks.Length; index++)
        {
            var check = stagePlan.Checks[index];
            GuardrailsClassifierResult? result = null;
            GuardrailsReasonCode? classifierFailure = null;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(check.Options.RequestTimeout);
                try
                {
                    result = await check.ClassifyAsync(classifierRequest, timeout.Token)
                        .WaitAsync(check.Options.RequestTimeout, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    timeout.Cancel();
                    checkEvidence.Add(EvidenceFor(index, check, GuardrailsCheckOutcome.Failed, 0));
                    classifierFailure = GuardrailsReasonCode.ClassifierTimedOut;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    checkEvidence.Add(EvidenceFor(index, check, GuardrailsCheckOutcome.Failed, 0));
                    classifierFailure = GuardrailsReasonCode.ClassifierTimedOut;
                }
                catch (HttpRequestException)
                {
                    checkEvidence.Add(EvidenceFor(index, check, GuardrailsCheckOutcome.Failed, 0));
                    classifierFailure = GuardrailsReasonCode.ProviderUnavailable;
                }
                catch (GuardrailsClassifierException exception)
                {
                    checkEvidence.Add(EvidenceFor(index, check, GuardrailsCheckOutcome.Failed, 0));
                    classifierFailure = exception.Failure == GuardrailsClassifierFailure.Unavailable
                        ? GuardrailsReasonCode.ProviderUnavailable
                        : GuardrailsReasonCode.ClassifierFailed;
                }
            }

            if (classifierFailure is null && !ValidateClassifierResult(result))
            {
                checkEvidence.Add(EvidenceFor(index, check, GuardrailsCheckOutcome.Failed, 0));
                classifierFailure = GuardrailsReasonCode.ClassifierFailed;
            }

            authority = await CheckAuthorityAsync(acceptedRun, request, contentDigest, cancellationToken)
                .ConfigureAwait(false);
            if (authority is not null)
                return Withhold(authority.Value);

            if (classifierFailure is not null)
            {
                checkFailure = classifierFailure.Value;
                break;
            }

            var validatedResult = result!;
            var resultOutcome = validatedResult.Outcome switch
            {
                GuardrailsClassifierOutcome.Clear => GuardrailsCheckOutcome.Clear,
                GuardrailsClassifierOutcome.Findings => GuardrailsCheckOutcome.Findings,
                GuardrailsClassifierOutcome.Error => GuardrailsCheckOutcome.Failed,
                _ => GuardrailsCheckOutcome.Failed
            };
            checkEvidence.Add(EvidenceFor(index, check, resultOutcome, validatedResult.Findings.Length));

            if (validatedResult.Outcome == GuardrailsClassifierOutcome.Error)
            {
                checkFailure = validatedResult.Failure == GuardrailsClassifierFailure.Unavailable
                    ? GuardrailsReasonCode.ProviderUnavailable
                    : GuardrailsReasonCode.ClassifierFailed;
                break;
            }

            findings.AddRange(validatedResult.Findings.Select(finding =>
                new GuardrailsVersionedFinding(
                    index,
                    check.Pin.ProviderId,
                    check.Pin.AdapterVersion.ToString(),
                    check.Pin.OptionsSchemaVersion,
                    check.Pin.OptionsRevision,
                    finding.Code,
                    finding.Severity)));
            if (findings.Count > MaximumFindings)
            {
                checkFailure = GuardrailsReasonCode.ClassifierFailed;
                break;
            }
        }

        var baselinePolicyResult = checkFailure == GuardrailsReasonCode.Allowed
            ? policyProvider.Evaluate(
                acceptedRun.PolicyOptions,
                CreatePolicyRequest(acceptedRun, request, []))
            : PolicyError(acceptedRun.PolicyOptions);
        var policyResult = checkFailure == GuardrailsReasonCode.Allowed
            ? policyProvider.Evaluate(
                acceptedRun.PolicyOptions,
                CreatePolicyRequest(acceptedRun, request, findings.ToImmutable()))
            : PolicyError(acceptedRun.PolicyOptions);
        var effectivePolicyResult = GuardrailsPolicyNarrowing.Apply(baselinePolicyResult, policyResult);

        var reason = checkFailure != GuardrailsReasonCode.Allowed
            ? checkFailure
            : effectivePolicyResult.Outcome switch
            {
                PolicyEvaluationOutcome.Allow => GuardrailsReasonCode.Allowed,
                PolicyEvaluationOutcome.Deny => GuardrailsReasonCode.PolicyDenied,
                _ => GuardrailsReasonCode.PolicyEvaluationFailed
            };
        var evidence = CreateEvidence(
            acceptedRun,
            request,
            contentDigest,
            checkEvidence.ToImmutable(),
            baselinePolicyResult,
            policyResult,
            reason);

        var append = await evidenceJournal.AppendAsync(evidence, cancellationToken).ConfigureAwait(false);
        if (append == GuardrailsJournalAppendStatus.Duplicate)
            return Withhold(GuardrailsReasonCode.JournalDuplicate, effectivePolicyResult, findings.ToImmutable());
        if (append != GuardrailsJournalAppendStatus.Appended)
            return Withhold(GuardrailsReasonCode.JournalUnavailable, effectivePolicyResult, findings.ToImmutable());

        authority = await CheckAuthorityAsync(acceptedRun, request, contentDigest, cancellationToken)
            .ConfigureAwait(false);
        if (authority is not null)
            return Withhold(authority.Value, effectivePolicyResult, findings.ToImmutable());

        if (checkFailure != GuardrailsReasonCode.Allowed ||
            effectivePolicyResult.Outcome != PolicyEvaluationOutcome.Allow)
            return Withhold(reason, effectivePolicyResult, findings.ToImmutable());

        return new(
            GuardrailsDecision.Allow,
            GuardrailsReasonCode.Allowed,
            effectivePolicyResult.Outcome,
            findings.ToImmutable());
    }

    private static bool ValidateRequest(
        GuardrailsAcceptedRunBinding acceptedRun,
        GuardrailsContentRequest request) =>
        acceptedRun is not null &&
        request is not null &&
        request.OperationId != Guid.Empty &&
        IsIdentifier(request.SnapshotId) &&
        IsSha256(request.SnapshotDigest) &&
        Enum.IsDefined(request.Stage) &&
        Enum.IsDefined(request.ContentClass) &&
        !request.CanonicalContent.IsDefaultOrEmpty &&
        request.CanonicalContent.Length <= MaximumContentBytes &&
        (request.Stage != GuardrailsContentStage.ToolResult ||
         request.RemoteToolCall is { } toolCall && IsValidToolCallIdentity(toolCall)) &&
        (request.Stage == GuardrailsContentStage.ToolResult || request.RemoteToolCall is null) &&
        !acceptedRun.StagePlans.IsDefault &&
        acceptedRun.StagePlans.Length <= MaximumChecks;

    private static bool IsValidToolCallIdentity(GuardrailsRemoteToolCallIdentity identity) =>
        AgtPolicyProvider.IsIdentifier(identity.AgentId) &&
        AgtPolicyProvider.IsIdentifier(identity.NodeId) &&
        AgtPolicyProvider.IsIdentifier(identity.ToolId) &&
        IsSha256(identity.ToolSchemaDigest) &&
        IsSha256(identity.PermissionMetadataDigest) &&
        IsSha256(identity.ArgumentsDigest);

    private static void ValidateAcceptedRun(
        GuardrailsAcceptedRunBinding acceptedRun,
        GuardrailsAcceptedStagePlan stagePlan,
        GuardrailsContentRequest request)
    {
        if (acceptedRun.PolicyOptions is null ||
            stagePlan is null ||
            stagePlan.Binding is null ||
            stagePlan.Binding.Bindings.IsDefault ||
            stagePlan.Checks.IsDefault ||
            stagePlan.Checks.Any(check => check is null) ||
            stagePlan.Binding.Bindings.Any(binding => binding is null))
            throw new ArgumentException("The accepted Guardrails run binding is incomplete.");

        if (!Uri.TryCreate(acceptedRun.ActorIssuer, UriKind.Absolute, out var issuer) ||
            issuer.Scheme != Uri.UriSchemeHttps ||
            !AgtPolicyProvider.IsIdentifier(acceptedRun.ActorId) ||
            !AgtPolicyProvider.IsIdentifier(acceptedRun.TenantId) ||
            !AgtPolicyProvider.IsIdentifier(acceptedRun.ProjectId) ||
            !AgtPolicyProvider.IsIdentifier(acceptedRun.RunId) ||
            !AgtPolicyProvider.IsIdentifier(acceptedRun.SessionId) ||
            !AgtPolicyProvider.IsIdentifier(acceptedRun.ActionId) ||
            !AgtPolicyProvider.IsIdentifier(acceptedRun.Purpose) ||
            !AgtPolicyProvider.IsIdentifier(acceptedRun.AcceptedSelectionRevision) ||
            !IsSha256(acceptedRun.AcceptedSelectionHash) ||
            !AgtPolicyProvider.IsIdentifier(acceptedRun.GrantId) ||
            !AgtPolicyProvider.IsIdentifier(acceptedRun.GrantRevision) ||
            acceptedRun.ExecutionFence < 1 ||
            acceptedRun.PolicyBinding is null ||
            acceptedRun.PolicyBinding.Seam != ProviderSeam.Policy ||
            acceptedRun.PolicyBinding.ProviderId != AgtPolicyProvider.ProviderId ||
            acceptedRun.PolicyBinding.AdapterVersion != AgtPolicyProvider.AdapterVersion ||
            acceptedRun.PolicyBinding.OptionsSchemaVersion != AgtPolicyProvider.OptionsSchemaVersion ||
            acceptedRun.PolicyBinding.OptionsRevision != acceptedRun.PolicyOptions.OptionsRevision ||
            acceptedRun.PolicyBinding.Resource is null ||
            acceptedRun.PolicyBinding.Resource.Seam != acceptedRun.PolicyBinding.Seam ||
            acceptedRun.PolicyBinding.Resource.ProviderId != acceptedRun.PolicyBinding.ProviderId ||
            acceptedRun.PolicyBinding.Resource.ResourceId != acceptedRun.PolicyOptions.ResourceId ||
            acceptedRun.PolicyBinding.Resource.Generation < 1 ||
            acceptedRun.PolicyBinding.Resource.Generation != acceptedRun.PolicyOptions.ResourceGeneration ||
            acceptedRun.PolicyBinding.RunId != acceptedRun.RunId ||
            acceptedRun.PolicyBinding.NegotiatedCapabilities is null ||
            !acceptedRun.PolicyBinding.NegotiatedCapabilities.Contains(PolicyProviderCapabilities.AgentActionEvaluation) ||
            acceptedRun.ProjectPolicyDocuments.IsDefault ||
            acceptedRun.ProjectPolicyDocuments.Length > 32 ||
            acceptedRun.ProjectPolicyDocuments.Any(string.IsNullOrWhiteSpace) ||
            acceptedRun.StagePlans.IsDefault ||
            acceptedRun.StagePlans.Any(item => item is null) ||
            stagePlan.Binding.Seam != ProviderSeam.Guardrails ||
            stagePlan.Binding.RunId != acceptedRun.RunId ||
            stagePlan.Checks.Length == 0 ||
            stagePlan.Binding.Bindings.IsDefault ||
            stagePlan.Checks.Length != stagePlan.Binding.Bindings.Length ||
            stagePlan.Checks.Length > MaximumChecks ||
            stagePlan.Stage != request.Stage ||
            stagePlan.ContentClass != request.ContentClass)
            throw new ArgumentException("The accepted Guardrails run binding is invalid.");
        if (!acceptedRun.ProjectPolicyDocuments.IsEmpty)
            AgtPolicyProvider.ValidatePolicyDocuments(
                acceptedRun.ProjectPolicyDocuments, requireDefaultDeny: false);

        var requiredCapability = GuardrailsProviderCapabilities.For(request.Stage, request.ContentClass);
        if (requiredCapability is null)
            throw new InvalidOperationException("No capability is defined for this content stage.");

        for (var index = 0; index < stagePlan.Checks.Length; index++)
        {
            var check = stagePlan.Checks[index];
            var pin = stagePlan.Binding.Bindings[index];
            if (check.Pin is null ||
                check.Options is null ||
                check.Capabilities is null ||
                pin.Resource is null ||
                check.Pin.Resource is null ||
                pin.NegotiatedCapabilities is null ||
                check.Pin.NegotiatedCapabilities is null)
                throw new ArgumentException("A Guardrails adapter pin is incomplete.");
            check.Options.Validate();
            if (pin.Seam != ProviderSeam.Guardrails ||
                pin.RunId != acceptedRun.RunId ||
                check.Pin.Seam != pin.Seam ||
                check.Pin.RunId != pin.RunId ||
                pin.ProviderId != check.Options.ProviderId ||
            pin.AdapterVersion != check.Options.AdapterVersion ||
            pin.OptionsSchemaVersion != check.Options.OptionsSchemaVersion ||
            pin.OptionsRevision != check.Options.OptionsRevision ||
            check.ProviderId != check.Options.ProviderId ||
            check.AdapterVersion != check.Options.AdapterVersion ||
            check.OptionsSchemaVersion != check.Options.OptionsSchemaVersion ||
            pin.ProviderId != check.Pin.ProviderId ||
            pin.AdapterVersion != check.Pin.AdapterVersion ||
            pin.OptionsSchemaVersion != check.Pin.OptionsSchemaVersion ||
            pin.OptionsRevision != check.Pin.OptionsRevision ||
            pin.Hosting != check.Pin.Hosting ||
            pin.Resource.Seam != pin.Seam ||
            pin.Resource.ProviderId != pin.ProviderId ||
            pin.Resource.Generation < 1 ||
            check.Pin.Resource.Seam != check.Pin.Seam ||
            check.Pin.Resource.ProviderId != check.Pin.ProviderId ||
            check.Pin.Resource.Generation < 1 ||
            pin.Resource.Seam != check.Pin.Resource.Seam ||
            pin.Resource.ProviderId != check.Pin.Resource.ProviderId ||
            pin.Resource.ResourceId != check.Pin.Resource.ResourceId ||
            pin.Resource.Generation != check.Pin.Resource.Generation ||
            !pin.NegotiatedCapabilities.SetEquals(check.Pin.NegotiatedCapabilities) ||
            !pin.NegotiatedCapabilities.Contains(requiredCapability) ||
                !pin.NegotiatedCapabilities.Contains(GuardrailsProviderCapabilities.PromptInjectionDetection) ||
                !check.Capabilities.Contains(requiredCapability) ||
                !check.Capabilities.Contains(GuardrailsProviderCapabilities.PromptInjectionDetection))
                throw new ArgumentException("A Guardrails adapter does not match its immutable stage pin.");
        }
    }

    private static bool ValidateClassifierResult(GuardrailsClassifierResult? result)
    {
        if (result is null || !Enum.IsDefined(result.Outcome) || result.Findings.IsDefault ||
            result.Findings.Length > MaximumFindings)
            return false;

        return result.Outcome switch
        {
            GuardrailsClassifierOutcome.Clear =>
                result.Findings.IsEmpty && result.Failure is null,
            GuardrailsClassifierOutcome.Findings =>
                !result.Findings.IsEmpty && result.Failure is null &&
                result.Findings.All(finding =>
                    finding is not null &&
                    AgtPolicyProvider.IsIdentifier(finding.Code) &&
                    finding.Code.Length <= 64 &&
                    Enum.IsDefined(finding.Severity)),
            GuardrailsClassifierOutcome.Error =>
                result.Findings.IsEmpty && result.Failure is { } failure && Enum.IsDefined(failure),
            _ => false
        };
    }

    private static AgtPolicyEvaluationRequest CreatePolicyRequest(
        GuardrailsAcceptedRunBinding acceptedRun,
        GuardrailsContentRequest request,
        ImmutableArray<GuardrailsVersionedFinding> findings)
    {
        var context = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["guardrails_stage"] = request.Stage.ToString(),
            ["guardrails_content_class"] = request.ContentClass.ToString(),
            ["guardrails_finding_count"] = findings.Length,
            [MaximumFindingsContextKey] = string.Join(
                ';',
                findings.Select(finding => $"{finding.Code}:{finding.Severity}"))
        };
        return new AgtPolicyEvaluationRequest(
            acceptedRun.ActorId,
            acceptedRun.ActionId,
            context,
            acceptedRun.ProjectPolicyDocuments);
    }

    private static AgtPolicyEvaluationResult PolicyError(AgtPolicyProviderOptions options) =>
        new(
            PolicyEvaluationOutcome.Error,
            PolicyEvaluationReasonCode.EvaluationFailed,
            AgtPolicyProvider.ProviderId,
            AgtPolicyProvider.AdapterVersion,
            options.OptionsSchemaVersion,
            options.OptionsRevision);

    private static GuardrailsEvaluationEvidence CreateEvidence(
        GuardrailsAcceptedRunBinding acceptedRun,
        GuardrailsContentRequest request,
        string contentDigest,
        ImmutableArray<GuardrailsCheckEvidence> checks,
        AgtPolicyEvaluationResult baselinePolicyResult,
        AgtPolicyEvaluationResult policyResult,
        GuardrailsReasonCode reasonCode) =>
        new(
            request.OperationId,
            request.SnapshotId,
            request.SnapshotDigest,
            contentDigest,
            acceptedRun.ActorIssuer,
            acceptedRun.ActorId,
            acceptedRun.TenantId,
            acceptedRun.ProjectId,
            acceptedRun.RunId,
            acceptedRun.SessionId,
            acceptedRun.ActionId,
            acceptedRun.Purpose,
            acceptedRun.AcceptedSelectionRevision,
            acceptedRun.AcceptedSelectionHash,
            acceptedRun.GrantId,
            acceptedRun.GrantRevision,
            acceptedRun.ExecutionFence,
            request.Stage,
            request.ContentClass,
            checks,
            baselinePolicyResult.Outcome,
            baselinePolicyResult.ReasonCode,
            policyResult.Outcome,
            policyResult.ReasonCode,
            reasonCode);

    private static GuardrailsCheckEvidence EvidenceFor(
        int index,
        IGuardrailsClassifierBinding check,
        GuardrailsCheckOutcome outcome,
        int findingCount) =>
        new(
            index,
            check.Pin.ProviderId,
            check.Pin.AdapterVersion.ToString(),
            check.Pin.OptionsSchemaVersion,
            check.Pin.OptionsRevision,
            outcome,
            findingCount);

    private async Task<GuardrailsReasonCode?> CheckAuthorityAsync(
        GuardrailsAcceptedRunBinding acceptedRun,
        GuardrailsContentRequest request,
        string contentDigest,
        CancellationToken cancellationToken)
    {
        var status = await currentAuthority!.CheckCurrentAsync(
            new(
                new(
                    acceptedRun.ActorIssuer,
                    acceptedRun.ActorId,
                    acceptedRun.TenantId,
                    acceptedRun.ProjectId,
                    acceptedRun.RunId,
                    acceptedRun.SessionId,
                    acceptedRun.ActionId,
                    acceptedRun.Purpose,
                    acceptedRun.AcceptedSelectionRevision,
                    acceptedRun.AcceptedSelectionHash,
                    acceptedRun.GrantId,
                    acceptedRun.GrantRevision,
                    acceptedRun.ExecutionFence),
                request.OperationId,
                request.SnapshotId,
                request.SnapshotDigest,
                contentDigest,
                request.Stage,
                request.ContentClass,
                request.RemoteToolCall),
            cancellationToken).ConfigureAwait(false);
        return status switch
        {
            GuardrailsAuthorityStatus.Current => null,
            GuardrailsAuthorityStatus.Revoked => GuardrailsReasonCode.AuthorityRevoked,
            GuardrailsAuthorityStatus.Stale => GuardrailsReasonCode.AuthorityStale,
            _ => GuardrailsReasonCode.AuthorityUnavailable
        };
    }

    private static bool IsIdentifier(string value) =>
        AgtPolicyProvider.IsIdentifier(value);

    private static bool IsSha256(string value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static string ComputeContentDigest(ImmutableArray<byte> canonicalContent) =>
        Convert.ToHexString(SHA256.HashData(canonicalContent.AsSpan()));

    private static GuardrailsGateResult Withhold(
        GuardrailsReasonCode reason,
        AgtPolicyEvaluationResult? policyResult = null,
        ImmutableArray<GuardrailsVersionedFinding> findings = default) =>
        new(
            GuardrailsDecision.Withhold,
            reason,
            policyResult?.Outcome,
            findings.IsDefault ? [] : findings);
}

public sealed class GuardrailsClassifierException(GuardrailsClassifierFailure failure)
    : Exception("The configured Guardrails classifier failed.")
{
    public GuardrailsClassifierFailure Failure { get; } = failure;
}
