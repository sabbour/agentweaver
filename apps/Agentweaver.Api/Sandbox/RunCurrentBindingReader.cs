using System.Text.Json;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;
using k8s;
using k8s.Autorest;
using LibGit2Sharp;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Api.Sandbox;

public static class RunCurrentBindingReader
{
    public static async Task<CurrentSandboxBindingResult> ReadAsync(
        Run run, IRunStore runs, IRunLeaseStore? leases, MemoryDbContext db,
        IKubernetes? client, string? ns, CancellationToken ct)
    {
        if (client is null || string.IsNullOrWhiteSpace(ns))
            return CurrentSandboxBindingResult.Unavailable("kubernetes_unavailable");
        if (leases is null)
            return CurrentSandboxBindingResult.Unavailable("lease_store_unavailable");
        var runId = run.Id.ToString();
        var lease = await leases.GetActiveClaimAsync(runId, ct).ConfigureAwait(false);
        if (lease is null || lease.LifecycleGeneration != run.LifecycleGeneration)
            return CurrentSandboxBindingResult.Unavailable("active_lease_missing");
        var events = await LatestAsync(db, runId, ct).ConfigureAwait(false);
        if (events.BoundType != RunEventExecutionPodNameStore.EventType)
            return CurrentSandboxBindingResult.Unavailable("execution_pod_unbound");
        if (events.AttestationJson is null
            || !AttestationFollowsCurrentBinding(events.BoundSequence, events.AttestationSequence))
            return CurrentSandboxBindingResult.Unavailable("postconfigure_attestation_missing");

        CurrentSandboxAttestation? attestation;
        try
        {
            attestation = JsonSerializer.Deserialize<CurrentSandboxAttestation>(
                events.AttestationJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return CurrentSandboxBindingResult.Unavailable("postconfigure_attestation_malformed");
        }
        using var boundPayload = JsonDocument.Parse(events.BoundJson ?? "{}");
        if (attestation is null || attestation.RunId != runId || attestation.Namespace != ns
            || attestation.LifecycleGeneration != run.LifecycleGeneration
            || attestation.ClaimName != SandboxClaimConventions.DeriveAgentHostClaimName(runId)
            || RunEventExecutionPodNameStore.ReadPodName(boundPayload.RootElement) != attestation.PodName)
            return CurrentSandboxBindingResult.Conflict("attestation_or_binding_changed");

        if (run.CurrentOutputRevisionId is null || run.TreeHash is null)
            return CurrentSandboxBindingResult.Unavailable("current_revision_missing");
        var revision = await runs.GetOutputRevisionAsync(run.Id, run.CurrentOutputRevisionId, ct)
            .ConfigureAwait(false);
        if (revision is null || revision.LifecycleGeneration != run.LifecycleGeneration
            || revision.TreeHash != run.TreeHash || attestation.SourceTree != revision.TreeHash)
            return CurrentSandboxBindingResult.Conflict("source_revision_mismatch");

        try
        {
            if (!SourceStillCurrent(attestation, revision.TreeHash))
                return CurrentSandboxBindingResult.Conflict("source_commit_or_worktree_mismatch");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CurrentSandboxBindingResult.Unavailable("source_repository_unavailable");
        }

        JsonDocument claim;
        JsonDocument sandbox;
        JsonDocument pod;
        try
        {
            claim = JsonDocument.Parse(JsonSerializer.Serialize(
                await client.CustomObjects.GetNamespacedCustomObjectAsync(
                SandboxClaimConventions.ApiGroup, SandboxClaimConventions.ApiVersion, ns,
                SandboxClaimConventions.ClaimPlural, attestation.ClaimName,
                cancellationToken: ct).ConfigureAwait(false)));
            sandbox = JsonDocument.Parse(JsonSerializer.Serialize(
                await client.CustomObjects.GetNamespacedCustomObjectAsync(
                "agents.x-k8s.io", SandboxClaimConventions.ApiVersion, ns, "sandboxes",
                attestation.PodName, cancellationToken: ct).ConfigureAwait(false)));
            pod = CurrentSandboxBindingVerifier.PodDocument(
                await client.CoreV1.ReadNamespacedPodAsync(attestation.PodName, ns, cancellationToken: ct)
                    .ConfigureAwait(false));
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return CurrentSandboxBindingResult.Unavailable("claim_sandbox_or_pod_missing");
        }
        using (claim)
        using (sandbox)
        using (pod)
        {
        var result = CurrentSandboxBindingVerifier.Verify(
            runId, ns, run.LifecycleGeneration, revision.TreeHash,
            lease.FencingToken, lease.LifecycleGeneration, attestation.PodName,
            attestation.ClaimUid, attestation.SandboxUid, attestation.PodUid,
            attestation.AssemblyAttempt, attestation.SourceRepository, attestation.SourceRef,
            attestation.SourceBaseCommit, attestation.SourceWorktree,
            claim.RootElement, sandbox.RootElement, pod.RootElement);
        if (result.State != "verified")
            return result;

        var reread = await LatestAsync(db, runId, ct).ConfigureAwait(false);
        var newLease = await leases.GetActiveClaimAsync(runId, ct).ConfigureAwait(false);
        var newRun = await runs.GetAsync(run.Id, ct).ConfigureAwait(false);
        JsonDocument newClaim;
        JsonDocument newSandbox;
        JsonDocument newPod;
        try
        {
            newClaim = JsonDocument.Parse(JsonSerializer.Serialize(
            await client.CustomObjects.GetNamespacedCustomObjectAsync(
                SandboxClaimConventions.ApiGroup, SandboxClaimConventions.ApiVersion, ns,
                SandboxClaimConventions.ClaimPlural, attestation.ClaimName,
                cancellationToken: ct).ConfigureAwait(false)));
            newSandbox = JsonDocument.Parse(JsonSerializer.Serialize(
            await client.CustomObjects.GetNamespacedCustomObjectAsync(
                "agents.x-k8s.io", SandboxClaimConventions.ApiVersion, ns, "sandboxes",
                attestation.PodName, cancellationToken: ct).ConfigureAwait(false)));
            newPod = CurrentSandboxBindingVerifier.PodDocument(
            await client.CoreV1.ReadNamespacedPodAsync(attestation.PodName, ns, cancellationToken: ct)
                .ConfigureAwait(false));
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return CurrentSandboxBindingResult.Conflict("binding_deleted_during_read");
        }
        using (newClaim)
        using (newSandbox)
        using (newPod)
        {
        var newProof = CurrentSandboxBindingVerifier.Verify(
            runId, ns, run.LifecycleGeneration, revision.TreeHash,
            newLease?.FencingToken, newLease?.LifecycleGeneration ?? 0, attestation.PodName,
            attestation.ClaimUid, attestation.SandboxUid, attestation.PodUid,
            attestation.AssemblyAttempt, attestation.SourceRepository, attestation.SourceRef,
            attestation.SourceBaseCommit, attestation.SourceWorktree,
            newClaim.RootElement, newSandbox.RootElement, newPod.RootElement);
        bool sourceStillCurrent;
        try
        {
            sourceStillCurrent = SourceStillCurrent(attestation, revision.TreeHash);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CurrentSandboxBindingResult.Unavailable("source_repository_unavailable");
        }
        if (newProof.State != "verified" || !sourceStillCurrent || !FenceUnchanged(
                events.BoundSequence, events.AttestationSequence,
                reread.BoundSequence, reread.AttestationSequence,
                lease.FencingToken, newLease?.FencingToken,
                attestation.ClaimUid,
                claim.RootElement.GetProperty("metadata").GetProperty("resourceVersion").GetString(),
                newClaim.RootElement.GetProperty("metadata").GetProperty("uid").GetString(),
                newClaim.RootElement.GetProperty("metadata").GetProperty("resourceVersion").GetString(),
                attestation.SandboxUid, newSandbox.RootElement.GetProperty("metadata").GetProperty("uid").GetString(),
                attestation.PodUid, newPod.RootElement.GetProperty("metadata").GetProperty("uid").GetString())
            || reread != events || newLease != lease
            || newRun?.LifecycleGeneration != run.LifecycleGeneration
            || newRun?.CurrentOutputRevisionId != run.CurrentOutputRevisionId
            || newRun?.TreeHash != revision.TreeHash)
            return CurrentSandboxBindingResult.Conflict("binding_rotated_during_read");
        return result;
        }
        }
    }

    public static bool FenceUnchanged(int bound, int attested, int newBound, int newAttested,
        long lease, long? newLease, string claimUid, string? claimVersion,
        string? newClaimUid, string? newClaimVersion,
        string sandboxUid, string? newSandboxUid, string podUid, string? newPodUid) =>
        bound == newBound && attested == newAttested && lease == newLease
        && claimUid == newClaimUid && !string.IsNullOrWhiteSpace(claimVersion)
        && claimVersion == newClaimVersion
        && sandboxUid == newSandboxUid && podUid == newPodUid;

    public static bool AttestationFollowsCurrentBinding(int boundSequence, int attestationSequence) =>
        boundSequence > 0 && attestationSequence > boundSequence;

    private static bool SourceStillCurrent(CurrentSandboxAttestation attestation, string tree)
    {
        using var repository = new Repository(attestation.SourceRepository);
        return repository.Lookup<Commit>(attestation.SourceBaseCommit) is { } commit
            && commit.Tree.Sha == tree
            && repository.Branches[attestation.SourceRef]?.Tip?.Sha == commit.Sha
            && Path.GetFileName(attestation.SourceWorktree.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                == $"assembly-build-test-{attestation.RunId}-attempt-{attestation.AssemblyAttempt}";
    }

    private static async Task<(int BoundSequence, string? BoundType, string? BoundJson,
        int AttestationSequence, string? AttestationJson)> LatestAsync(
        MemoryDbContext db, string runId, CancellationToken ct)
    {
        var bound = await db.RunEvents.AsNoTracking()
            .Where(e => e.RunId == runId && (e.EventType == RunEventExecutionPodNameStore.EventType
                || e.EventType == RunEventExecutionPodNameStore.UnboundEventType))
            .OrderByDescending(e => e.Sequence)
            .Select(e => new { e.Sequence, e.EventType, e.PayloadJson })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var attested = await db.RunEvents.AsNoTracking()
            .Where(e => e.RunId == runId && e.EventType == CurrentSandboxBindingVerifier.EventType)
            .OrderByDescending(e => e.Sequence)
            .Select(e => new { e.Sequence, e.PayloadJson })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return (bound?.Sequence ?? 0, bound?.EventType, bound?.PayloadJson,
            attested?.Sequence ?? 0, attested?.PayloadJson);
    }
}
