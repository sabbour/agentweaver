using System.Security.Cryptography;
using System.Text.Json;
using Agentweaver.Abstractions;

namespace Agentweaver.Orchestrator.Core;

/// <summary>Exact plan item and parent authority that a child-spawn intent asks the Orchestrator to admit.</summary>
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
    WorkPlanItem WorkItem);

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
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (intent.IntentId == Guid.Empty ||
            !AgtPolicyProvider.IsIdentifier(intent.ParentSessionId) ||
            !AgtPolicyProvider.IsIdentifier(intent.RootSessionId) ||
            !AgtPolicyProvider.IsIdentifier(intent.PlanId) ||
            intent.PlanRevision < 1 ||
            !AgtPolicyProvider.IsIdentifier(intent.WorkflowStepId) ||
            !AgtPolicyProvider.IsIdentifier(intent.WorkItemId) ||
            !AgtPolicyProvider.IsIdentifier(intent.ContextRevision) ||
            intent.ExecutionFence < 1 ||
            intent.WorkItem is null ||
            intent.WorkItem.Id != intent.WorkItemId ||
            intent.WorkItem.WorkflowStepId != intent.WorkflowStepId ||
            intent.WorkItem.DependsOn.IsDefault ||
            intent.WorkItem.DeclaredOutputs.IsDefault)
            throw new ArgumentException("The child-spawn intent is invalid.", nameof(intent));
    }

    /// <summary>Hashes the intent with an explicit field order so retries use the same identity across callers.</summary>
    public static string ComputeIntentHash(MafChildSpawnIntent intent)
    {
        ValidateIntent(intent);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            // Field order is part of this contract; keep it explicit rather than relying on serializer defaults.
            writer.WriteStartObject();
            writer.WriteNumber("version", 1);
            writer.WriteString("intentId", intent.IntentId.ToString("N"));
            writer.WriteString("parentSessionId", intent.ParentSessionId);
            writer.WriteString("rootSessionId", intent.RootSessionId);
            writer.WriteString("planId", intent.PlanId);
            writer.WriteNumber("planRevision", intent.PlanRevision);
            writer.WriteString("workflowStepId", intent.WorkflowStepId);
            writer.WriteString("workItemId", intent.WorkItemId);
            writer.WriteString("contextRevision", intent.ContextRevision);
            writer.WriteNumber("executionFence", intent.ExecutionFence);
            WriteWorkItem(writer, intent.WorkItem);
            writer.WriteEndObject();
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    /// <summary>Creates immutable evidence only for a complete policy allow decision.</summary>
    public static MafChildSpawnAuthorizationEvidence CreateEvidence(
        MafChildSpawnIntent intent,
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
            intent.WorkItemId,
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
        MafChildSpawnIntent intent,
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
            evidence.WorkItemId == intent.WorkItemId &&
            evidence.PolicyProviderId == authority.PolicyProviderId &&
            evidence.PolicyAdapterVersion == authority.PolicyAdapterVersion &&
            evidence.PolicyOptionsSchemaVersion == authority.PolicyOptionsSchemaVersion &&
            evidence.PolicyOptionsRevision == authority.PolicyOptionsRevision &&
            evidence.PolicyReceiptId != Guid.Empty &&
            evidence.Outcome == PolicyEvaluationOutcome.Allow &&
            evidence.ReasonCode == PolicyEvaluationReasonCode.Allowed;
    }

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
