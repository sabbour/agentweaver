using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentweaver.Api.Sandbox;

public sealed record CurrentSandboxBindingResult
{
    [JsonPropertyName("state")]
    public required string State { get; init; }
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
    [JsonPropertyName("provisioner")]
    public string? Provisioner { get; init; }
    [JsonPropertyName("run_id")]
    public string? RunId { get; init; }
    [JsonPropertyName("claim_name")]
    public string? ClaimName { get; init; }
    [JsonPropertyName("claim_uid")]
    public string? ClaimUid { get; init; }
    [JsonPropertyName("pod_name")]
    public string? PodName { get; init; }
    [JsonPropertyName("pod_uid")]
    public string? PodUid { get; init; }
    [JsonPropertyName("namespace")]
    public string? Namespace { get; init; }
    [JsonPropertyName("lifecycle_generation")]
    public int? LifecycleGeneration { get; init; }
    [JsonPropertyName("assembly_attempt")]
    public string? AssemblyAttempt { get; init; }
    [JsonPropertyName("source_repository")]
    public string? SourceRepository { get; init; }
    [JsonPropertyName("source_ref")]
    public string? SourceRef { get; init; }
    [JsonPropertyName("source_base_commit")]
    public string? SourceBaseCommit { get; init; }
    [JsonPropertyName("source_tree")]
    public string? SourceTree { get; init; }
    [JsonPropertyName("source_worktree")]
    public string? SourceWorktree { get; init; }

    public static CurrentSandboxBindingResult Unavailable(string reason) =>
        new() { State = "unavailable", Reason = reason };
    public static CurrentSandboxBindingResult Conflict(string reason) =>
        new() { State = "conflict", Reason = reason };
}

public sealed record CurrentSandboxAttestation(
    string RunId,
    string ClaimName,
    string ClaimUid,
    string ClaimResourceVersion,
    string SandboxUid,
    string PodName,
    string PodUid,
    string Namespace,
    int LifecycleGeneration,
    string AssemblyAttempt,
    string SourceRepository,
    string SourceRef,
    string SourceBaseCommit,
    string SourceTree,
    string SourceWorktree);

public static class CurrentSandboxBindingVerifier
{
    public const string EventType = "sandbox.execution_pod.configured";
    public const string ClaimUidLabel = "agents.x-k8s.io/claim-uid";
    public const string SourceRepositoryAnnotation = "agentweaver.io/source-repository";
    public const string SourceRefAnnotation = "agentweaver.io/source-ref";
    public const string SourceBaseCommitAnnotation = "agentweaver.io/source-base-commit";
    public const string SourceTreeAnnotation = "agentweaver.io/source-tree";

