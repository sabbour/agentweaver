using System.Security.Cryptography;
using System.Text.Json;
using Agentweaver.Abstractions;

namespace Agentweaver.Orchestrator.Core;

/// <summary>Shared run, session, plan, and fence binding for child-spawn intent variants.</summary>
public abstract record MafChildSpawnAuthorizationIntent(
    Guid IntentId,
    string ParentSessionId,
    string RootSessionId,
    string PlanId,
    long PlanRevision,
    string WorkflowStepId,
    string ContextRevision,
    long ExecutionFence);

/// <summary>Exact open-step work item that a child-spawn intent asks the Orchestrator to admit.</summary>
public sealed record MafChildSpawnIntent(
    Guid IntentId,
    string ParentSessionId,
    string RootSessionId,
    string PlanId,
    long PlanRevision,
    string WorkflowStepId,
    string WorkItemId,
    string ContextRevision,
    long ExecutionFence,
    WorkPlanItem WorkItem)
    : MafChildSpawnAuthorizationIntent(
        IntentId, ParentSessionId, RootSessionId, PlanId, PlanRevision,
        WorkflowStepId, ContextRevision, ExecutionFence);

/// <summary>The selected agent, model, and pinned isolation binding for one fixed workflow step.</summary>
public sealed record MafChildSpawnRoleBinding(
    string RoleId,
    string AgentId,
    string ModelSelectionReference,
    string IsolationProviderId,
    string IsolationChoice);

/// <summary>
/// Fixed-work child-spawn intent bound to a confirmed workflow step and its accepted role selection.
/// </summary>
public sealed record MafChildSpawnFixedWorkIntent(
    Guid IntentId,
    string ParentSessionId,
    string RootSessionId,
    string PlanId,
    long PlanRevision,
    string WorkflowStepId,
    string ContextRevision,
    long ExecutionFence,
    string WorkflowDefinitionId,
    string WorkflowDefinitionRevision,
    string FixedWorkSpecificationHash,
    MafChildSpawnRoleBinding RoleBinding)
    : MafChildSpawnAuthorizationIntent(
        IntentId, ParentSessionId, RootSessionId, PlanId, PlanRevision,
        WorkflowStepId, ContextRevision, ExecutionFence);

/// <summary>Current authenticated and selected authority used to evaluate a child-spawn intent.</summary>
public sealed record MafChildSpawnAuthorizationAuthority(
    string ProjectId,
    string RunId,
    string ActorIssuer,
    string ActorId,
    string TenantId,
    string AcceptedSelectionHash,
    string ContextRevision,
    long ProjectRevision,
    long ProjectConfigurationRevision,
    long PlatformRuntimeRevision,
    long MembershipRevision,
    long RoleRevision,
    string PolicyProviderId,
    string PolicyAdapterVersion,
    int PolicyOptionsSchemaVersion,
    string PolicyOptionsRevision);

/// <summary>Durable allow evidence bound to one exact child-spawn intent and policy revision.</summary>
public sealed record MafChildSpawnAuthorizationEvidence(
    Guid IntentId,
    string IntentHash,
    string ProjectId,
    string RunId,
    string ParentSessionId,
    string RootSessionId,
    string ActorIssuer,
    string ActorId,
    string TenantId,
    string AcceptedSelectionHash,
    string ContextRevision,
    long ProjectRevision,
    long ProjectConfigurationRevision,
    long PlatformRuntimeRevision,
    long MembershipRevision,
    long RoleRevision,
    long ExecutionFence,
    string PlanId,
    long PlanRevision,
    string WorkflowStepId,
    string WorkItemId,
    Guid PolicyReceiptId,
    string PolicyProviderId,
    string PolicyAdapterVersion,
    int PolicyOptionsSchemaVersion,
    string PolicyOptionsRevision,
    PolicyEvaluationOutcome Outcome,
    PolicyEvaluationReasonCode ReasonCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ConsumedAt = null);

/// <summary>Allow evidence returned by the owner-local consumer and whether another transaction consumed it first.</summary>
public sealed record MafChildSpawnAuthorizationConsumeResult(
    MafChildSpawnAuthorizationEvidence Evidence,
    bool AlreadyConsumed);

/// <summary>Pure validation and binding rules shared by the authorizer and its future owner-local consumer.</summary>
public static class MafChildSpawnAuthorizationContract
{
    public const string ActionId = "maf.child.spawn";
    public const string Purpose = "maf.child.spawn";

    /// <summary>Validates the immutable spawn intent before it is hashed or authorized.</summary>
    public static void ValidateIntent(MafChildSpawnIntent intent)
        => ValidateIntent((MafChildSpawnAuthorizationIntent)intent);

