using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using AgentControlSpecification;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator.Core;
using Agentweaver.Policy.Acs;

namespace Agentweaver.Orchestrator;

/// <summary>One immutable native Policy revision returned by an exact-scope runtime source.</summary>
public sealed record RuntimeNativePolicyRevision(
    string ScopeType,
    string ScopeId,
    string PolicyId,
    long Revision,
    string ContentSha256,
    NativeAcsArtifact Artifact);

/// <summary>Accepted run source and current source revisions that must independently permit model use.</summary>
public sealed record RuntimeNativePolicySourceSnapshot(
    ImmutableArray<RuntimeNativePolicyRevision> Accepted,
    ImmutableArray<RuntimeNativePolicyRevision> Current);

/// <summary>Exact-scope Policy source reserved for explicit G1 test composition.</summary>
public interface IRuntimeNativePolicySource
{
    Task<RuntimeNativePolicySourceSnapshot> ReadAsync(
        RuntimeRegistration registration,
        AuthorizedRunSelection accepted,
        CancellationToken cancellationToken);
}

/// <summary>Owner-side admission binding retained before a native-policy admission can be verified.</summary>
public sealed record RuntimeNativePolicySourceReference(
    string ScopeType,
    string ScopeId,
    string PolicyId,
    long Revision,
    string ContentSha256);

public sealed record RuntimeNativePolicyAdmissionBinding(
    Guid EventId,
    string RequestHash,
    string RegistrationHash,
    string BindingHash,
    ImmutableArray<RuntimeNativePolicySourceReference> AcceptedSources,
    ImmutableArray<RuntimeNativePolicySourceReference> CurrentSources,
    string? GuardrailsBindingHash = null);

/// <summary>Append-only retention boundary for exact native Policy admission bindings.</summary>
public interface IRuntimeNativePolicyAdmissionStore
{
    Task<RuntimeNativePolicyAdmissionBinding?> ReadAsync(
        Guid eventId, CancellationToken cancellationToken);

    Task StoreAsync(
        RuntimeNativePolicyAdmissionBinding binding, CancellationToken cancellationToken);
}

/// <summary>Validated native Policy revisions and guardrail requirements prepared for one model turn.</summary>
public sealed record RuntimeNativePolicyPreparation(
    RuntimeNativePolicyAdmissionBinding Binding,
    ImmutableArray<RuntimeNativePolicyPreparedRevision> Revisions,
    ImmutableArray<NativeAcsGuardrailsRequiredCheck> RequiredChecks);

/// <summary>One validated native Policy revision and its parsed Guardrails requirements.</summary>
public sealed record RuntimeNativePolicyPreparedRevision(
    RuntimeNativePolicyRevision Source,
    NativeAcsGuardrailsPolicy Guardrails,
    bool AcceptedAtRun,
    bool Current);