    public static JsonDocument PodDocument(k8s.Models.V1Pod pod) =>
        JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            metadata = new
            {
                name = pod.Metadata?.Name,
                @namespace = pod.Metadata?.NamespaceProperty,
                uid = pod.Metadata?.Uid,
                deletionTimestamp = pod.Metadata?.DeletionTimestamp,
                labels = pod.Metadata?.Labels,
                ownerReferences = pod.Metadata?.OwnerReferences?.Select(owner => new
                {
                    kind = owner.Kind,
                    name = owner.Name,
                    uid = owner.Uid,
                    controller = owner.Controller,
                }),
            },
            status = new
            {
                phase = pod.Status?.Phase,
                conditions = pod.Status?.Conditions?.Select(condition => new
                {
                    type = condition.Type,
                    status = condition.Status,
                }),
            },
        }));

    public static CurrentSandboxBindingResult Verify(
        string runId, string ns, int generation, string? currentTree,
        long? leaseToken, int leaseGeneration, string? boundPod,
        string? attestedClaimUid, string? attestedSandboxUid, string? attestedPodUid,
        string? assemblyAttempt, string? sourceRepository, string? sourceRef,
        string? sourceBaseCommit, string? sourceWorktree,
        JsonElement claim, JsonElement sandbox, JsonElement pod)
    {
        if (leaseToken is null || leaseGeneration != generation)
            return CurrentSandboxBindingResult.Unavailable("active_lease_missing");
        if (string.IsNullOrWhiteSpace(boundPod) || string.IsNullOrWhiteSpace(attestedClaimUid)
            || string.IsNullOrWhiteSpace(attestedSandboxUid) || string.IsNullOrWhiteSpace(attestedPodUid)
            || string.IsNullOrWhiteSpace(currentTree) || string.IsNullOrWhiteSpace(sourceRepository)
            || string.IsNullOrWhiteSpace(sourceRef) || string.IsNullOrWhiteSpace(sourceBaseCommit)
            || string.IsNullOrWhiteSpace(sourceWorktree) || string.IsNullOrWhiteSpace(assemblyAttempt))
            return CurrentSandboxBindingResult.Unavailable("attestation_incomplete");
        var token = leaseToken.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (assemblyAttempt != token)
            return CurrentSandboxBindingResult.Conflict("lease_attempt_mismatch");
        var claimName = SandboxClaimConventions.DeriveAgentHostClaimName(runId);
        var claimMeta = Property(claim, "metadata");
        var sandboxMeta = Property(sandbox, "metadata");
        var podMeta = Property(pod, "metadata");
        if (HasDeletionTimestamp(claimMeta) || HasDeletionTimestamp(sandboxMeta)
            || HasDeletionTimestamp(podMeta))
            return CurrentSandboxBindingResult.Unavailable("binding_resource_deleting");
        if (SandboxClaimConventions.GetPhase(claim) != "Bound")
            return CurrentSandboxBindingResult.Unavailable("claim_not_ready");
        var podStatus = Property(pod, "status");
        var podConditions = Property(podStatus, "conditions");
        if (Read(podStatus, "phase") != "Running"
            || podConditions.ValueKind != JsonValueKind.Array
            || !podConditions.EnumerateArray().Any(condition =>
                Read(condition, "type") == "Ready" && Read(condition, "status") == "True"))
            return CurrentSandboxBindingResult.Unavailable("pod_not_ready");
        if (Read(claimMeta, "name") != claimName || Read(claimMeta, "namespace") != ns
            || Read(claimMeta, "uid") != attestedClaimUid
            || SandboxClaimConventions.TryGetBoundPodName(claim) != boundPod)
            return CurrentSandboxBindingResult.Conflict("claim_identity_changed");

        var annotations = Property(claimMeta, "annotations");
        if (Read(annotations, SandboxClaimConventions.RunIdAnnotation) != runId
            || Read(annotations, KubernetesSandboxExecutor.LifecycleGenerationAnnotation)
                != generation.ToString(System.Globalization.CultureInfo.InvariantCulture)
            || Read(annotations, KubernetesSandboxExecutor.HolderTokenAnnotation) != token
            || Read(annotations, SourceRepositoryAnnotation) != sourceRepository
            || Read(annotations, SourceRefAnnotation) != sourceRef
            || Read(annotations, SourceBaseCommitAnnotation) != sourceBaseCommit
            || Read(annotations, SourceTreeAnnotation) != currentTree
            || Read(annotations, "agentweaver.io/working-directory") != sourceWorktree)
            return CurrentSandboxBindingResult.Conflict("claim_source_or_owner_mismatch");

        if (Read(sandboxMeta, "name") != boundPod || Read(podMeta, "name") != boundPod
            || Read(sandboxMeta, "namespace") != ns || Read(podMeta, "namespace") != ns
            || Read(sandboxMeta, "uid") != attestedSandboxUid
            || Read(podMeta, "uid") != attestedPodUid
            || Read(Property(sandboxMeta, "labels"), ClaimUidLabel) != attestedClaimUid
            || !OwnedBy(sandboxMeta, "SandboxClaim", claimName, attestedClaimUid)
            || !OwnedBy(podMeta, "Sandbox", boundPod, attestedSandboxUid))
            return CurrentSandboxBindingResult.Conflict("pod_owner_or_uid_mismatch");

        return new CurrentSandboxBindingResult
        {
            State = "verified",
            RunId = runId,
            Provisioner = "kubernetes-sandbox-claim",
            ClaimName = claimName,
            ClaimUid = attestedClaimUid,
            PodName = boundPod,
            PodUid = attestedPodUid,
            Namespace = ns,
            LifecycleGeneration = generation,
            AssemblyAttempt = assemblyAttempt,
            SourceRepository = sourceRepository,
            SourceRef = sourceRef,
            SourceBaseCommit = sourceBaseCommit,
            SourceTree = currentTree,
            SourceWorktree = sourceWorktree,
        };
    }

    private static JsonElement Property(JsonElement parent, string key) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(key, out var child)
            ? child : default;

    private static string? Read(JsonElement parent, string key)
    {
        var value = Property(parent, key);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static bool HasDeletionTimestamp(JsonElement metadata)
    {
        var value = Property(metadata, "deletionTimestamp");
        return value.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null);
    }

    private static bool OwnedBy(JsonElement metadata, string kind, string name, string uid)
    {
        var references = Property(metadata, "ownerReferences");
        return references.ValueKind == JsonValueKind.Array && references.EnumerateArray().Any(reference =>
            Read(reference, "kind") == kind && Read(reference, "name") == name
            && Read(reference, "uid") == uid
            && Property(reference, "controller").ValueKind == JsonValueKind.True);
    }
}