    /// <summary>Validates the immutable intent before it is hashed or authorized.</summary>
    public static void ValidateIntent(MafChildSpawnAuthorizationIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (intent.IntentId == Guid.Empty ||
            !AgtPolicyProvider.IsIdentifier(intent.ParentSessionId) ||
            !AgtPolicyProvider.IsIdentifier(intent.RootSessionId) ||
            !AgtPolicyProvider.IsIdentifier(intent.PlanId) ||
            intent.PlanRevision < 1 ||
            !AgtPolicyProvider.IsIdentifier(intent.WorkflowStepId) ||
            !AgtPolicyProvider.IsIdentifier(intent.ContextRevision) ||
            intent.ExecutionFence < 1)
            throw new ArgumentException("The child-spawn intent is invalid.", nameof(intent));

        switch (intent)
        {
            case MafChildSpawnIntent open:
                if (!AgtPolicyProvider.IsIdentifier(open.WorkItemId) ||
                    open.WorkItem is null ||
                    open.WorkItem.Id != open.WorkItemId ||
                    open.WorkItem.WorkflowStepId != open.WorkflowStepId ||
                    open.WorkItem.DependsOn.IsDefault ||
                    open.WorkItem.DeclaredOutputs.IsDefault)
                    throw new ArgumentException("The child-spawn intent is invalid.", nameof(intent));
                break;
            case MafChildSpawnFixedWorkIntent fixedWork:
                if (!AgtPolicyProvider.IsIdentifier(fixedWork.WorkflowDefinitionId) ||
                    !WorkflowValidationSupport.IsOpaqueReference(fixedWork.WorkflowDefinitionRevision) ||
                    !IsCanonicalHash(fixedWork.FixedWorkSpecificationHash) ||
                    fixedWork.RoleBinding is null ||
                    !AgtPolicyProvider.IsIdentifier(fixedWork.RoleBinding.RoleId) ||
                    !WorkflowValidationSupport.IsOpaqueReference(fixedWork.RoleBinding.AgentId) ||
                    !WorkflowValidationSupport.IsOpaqueReference(fixedWork.RoleBinding.ModelSelectionReference) ||
                    !WorkflowValidationSupport.IsOpaqueReference(fixedWork.RoleBinding.IsolationProviderId) ||
                    !AgtPolicyProvider.IsIdentifier(fixedWork.RoleBinding.IsolationChoice))
                    throw new ArgumentException("The fixed-work child-spawn intent is invalid.", nameof(intent));
                break;
            default:
                throw new ArgumentException("The child-spawn intent variant is unsupported.", nameof(intent));
        }
    }

    /// <summary>Hashes the intent with an explicit field order so retries use the same identity across callers.</summary>
    public static string ComputeIntentHash(MafChildSpawnIntent intent)
        => ComputeIntentHash((MafChildSpawnAuthorizationIntent)intent);

