using System.Collections.Concurrent;
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

/// <summary>Reads exact-scope Policy revisions for runtime authorization.</summary>
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
    string? GuardrailsBindingHash = null,
    RuntimeActionRequest? Request = null);

/// <summary>Append-only retention boundary for exact native Policy admission bindings.</summary>
public interface IRuntimeNativePolicyAdmissionStore
{
    Task<RuntimeNativePolicyAdmissionBinding?> ReadAsync(
        Guid eventId, CancellationToken cancellationToken);

    Task StoreAsync(
        RuntimeNativePolicyAdmissionBinding binding, CancellationToken cancellationToken);
}

/// <summary>Reads the current exact Platform and Project Policy revisions from Projects.</summary>
internal sealed class RuntimeNativePolicySource(
    ProjectsRunSelectionClient projects,
    IHttpContextAccessor httpContext) : IRuntimeNativePolicySource
{
    public async Task<RuntimeNativePolicySourceSnapshot> ReadAsync(
        RuntimeRegistration registration,
        AuthorizedRunSelection accepted,
        CancellationToken cancellationToken)
    {
        if (httpContext.HttpContext is not { } context)
            throw new RuntimeAuthorizationException(RuntimeNativePolicyGate.UnavailableCode);
        var current = await projects.ReadCurrentNativePolicyAsync(
            context, registration.Binding.ProjectId, cancellationToken).ConfigureAwait(false);
        return new RuntimeNativePolicySourceSnapshot(current, current);
    }
}