/// <summary>
/// Applies the opt-in native Policy profile to the Orchestrator model-turn boundary. Production
/// registration remains unchanged until a separately admitted activation supplies a source and store.
/// </summary>
public sealed class RuntimeNativePolicyGate(
    IRuntimeNativePolicySource source,
    IRuntimeNativePolicyAdmissionStore admissionStore,
    NativeAcsPolicyEvaluator evaluator)
{
    public const string UnavailableCode = "runtime_native_policy_unavailable";
    public const string DeniedCode = "runtime_native_policy_denied";
    public const string StaleCode = "runtime_native_policy_stale";

    public async Task<RuntimeNativePolicyPreparation> PrepareAuthorizeAsync(
        RuntimeRegistration registration,
        AuthorizedRunSelection accepted,
        RuntimeActionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(accepted);
        ArgumentNullException.ThrowIfNull(request);
        if (request.ActionId != "model.turn")
            throw new ArgumentException("The native Policy gate applies only to model.turn.", nameof(request));

        var modelFacts = ReadModelFacts(registration, accepted);
        var revisions = await ReadAndValidateAsync(
            registration, accepted, cancellationToken).ConfigureAwait(false);
        foreach (var revision in revisions)
        {
            var result = await evaluator.EvaluateComposedAsync(
                revision.Source.Artifact,
                NativeAcsDomainId.ModelUse,
                InterventionPoint.PreModelCall,
                Snapshot(registration, request, modelFacts),
                cancellationToken).ConfigureAwait(false);
            RequireAllowed(result);
        }

        var requiredChecks = CombineRequiredChecks(
            revisions.Select(item => item.Guardrails.RequiredChecks));
        var acceptedSources = revisions
            .Where(item => item.AcceptedAtRun)
            .Select(ToReference)
            .ToImmutableArray();
        var currentSources = revisions
            .Where(item => item.Current)
            .Select(ToReference)
            .ToImmutableArray();
        var sourceBinding = string.Join(
            "\n",
            ["accepted", .. acceptedSources.Select(SerializeReference), "current",
                .. currentSources.Select(SerializeReference)]);
        var binding = new RuntimeNativePolicyAdmissionBinding(
            request.EventId,
            RuntimeActionContract.Hash(request),
            RuntimeContractValidation.RegistrationHash(registration),
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sourceBinding))),
            acceptedSources,
            currentSources);
        return new(binding, revisions, requiredChecks);

        static RuntimeNativePolicySourceReference ToReference(
            RuntimeNativePolicyPreparedRevision item) =>
            new RuntimeNativePolicySourceReference(
                item.Source.ScopeType,
                item.Source.ScopeId,
                item.Source.PolicyId,
                item.Source.Revision,
                item.Source.ContentSha256);

        static string SerializeReference(RuntimeNativePolicySourceReference item) =>
            $"{item.ScopeType}\0{item.ScopeId}\0{item.PolicyId}\0" +
            $"{item.Revision}\0{item.ContentSha256}";
    }

    public async Task CommitAsync(
        RuntimeNativePolicyPreparation preparation,
        string? guardrailsBindingHash,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        try
        {
            await admissionStore.StoreAsync(
                preparation.Binding with { GuardrailsBindingHash = guardrailsBindingHash },
                cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            throw new RuntimeAuthorizationException(UnavailableCode);
        }
    }

    public async Task<RuntimeNativePolicyPreparation> RequireVerifyAsync(
        RuntimeRegistration registration,
        AuthorizedRunSelection accepted,
        RuntimeActionRequest request,
        CancellationToken cancellationToken)
    {
        var current = await PrepareAuthorizeAsync(
            registration, accepted, request, cancellationToken).ConfigureAwait(false);
        RuntimeNativePolicyAdmissionBinding? admitted;
        try
        {
            admitted = await admissionStore.ReadAsync(request.EventId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            throw new RuntimeAuthorizationException(UnavailableCode);
        }

        if (admitted is null ||
            admitted.EventId != current.Binding.EventId ||
            admitted.RequestHash != current.Binding.RequestHash ||
            admitted.RegistrationHash != current.Binding.RegistrationHash ||
            admitted.BindingHash != current.Binding.BindingHash ||
            !admitted.AcceptedSources.SequenceEqual(current.Binding.AcceptedSources) ||
            !admitted.CurrentSources.SequenceEqual(current.Binding.CurrentSources) ||
            !current.RequiredChecks.IsDefaultOrEmpty &&
                string.IsNullOrWhiteSpace(admitted.GuardrailsBindingHash) ||
            admitted.AcceptedSources.Length != current.Binding.AcceptedSources.Length ||
            admitted.CurrentSources.Length != current.Binding.CurrentSources.Length)
            throw new RuntimeAuthorizationException(StaleCode);
        return current;
    }

    public async Task<string> RequireGuardrailsAsync(
        RuntimeNativePolicyPreparation preparation,
        RuntimeRegistration registration,
        RuntimeActionRequest request,
        ImmutableArray<NativeAcsGuardrailsCheckFacts> checks,
        ImmutableArray<NativeAcsGuardrailsFindingFacts> allFindings,
        CancellationToken cancellationToken)
    {
        foreach (var revision in preparation.Revisions)
        {
            var required = revision.Guardrails.RequiredChecks;
            if (required.IsDefaultOrEmpty)
                continue;
            var checkIds = required.Select(check => check.ProviderId).ToHashSet(StringComparer.Ordinal);
            var applicableChecks = checks.Where(check => checkIds.Contains(check.ProviderId)).ToImmutableArray();
            var applicableFindings = allFindings
                .Where(finding => checkIds.Contains(finding.CheckId))
                .ToImmutableArray();
            var result = await evaluator.EvaluateComposedAsync(
                revision.Source.Artifact,
                NativeAcsDomainId.Guardrails,
                InterventionPoint.PreModelCall,
                Snapshot(
                    registration,
                    request,
                    modelUse: null,
                    guardrails: new NativeAcsGuardrailsEvaluationFacts(
                        "userInput", "text", applicableChecks, applicableFindings)),
                cancellationToken).ConfigureAwait(false);
            RequireAllowed(result);
        }
        var material = preparation.Binding.BindingHash + "\n" +
            string.Join("\n", checks.Select(check =>
                $"{check.ProviderId}\0{check.AdapterVersion}\0{check.OptionsSchemaVersion}\0{check.OptionsRevision}")) +
            "\n" +
            string.Join("\n", allFindings.Select(finding =>
                $"{finding.CheckId}\0{finding.Code}\0{finding.Severity}"));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private async Task<ImmutableArray<RuntimeNativePolicyPreparedRevision>> ReadAndValidateAsync(
        RuntimeRegistration registration,
        AuthorizedRunSelection accepted,
        CancellationToken cancellationToken)
    {
        RuntimeNativePolicySourceSnapshot snapshot;
        try
        {
            snapshot = await source.ReadAsync(
                registration, accepted, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RuntimeAuthorizationException)
        {
            throw;
        }
        catch
        {
            throw new RuntimeAuthorizationException(UnavailableCode);
        }

        if (snapshot.Accepted.IsDefault || snapshot.Current.IsDefault ||
            !HasOnePlatform(snapshot.Accepted) || !HasOnePlatform(snapshot.Current))
            throw new RuntimeAuthorizationException(UnavailableCode);

        var revisions = snapshot.Accepted.Select(item => (Item: item, AcceptedAtRun: true))
            .Concat(snapshot.Current.Select(item => (Item: item, AcceptedAtRun: false)))
            .ToImmutableArray();
        var prepared = ImmutableArray.CreateBuilder<RuntimeNativePolicyPreparedRevision>();
        foreach (var pair in revisions)
        {
            var revision = pair.Item;
            if (revision is null ||
                !IsIdentifier(revision.ScopeType) ||
                !IsIdentifier(revision.ScopeId) ||
                !IsIdentifier(revision.PolicyId) ||
                revision.Revision < 1 ||
                !string.Equals(
                    NativeAcsArtifactHash.Compute(revision.Artifact),
                    revision.ContentSha256,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    revision.ContentSha256,
                    revision.Artifact.ContentSha256,
                    StringComparison.Ordinal))
                throw new RuntimeAuthorizationException(UnavailableCode);
            if (!MatchesScope(revision, accepted))
                throw new RuntimeAuthorizationException(StaleCode);
            var inspection = NativeAcsUnifiedPolicyProfile.Inspect(revision.Artifact);
            if (!inspection.IsValid || inspection.GuardrailsPolicy is null)
                throw new RuntimeAuthorizationException(UnavailableCode);
            prepared.Add(new(
                revision,
                inspection.GuardrailsPolicy,
                pair.AcceptedAtRun,
                !pair.AcceptedAtRun));
        }

        return prepared
            .OrderBy(item => item.Source.ScopeType, StringComparer.Ordinal)
            .ThenBy(item => item.Source.ScopeId, StringComparer.Ordinal)
            .ThenBy(item => item.Source.Revision)
            .ToImmutableArray();
    }

    private static bool HasOnePlatform(ImmutableArray<RuntimeNativePolicyRevision> revisions) =>
        revisions.Length is >= 1 and <= 2 &&
        revisions.Count(item => item is not null &&
            item.ScopeType == "Platform" && item.ScopeId == "default") == 1 &&
        revisions.Where(item => item is not null && item.ScopeType == "Project").Count() <= 1;

    private static bool MatchesScope(
        RuntimeNativePolicyRevision revision,
        AuthorizedRunSelection accepted) =>
        revision.ScopeType == "Platform" && revision.ScopeId == "default" ||
        revision.ScopeType == "Project" && revision.ScopeId == accepted.Selection.ProjectId;

    private static NativeAcsModelUseEvaluationFacts ReadModelFacts(
        RuntimeRegistration registration,
        AuthorizedRunSelection accepted)
    {
        var binding = registration.Binding;
        RuntimeAcceptedModelSelection selection;
        try
        {
            selection = RuntimeAcceptedModelSelection.Read(
                accepted.Selection.Snapshot,
                binding.ModelSelectionReference ??
                    throw new RuntimeAuthorizationException("runtime_native_policy_model_mismatch"),
                binding.ReasoningEffort);
            RuntimeContractValidation.Validate(registration);
        }
        catch (RuntimeAuthorizationException)
        {
            throw;
        }
        catch
        {
            throw new RuntimeAuthorizationException("runtime_native_policy_model_mismatch");
        }

        var pin = binding.ModelBindingPin;
        var selectionHash = RuntimeContractValidation.Hash(
            Encoding.UTF8.GetBytes(accepted.Selection.Snapshot.GetRawText()));
        if (accepted.Authorization.ActorId != binding.ActorId ||
            accepted.Authorization.Issuer != binding.ActorIssuer ||
            accepted.Authorization.TenantId != binding.TenantId ||
            accepted.Selection.ProjectId != binding.ProjectId ||
            accepted.Selection.RunId != binding.RunId ||
            accepted.Selection.ProjectRevision != binding.ProjectRevision ||
            accepted.Selection.ProjectConfigurationRevision != binding.ProjectConfigurationRevision ||
            accepted.Selection.PlatformRuntimeRevision != binding.PlatformRuntimeRevision ||
            accepted.Selection.ContextRevision != binding.ContextRevision ||
            selectionHash != binding.AcceptedSelectionHash ||
            pin is null ||
            selection.ModelBindingPin != pin ||
            selection.Reference != pin.ModelSelectionReference ||
            selection.SourceMode != pin.SourceMode ||
            binding.ModelSelectionReference != pin.ModelSelectionReference ||
            binding.ModelSourceMode != pin.SourceMode ||
            binding.ReasoningEffort != pin.ReasoningEffort)
            throw new RuntimeAuthorizationException("runtime_native_policy_model_mismatch");

        return new(
            pin.ModelSelectionReference,
            pin.ModelId,
            pin.SourceMode,
            pin.ConfigurationRevision,
            pin.ConfigurationHash,
            pin.ProviderType,
            pin.ProviderId);
    }

    private static NativeAcsEvaluationSnapshot Snapshot(
        RuntimeRegistration registration,
        RuntimeActionRequest request,
        NativeAcsModelUseEvaluationFacts? modelUse = null,
        NativeAcsGuardrailsEvaluationFacts? guardrails = null) =>
        new(
            new SessionIdentity(
                registration.Binding.ProjectId,
                registration.Binding.RunId,
                registration.Binding.SessionId),
            registration.Binding.TenantId,
            registration.Binding.AgentId,
            request.ActionId)
        {
            ModelUse = modelUse,
            Guardrails = guardrails
        };

    private static ImmutableArray<NativeAcsGuardrailsRequiredCheck> CombineRequiredChecks(
        IEnumerable<ImmutableArray<NativeAcsGuardrailsRequiredCheck>> lists)
    {
        var combined = new List<NativeAcsGuardrailsRequiredCheck>();
        var byId = new Dictionary<string, NativeAcsGuardrailsRequiredCheck>(StringComparer.Ordinal);
        foreach (var check in lists.SelectMany(list => list.IsDefault ? [] : list))
        {
            if (byId.TryGetValue(check.ProviderId, out var existing))
            {
                if (existing != check)
                    throw new RuntimeAuthorizationException(StaleCode);
                continue;
            }
            byId.Add(check.ProviderId, check);
            combined.Add(check);
        }
        return combined.ToImmutableArray();
    }

    private static void RequireAllowed(NativeAcsEvaluationResult result)
    {
        if (result.Outcome == PolicyEvaluationOutcome.Allow &&
            result.ReasonCode == PolicyEvaluationReasonCode.Allowed)
            return;
        throw new RuntimeAuthorizationException(
            result.Outcome == PolicyEvaluationOutcome.Deny ? DeniedCode : UnavailableCode);
    }

    private static bool IsIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 256 &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':');
}