    /// <summary>Hashes either supported intent variant with a stable, explicit field order.</summary>
    public static string ComputeIntentHash(MafChildSpawnAuthorizationIntent intent)
    {
        ValidateIntent(intent);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            switch (intent)
            {
                case MafChildSpawnIntent open:
                    // Preserve version 1 bytes for existing open-work authorization receipts.
                    writer.WriteStartObject();
                    writer.WriteNumber("version", 1);
                    writer.WriteString("intentId", open.IntentId.ToString("N"));
                    writer.WriteString("parentSessionId", open.ParentSessionId);
                    writer.WriteString("rootSessionId", open.RootSessionId);
                    writer.WriteString("planId", open.PlanId);
                    writer.WriteNumber("planRevision", open.PlanRevision);
                    writer.WriteString("workflowStepId", open.WorkflowStepId);
                    writer.WriteString("workItemId", open.WorkItemId);
                    writer.WriteString("contextRevision", open.ContextRevision);
                    writer.WriteNumber("executionFence", open.ExecutionFence);
                    WriteWorkItem(writer, open.WorkItem);
                    writer.WriteEndObject();
                    break;
                case MafChildSpawnFixedWorkIntent fixedWork:
                    // Field order and the distinct variant version are part of the durable receipt identity.
                    writer.WriteStartObject();
                    writer.WriteNumber("version", 2);
                    writer.WriteString("intentKind", "fixedWork");
                    writer.WriteString("intentId", fixedWork.IntentId.ToString("N"));
                    writer.WriteString("parentSessionId", fixedWork.ParentSessionId);
                    writer.WriteString("rootSessionId", fixedWork.RootSessionId);
                    writer.WriteString("planId", fixedWork.PlanId);
                    writer.WriteNumber("planRevision", fixedWork.PlanRevision);
                    writer.WriteString("workflowDefinitionId", fixedWork.WorkflowDefinitionId);
                    writer.WriteString("workflowDefinitionRevision", fixedWork.WorkflowDefinitionRevision);
                    writer.WriteString("workflowStepId", fixedWork.WorkflowStepId);
                    writer.WriteString("fixedWorkSpecificationHash", fixedWork.FixedWorkSpecificationHash);
                    writer.WriteString("contextRevision", fixedWork.ContextRevision);
                    writer.WriteNumber("executionFence", fixedWork.ExecutionFence);
                    writer.WriteStartObject("roleBinding");
                    writer.WriteString("roleId", fixedWork.RoleBinding.RoleId);
                    writer.WriteString("agentId", fixedWork.RoleBinding.AgentId);
                    writer.WriteString("modelSelectionReference", fixedWork.RoleBinding.ModelSelectionReference);
                    writer.WriteString("isolationProviderId", fixedWork.RoleBinding.IsolationProviderId);
                    writer.WriteString("isolationChoice", fixedWork.RoleBinding.IsolationChoice);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                    break;
                default:
                    throw new ArgumentException("The child-spawn intent variant is unsupported.", nameof(intent));
            }
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    /// <summary>Hashes the canonical fixed-work fields in their declared order.</summary>
    public static string ComputeFixedWorkSpecificationHash(FixedWorkSpecification specification)
    {
        ArgumentNullException.ThrowIfNull(specification);
        if (string.IsNullOrWhiteSpace(specification.Title) ||
            string.IsNullOrWhiteSpace(specification.Task) ||
            !AgtPolicyProvider.IsIdentifier(specification.RoleId) ||
            !AgtPolicyProvider.IsIdentifier(specification.Phase) ||
            !AgtPolicyProvider.IsIdentifier(specification.IsolationChoice) ||
            specification.DeclaredOutputs.IsDefault)
            throw new ArgumentException("The fixed-work specification is invalid.", nameof(specification));

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            // Explicit ordering prevents serializer settings from changing a confirmed specification's identity.
            writer.WriteStartObject();
            writer.WriteNumber("version", 1);
            writer.WriteString("title", specification.Title);
            writer.WriteString("task", specification.Task);
            writer.WriteString("roleId", specification.RoleId);
            writer.WriteString("phase", specification.Phase);
            writer.WriteString("isolationChoice", specification.IsolationChoice);
            WriteStrings(writer, "declaredOutputs", specification.DeclaredOutputs);
            writer.WriteEndObject();
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    /// <summary>Creates immutable evidence only for a complete policy allow decision.</summary>
    public static MafChildSpawnAuthorizationEvidence CreateEvidence(
        MafChildSpawnAuthorizationIntent intent,
        MafChildSpawnAuthorizationAuthority authority,
        Guid policyReceiptId,
        PolicyEvaluationOutcome outcome,
        PolicyEvaluationReasonCode reasonCode,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ValidateIntent(intent);
        ValidateAuthority(authority);
        if (policyReceiptId == Guid.Empty ||
            outcome != PolicyEvaluationOutcome.Allow ||
            reasonCode != PolicyEvaluationReasonCode.Allowed)
            throw new ArgumentException("Only a recorded policy allow can authorize child work.");

        return new MafChildSpawnAuthorizationEvidence(
            intent.IntentId,
            ComputeIntentHash(intent),
            authority.ProjectId,
            authority.RunId,
            intent.ParentSessionId,
            intent.RootSessionId,
            authority.ActorIssuer,
            authority.ActorId,
            authority.TenantId,
            authority.AcceptedSelectionHash,
            authority.ContextRevision,
            authority.ProjectRevision,
            authority.ProjectConfigurationRevision,
            authority.PlatformRuntimeRevision,
            authority.MembershipRevision,
            authority.RoleRevision,
            intent.ExecutionFence,
            intent.PlanId,
            intent.PlanRevision,
            intent.WorkflowStepId,
            ReceiptWorkItemId(intent),
            policyReceiptId,
            authority.PolicyProviderId,
            authority.PolicyAdapterVersion,
            authority.PolicyOptionsSchemaVersion,
            authority.PolicyOptionsRevision,
            outcome,
            reasonCode,
            createdAt);
    }

    /// <summary>Returns true only when the durable allow still matches the exact intent and all current authority.</summary>
    public static bool MatchesCurrent(
        MafChildSpawnAuthorizationEvidence evidence,
        MafChildSpawnAuthorizationIntent intent,
        MafChildSpawnAuthorizationAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(authority);
        ValidateIntent(intent);
        ValidateAuthority(authority);

        return evidence.IntentId == intent.IntentId &&
            evidence.IntentHash == ComputeIntentHash(intent) &&
            evidence.ProjectId == authority.ProjectId &&
            evidence.RunId == authority.RunId &&
            evidence.ParentSessionId == intent.ParentSessionId &&
            evidence.RootSessionId == intent.RootSessionId &&
            evidence.ActorIssuer == authority.ActorIssuer &&
            evidence.ActorId == authority.ActorId &&
            evidence.TenantId == authority.TenantId &&
            evidence.AcceptedSelectionHash == authority.AcceptedSelectionHash &&
            evidence.ContextRevision == authority.ContextRevision &&
            evidence.ContextRevision == intent.ContextRevision &&
            evidence.ProjectRevision == authority.ProjectRevision &&
            evidence.ProjectConfigurationRevision == authority.ProjectConfigurationRevision &&
            evidence.PlatformRuntimeRevision == authority.PlatformRuntimeRevision &&
            evidence.MembershipRevision == authority.MembershipRevision &&
            evidence.RoleRevision == authority.RoleRevision &&
            evidence.ExecutionFence == intent.ExecutionFence &&
            evidence.PlanId == intent.PlanId &&
            evidence.PlanRevision == intent.PlanRevision &&
            evidence.WorkflowStepId == intent.WorkflowStepId &&
            evidence.WorkItemId == ReceiptWorkItemId(intent) &&
            evidence.PolicyProviderId == authority.PolicyProviderId &&
            evidence.PolicyAdapterVersion == authority.PolicyAdapterVersion &&
            evidence.PolicyOptionsSchemaVersion == authority.PolicyOptionsSchemaVersion &&
            evidence.PolicyOptionsRevision == authority.PolicyOptionsRevision &&
            evidence.PolicyReceiptId != Guid.Empty &&
            evidence.Outcome == PolicyEvaluationOutcome.Allow &&
            evidence.ReasonCode == PolicyEvaluationReasonCode.Allowed;
    }

    /// <summary>Returns the existing receipt column's stable item key without creating fixed work as a plan item.</summary>
    public static string ReceiptWorkItemId(MafChildSpawnAuthorizationIntent intent) =>
        intent switch
        {
            MafChildSpawnIntent open => open.WorkItemId,
            MafChildSpawnFixedWorkIntent fixedWork => fixedWork.WorkflowStepId,
            _ => throw new ArgumentException("The child-spawn intent variant is unsupported.", nameof(intent))
        };

    private static bool IsCanonicalHash(string? value) =>
        value is { Length: 64 } &&
        value.All(Uri.IsHexDigit) &&
        string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal);

    private static void ValidateAuthority(MafChildSpawnAuthorizationAuthority authority)
    {
        if (!AgtPolicyProvider.IsIdentifier(authority.ProjectId) ||
            !AgtPolicyProvider.IsIdentifier(authority.RunId) ||
            string.IsNullOrWhiteSpace(authority.ActorIssuer) || authority.ActorIssuer.Length > 512 ||
            !AgtPolicyProvider.IsIdentifier(authority.ActorId) ||
            !AgtPolicyProvider.IsIdentifier(authority.TenantId) ||
            authority.AcceptedSelectionHash.Length != 64 ||
            !authority.AcceptedSelectionHash.All(Uri.IsHexDigit) ||
            !AgtPolicyProvider.IsIdentifier(authority.ContextRevision) ||
            authority.ProjectRevision < 1 ||
            authority.ProjectConfigurationRevision < 1 ||
            authority.PlatformRuntimeRevision < 1 ||
            authority.MembershipRevision < 1 ||
            authority.RoleRevision < 1 ||
            !AgtPolicyProvider.IsIdentifier(authority.PolicyProviderId) ||
            string.IsNullOrWhiteSpace(authority.PolicyAdapterVersion) ||
            authority.PolicyAdapterVersion.Length > 64 ||
            authority.PolicyOptionsSchemaVersion < 1 ||
            !AgtPolicyProvider.IsIdentifier(authority.PolicyOptionsRevision))
            throw new ArgumentException("The child-spawn authorization authority is invalid.", nameof(authority));
    }

    private static void WriteWorkItem(Utf8JsonWriter writer, WorkPlanItem item)
    {
        writer.WriteStartObject("workItem");
        writer.WriteString("id", item.Id);
        writer.WriteString("workflowStepId", item.WorkflowStepId);
        writer.WriteString("title", item.Title);
        writer.WriteString("task", item.Task);
        writer.WriteString("roleId", item.RoleId);
        writer.WriteString("agentId", item.AgentId);
        writer.WriteString("phase", item.Phase);
        writer.WriteString("modelSelectionReference", item.ModelSelectionReference);
        writer.WriteString("isolationProviderId", item.IsolationProviderId);
        writer.WriteString("isolationChoice", item.IsolationChoice);
        WriteStrings(writer, "dependsOn", item.DependsOn);
        WriteStrings(writer, "declaredOutputs", item.DeclaredOutputs);
        writer.WriteEndObject();
    }

    private static void WriteStrings(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values)
            writer.WriteStringValue(value);
        writer.WriteEndArray();
    }
}