/// <summary>Retains immutable native Policy bindings for the process lifetime.</summary>
internal sealed class RuntimeNativePolicyAdmissionStore
    : IRuntimeNativePolicyAdmissionStore
{
    private readonly ConcurrentDictionary<Guid, RuntimeNativePolicyAdmissionBinding> _bindings = new();

    public Task<RuntimeNativePolicyAdmissionBinding?> ReadAsync(
        Guid eventId, CancellationToken cancellationToken) =>
        Task.FromResult(_bindings.TryGetValue(eventId, out var binding) ? binding : null);

    public Task StoreAsync(
        RuntimeNativePolicyAdmissionBinding binding, CancellationToken cancellationToken)
    {
        if (!_bindings.TryAdd(binding.EventId, binding) &&
            _bindings.TryGetValue(binding.EventId, out var existing) && existing != binding)
            throw new InvalidOperationException("The native Policy admission binding changed.");
        return Task.CompletedTask;
    }
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
/// Applies the native Policy profile to runtime model and tool callback boundaries.
/// </summary>
public sealed class RuntimeNativePolicyGate(
    IRuntimeNativePolicySource source,
    IRuntimeNativePolicyAdmissionStore admissionStore,
    NativeAcsPolicyEvaluator? evaluator)
{
    public const string UnavailableCode = "runtime_native_policy_unavailable";
    public const string DeniedCode = "runtime_native_policy_denied";
    public const string StaleCode = "runtime_native_policy_stale";
    public bool IsAvailable => evaluator is not null;

    public async Task<RuntimeNativePolicyPreparation> PrepareAuthorizeAsync(
        RuntimeRegistration registration,
        AuthorizedRunSelection accepted,
        RuntimeActionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(accepted);
        ArgumentNullException.ThrowIfNull(request);
        if (evaluator is null)
            throw new RuntimeAuthorizationException(UnavailableCode);
        var modelFacts = request.ActionId == "model.turn"
            ? ReadModelFacts(registration, accepted)
            : null;
        var revisions = await ReadAndValidateAsync(
            registration, accepted, cancellationToken).ConfigureAwait(false);
        RuntimeNativePolicyAdmissionBinding? invocation = null;
        if (request.ToolOwner?.Phase == "permission")
            invocation = await RequireInvocationBindingAsync(
                registration, request, revisions, cancellationToken).ConfigureAwait(false);
        foreach (var revision in revisions)
        {
            await EvaluateAsync(
                registration, request, revision, modelFacts, cancellationToken).ConfigureAwait(false);
            if (invocation?.Request is { } invocationRequest)
                await EvaluateAsync(
                    registration, invocationRequest, revision, null,
                    cancellationToken).ConfigureAwait(false);
        }

        var requiredChecks = request.ActionId == "model.turn"
            ? CombineRequiredChecks(revisions.Select(item => item.Guardrails.RequiredChecks))
            : [];
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
            currentSources,
            Request: request);
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
        var release = await PrepareAuthorizeAsync(
            registration, accepted, request, cancellationToken).ConfigureAwait(false);
        if (release.Binding.RequestHash != current.Binding.RequestHash ||
            release.Binding.RegistrationHash != current.Binding.RegistrationHash ||
            release.Binding.BindingHash != current.Binding.BindingHash ||
            !release.Binding.AcceptedSources.SequenceEqual(current.Binding.AcceptedSources) ||
            !release.Binding.CurrentSources.SequenceEqual(current.Binding.CurrentSources))
            throw new RuntimeAuthorizationException(StaleCode);
        return release;
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
            var result = await EvaluateComposedAsync(
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
        NativeAcsGuardrailsEvaluationFacts? guardrails = null,
        NativeAcsToolEvaluationFacts? tool = null) =>
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
            Guardrails = guardrails,
            Tool = tool
        };

    private static NativeAcsToolEvaluationFacts ReadToolFacts(RuntimeActionRequest request)
    {
        var owner = request.ToolOwner
            ?? throw new RuntimeAuthorizationException("runtime_action_tool_owner_required");
        RuntimeActionContract.Validate(request);
        return new(
            owner.ToolName,
            owner.CallId,
            owner.Phase,
            owner.ArgumentDigest,
            owner.SdkSessionId,
            owner.MessageId.ToString("D"),
            owner.TurnId,
            owner.ReadAccess,
            owner.WriteAccess,
            owner.ShellAccess,
            owner.PermissionKind,
            owner.InvocationArgumentDigest);
    }

    private async Task<RuntimeNativePolicyAdmissionBinding> RequireInvocationBindingAsync(
        RuntimeRegistration registration,
        RuntimeActionRequest request,
        ImmutableArray<RuntimeNativePolicyPreparedRevision> revisions,
        CancellationToken cancellationToken)
    {
        var owner = request.ToolOwner!;
        RuntimeNativePolicyAdmissionBinding? invocation;
        try
        {
            invocation = await admissionStore.ReadAsync(
                owner.InvocationEventId!.Value, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            throw new RuntimeAuthorizationException(UnavailableCode);
        }
        if (invocation is null ||
            invocation.EventId == request.EventId ||
            invocation.RequestHash != owner.InvocationRequestHash ||
            invocation.RegistrationHash != RuntimeContractValidation.RegistrationHash(registration) ||
            invocation.Request?.ToolOwner is not { } invocationOwner ||
            !string.Equals(invocationOwner.SdkSessionId, owner.SdkSessionId, StringComparison.Ordinal) ||
            !string.Equals(invocationOwner.ToolName, owner.ToolName, StringComparison.Ordinal) ||
            !string.Equals(invocationOwner.CallId, owner.CallId, StringComparison.Ordinal) ||
            invocationOwner.MessageId != owner.MessageId ||
            !string.Equals(invocationOwner.TurnId, owner.TurnId, StringComparison.Ordinal) ||
            !string.Equals(invocationOwner.ActionId, owner.ActionId, StringComparison.Ordinal) ||
            invocationOwner.ReadAccess != owner.ReadAccess ||
            invocationOwner.WriteAccess != owner.WriteAccess ||
            invocationOwner.ShellAccess != owner.ShellAccess ||
            !string.Equals(invocationOwner.ArgumentDigest, owner.InvocationArgumentDigest,
                StringComparison.Ordinal) ||
            invocationOwner.Phase != "invocation" ||
            !invocation.CurrentSources.SequenceEqual(
                revisions.Where(item => item.Current).Select(ToReference)))
            throw new RuntimeAuthorizationException(StaleCode);
        return invocation;

        static RuntimeNativePolicySourceReference ToReference(
            RuntimeNativePolicyPreparedRevision item) =>
            new(
                item.Source.ScopeType,
                item.Source.ScopeId,
                item.Source.PolicyId,
                item.Source.Revision,
                item.Source.ContentSha256);
    }

    private async Task EvaluateAsync(
        RuntimeRegistration registration,
        RuntimeActionRequest request,
        RuntimeNativePolicyPreparedRevision revision,
        NativeAcsModelUseEvaluationFacts? modelFacts,
        CancellationToken cancellationToken)
    {
        if (request.ActionId == "model.turn")
        {
            RequireAllowed(await EvaluateComposedAsync(
                revision.Source.Artifact,
                NativeAcsDomainId.ModelUse,
                InterventionPoint.PreModelCall,
                Snapshot(
                    registration,
                    request,
                    modelUse: modelFacts ??
                        throw new RuntimeAuthorizationException(UnavailableCode)),
                cancellationToken).ConfigureAwait(false));
            return;
        }
        var tool = ReadToolFacts(request);
        RequireAllowed(await EvaluateComposedAsync(
            revision.Source.Artifact,
            NativeAcsDomainId.Actions,
            InterventionPoint.PreModelCall,
            Snapshot(registration, request, tool: tool),
            cancellationToken).ConfigureAwait(false));
        RequireAllowed(await EvaluateComposedAsync(
            revision.Source.Artifact,
            NativeAcsDomainId.ToolPermissions,
            InterventionPoint.PreModelCall,
            Snapshot(registration, request, tool: tool),
            cancellationToken).ConfigureAwait(false));
    }

    private Task<NativeAcsEvaluationResult> EvaluateComposedAsync(
        NativeAcsArtifact artifact,
        NativeAcsDomainId domain,
        InterventionPoint point,
        NativeAcsEvaluationSnapshot snapshot,
        CancellationToken cancellationToken) =>
        evaluator?.EvaluateComposedAsync(artifact, domain, point, snapshot, cancellationToken)
        ?? throw new RuntimeAuthorizationException(UnavailableCode);

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
