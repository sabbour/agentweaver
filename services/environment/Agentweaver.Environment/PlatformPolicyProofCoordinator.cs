using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;

namespace Agentweaver.Environment;

public sealed record PlatformPolicyProofPreparation(
    string PreparationId,
    long RegistryRevision,
    string SourceId,
    long SourceRevision,
    string SourceHash,
    string CompilerId,
    ImmutableArray<PlatformEgressDestination> SystemDestinations);

public sealed record PlatformPolicyProofFrozenTarget(
    Guid HoldId,
    string TargetId,
    string PhysicalTarget,
    string Namespace,
    string NamespaceUid,
    long SealEpoch,
    string GuardDigest,
    string ProfileDigest)
{
    internal KubernetesPlatformPolicyCapture Capture(
        SystemOnlyPolicyCeiling ceiling) =>
        new(
            Namespace,
            NamespaceUid,
            SealEpoch,
            GuardDigest,
            ProfileDigest,
            SystemOnlyPolicySetEvaluation.Version,
            SystemOnlyPolicyCeiling.Version,
            ceiling.Hash());
}

public sealed class PlatformPolicyProofCoordinator
{
    private readonly KubernetesPlatformPolicyInventoryReader inventory;
    private readonly CiliumPlatformEgressProvider provider;
    private readonly PlatformPolicyProofStore store;
    private readonly TimeProvider timeProvider;

    internal PlatformPolicyProofCoordinator(
        KubernetesPlatformPolicyInventoryReader inventory,
        CiliumPlatformEgressProvider provider,
        PlatformPolicyProofStore store,
        TimeProvider timeProvider)
    {
        this.inventory = inventory;
        this.provider = provider;
        this.store = store;
        this.timeProvider = timeProvider;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task<PlatformPolicyProofReceipt> ProveAsync(
        PlatformPolicyProofPreparation preparation,
        PlatformPolicyProofFrozenTarget target,
        PlatformEgressApplyReservation reservation,
        CancellationToken cancellationToken)
    {
        Validate(preparation, target, reservation);
        var ceiling = SystemOnlyPolicyCeiling.Create(preparation.SystemDestinations);
        string providerTarget;
        try
        {
            providerTarget = provider.GetProofTargetId();
        }
        catch (CiliumPolicyException)
        {
            throw new PlatformPolicyProofException(
                "policy_fence_unavailable", "The provider target identity is unavailable.");
        }
        if (string.IsNullOrWhiteSpace(providerTarget))
            throw new PlatformPolicyProofException(
                "policy_fence_unavailable", "The provider target identity is unavailable.");
        if (providerTarget != target.TargetId || providerTarget != reservation.TargetId ||
            provider.ProofNamespace != target.Namespace)
            throw new PlatformPolicyProofException(
                "policy_fence_conflict", "The provider and reservation targets do not agree.");
        var readerAddress = RequireProofOrigin(inventory.BaseAddress);
        var writerAddress = RequireProofOrigin(provider.ProofBaseAddress);
        if (readerAddress != writerAddress)
            throw new PlatformPolicyProofException(
                "policy_fence_conflict", "The policy reader and writer targets do not agree.");
        reservation = await store.AcquireProofHoldAsync(
            new(
                target.HoldId,
                target.TargetId,
                target.PhysicalTarget,
                target.Namespace,
                target.NamespaceUid,
                target.SealEpoch,
                target.GuardDigest,
                target.ProfileDigest,
                preparation.RegistryRevision,
                preparation.PreparationId,
                reservation.ReservationId),
            reservation, cancellationToken).ConfigureAwait(false);

        var currentReceipt = await store.ReadCurrentReceiptAsync(
            target.TargetId, target.HoldId, cancellationToken).ConfigureAwait(false);
        if (currentReceipt is not null)
        {
            if (currentReceipt.ReservationId != reservation.ReservationId ||
                currentReceipt.SourceEpoch != reservation.SourceEpoch ||
                currentReceipt.SourceRevision != preparation.SourceRevision ||
                currentReceipt.SourceHash != preparation.SourceHash ||
                currentReceipt.PolicyGeneration != reservation.Generation)
                throw new PlatformPolicyProofException(
                    "policy_fence_conflict", "The hold was proven for different policy input.");
            return currentReceipt with { Replayed = true };
        }

        var before = await inventory.CaptureAsync(
                target.PhysicalTarget, target.Capture(ceiling), cancellationToken)
            .ConfigureAwait(false);
        _ = PlatformPolicyProofSnapshot.Serialize(before);
        var named = await inventory.GetNamedAsync(
                target.Namespace, CiliumPlatformEgressProvider.PolicyName, cancellationToken)
            .ConfigureAwait(false);
        var typedNamed = DeserializePolicy(named);
        var rendered = provider.RenderForProof(typedNamed, reservation.Policy, reservation.SourceEpoch);
        var candidate = ToRecord(rendered);
        var candidateInventory = before.Replace(candidate);
        SystemOnlyPolicySetEvaluator.Evaluate(candidateInventory, ceiling);
        var listed = before.Policies.SingleOrDefault(policy =>
            KubernetesPlatformPolicyInventory.SameIdentity(policy, candidate));
        _ = PlatformPolicyProofSnapshot.Serialize(before.Replace(
            IncludeRetainedMetadata(candidate, named, listed)));

        PlatformEgressStatus? status;
        try
        {
            status = await provider.ApplyReservedAsync(reservation, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            status = await provider.ApplyReservedAsync(reservation, cancellationToken).ConfigureAwait(false);
        }
        if (status.Message == "policy_fence_unavailable")
            throw new PlatformPolicyProofException(
                "policy_fence_unavailable", "The policy fence store is unavailable.");
        if (!status.Matches(reservation.Policy.IntentHash, reservation.Policy.SourceHash) ||
            status.Generation != reservation.Generation)
            throw new PlatformPolicyProofException(
                "policy_readback_mismatch", "The reserved Kubernetes write did not produce exact readback.");

        var after = await inventory.CaptureAsync(
                target.PhysicalTarget, target.Capture(ceiling), cancellationToken)
            .ConfigureAwait(false);
        var namedAfter = await inventory.GetNamedAsync(
                target.Namespace, CiliumPlatformEgressProvider.PolicyName, cancellationToken)
            .ConfigureAwait(false);
        var listedAfter = after.Policies.SingleOrDefault(policy =>
            policy.Kind == "CiliumNetworkPolicy" &&
            policy.Namespace == target.Namespace &&
            policy.Name == CiliumPlatformEgressProvider.PolicyName)
            ?? throw new PlatformPolicyProofException(
                "policy_readback_mismatch", "The full inventory does not contain the reserved policy.");
        if (namedAfter.Uid != reservation.ObjectUid ||
            namedAfter.ResourceVersion != listedAfter.ResourceVersion ||
            namedAfter.Uid != listedAfter.Uid ||
            namedAfter.CanonicalContent != listedAfter.CanonicalContent)
            throw new PlatformPolicyProofException(
                "policy_readback_mismatch", "The named read and policy list disagree.");

        SystemOnlyPolicySetEvaluator.Evaluate(after, ceiling);
        var typedAfter = DeserializePolicy(namedAfter);
        if (ReadAnnotation(typedAfter, CiliumPlatformEgressProvider.GenerationAnnotation) !=
                reservation.Generation.ToString(CultureInfo.InvariantCulture) ||
            ReadAnnotation(typedAfter, CiliumPlatformEgressProvider.SourceEpochAnnotation) !=
                reservation.SourceEpoch.ToString(CultureInfo.InvariantCulture) ||
            ReadAnnotation(typedAfter, CiliumPlatformEgressProvider.IntentHashAnnotation) !=
                reservation.Policy.IntentHash ||
            ReadAnnotation(typedAfter, CiliumPlatformEgressProvider.SourceHashAnnotation) !=
                reservation.Policy.SourceHash)
            throw new PlatformPolicyProofException(
                "policy_readback_mismatch", "The observed policy lost its reserved source and intent binding.");

        var snapshotJson = PlatformPolicyProofSnapshot.Serialize(after);
        var specDigest = Sha256(JsonSerializer.SerializeToUtf8Bytes(typedAfter.Spec, JsonOptions));
        return await store.PersistReceiptAsync(
            new(
                target.HoldId,
                target.HoldId,
                target.TargetId,
                reservation.ReservationId,
                reservation.SourceEpoch,
                preparation.SourceRevision,
                preparation.SourceId,
                preparation.SourceHash,
                reservation.Policy.IntentHash,
                preparation.CompilerId,
                SystemOnlyPolicySetEvaluation.Version,
                SystemOnlyPolicyCeiling.Version,
                target.ProfileDigest,
                target.GuardDigest,
                after.SnapshotId,
                specDigest,
                namedAfter.ResourceVersion,
                namedAfter.Uid,
                reservation.Generation,
                snapshotJson,
                timeProvider.GetUtcNow()),
            cancellationToken).ConfigureAwait(false);
    }

    public Task<PlatformPolicyProofReceipt?> ReadCurrentReceiptAsync(
        string targetId,
        Guid holdId,
        CancellationToken cancellationToken) =>
        store.ReadCurrentReceiptAsync(targetId, holdId, cancellationToken);

    public Task RequireAllTargetsAsync(
        long registryRevision,
        string preparationId,
        IEnumerable<string> targetIds,
        CancellationToken cancellationToken) =>
        store.RequireAllTargetsAsync(
            registryRevision, preparationId, targetIds, cancellationToken);

    internal static KubernetesPlatformPolicyRecord ToRecord(CiliumNetworkPolicyDocument policy)
    {
        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(policy, JsonOptions));
        return new(
            policy.ApiVersion,
            policy.Kind,
            policy.Metadata.Namespace,
            policy.Metadata.Name,
            policy.Metadata.Uid ?? string.Empty,
            policy.Metadata.ResourceVersion ?? string.Empty,
            CanonicalJson(document.RootElement),
            document.RootElement.Clone());
    }

    private static KubernetesPlatformPolicyRecord IncludeRetainedMetadata(
        KubernetesPlatformPolicyRecord candidate,
        KubernetesPlatformPolicyRecord named,
        KubernetesPlatformPolicyRecord? listed)
    {
        var root = JsonNode.Parse(candidate.Content.GetRawText())!.AsObject();
        var metadata = root["metadata"]!.AsObject();
        var renderedFields = metadata.Select(property => property.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var source in new[] { named, listed })
        {
            if (source is null)
                continue;
            foreach (var property in source.Content.GetProperty("metadata").EnumerateObject())
            {
                if (renderedFields.Contains(property.Name))
                    continue;
                var value = JsonNode.Parse(property.Value.GetRawText());
                if (!metadata.TryGetPropertyValue(property.Name, out var existing) ||
                    EncodedSize(value) > EncodedSize(existing))
                    metadata[property.Name] = value;
            }
        }
        using var document = JsonDocument.Parse(root.ToJsonString());
        return candidate with
        {
            CanonicalContent = CanonicalJson(document.RootElement),
            Content = document.RootElement.Clone(),
        };
    }

    private static int EncodedSize(JsonNode? value) =>
        Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(value?.ToJsonString() ?? "null"));

    private static CiliumNetworkPolicyDocument DeserializePolicy(KubernetesPlatformPolicyRecord record)
    {
        if (record.ApiVersion != "cilium.io/v2" ||
            record.Kind != "CiliumNetworkPolicy")
            throw new PlatformPolicyProofException(
                "policy_readback_mismatch", "The reserved target is not the expected Cilium object.");
        return JsonSerializer.Deserialize<CiliumNetworkPolicyDocument>(
                record.Content.GetRawText(), JsonOptions) ??
            throw new PlatformPolicyProofException(
                "policy_inventory_unavailable", "The named policy is malformed.");
    }

    private static string ReadAnnotation(CiliumNetworkPolicyDocument policy, string key) =>
        policy.Metadata.Annotations.GetValueOrDefault(key) ?? string.Empty;

    private static string CanonicalJson(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Object => "{" + string.Join(",", element.EnumerateObject()
                .OrderBy(property => property.Name, StringComparer.Ordinal)
                .Select(property => JsonSerializer.Serialize(property.Name) + ":" + CanonicalJson(property.Value))) + "}",
            JsonValueKind.Array => "[" + string.Join(",", element.EnumerateArray().Select(CanonicalJson)) + "]",
            _ => element.GetRawText(),
        };

    private static string Sha256(byte[] value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    internal static Uri RequireProofOrigin(Uri? address)
    {
        try
        {
            var origin = EnvironmentHttpTransport.RequireTrustedHttpsBaseUri(
                address?.OriginalString ?? string.Empty, "Kubernetes:ApiServer");
            if (origin.AbsolutePath != "/")
                throw new InvalidOperationException();
            return origin;
        }
        catch (InvalidOperationException)
        {
            throw new PlatformPolicyProofException(
                "policy_fence_unavailable", "The Kubernetes proof origin must be an absolute HTTPS root.");
        }
    }

    private static void Validate(
        PlatformPolicyProofPreparation preparation,
        PlatformPolicyProofFrozenTarget target,
        PlatformEgressApplyReservation reservation)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(reservation);
        if (target.HoldId == Guid.Empty ||
            string.IsNullOrWhiteSpace(target.TargetId) ||
            string.IsNullOrWhiteSpace(target.PhysicalTarget) ||
            string.IsNullOrWhiteSpace(target.Namespace) ||
            !IsSha256(target.GuardDigest) ||
            !IsSha256(target.ProfileDigest))
            throw new PlatformPolicyProofException(
                "policy_fence_unavailable", "The frozen target does not prove a valid sealed epoch.");
        if (preparation.SystemDestinations.IsDefault ||
            string.IsNullOrWhiteSpace(preparation.SourceId) ||
            string.IsNullOrWhiteSpace(preparation.CompilerId) ||
            preparation.CompilerId != PlatformEgressAcsCompiler.CompilerVersion ||
            !IsSha256(preparation.SourceHash) ||
            reservation.Policy is null ||
            reservation.Policy.Destinations.IsDefault)
            throw new PlatformPolicyProofException(
                "policy_semantics_unsupported", "The preparation is not in the supported System-only profile.");
        if (preparation.RegistryRevision <= 0 ||
            preparation.SourceRevision <= 0 ||
            !preparation.SystemDestinations.SequenceEqual(reservation.Policy.Destinations) ||
            preparation.SourceHash != reservation.Policy.SourceHash ||
            preparation.SourceId != reservation.SourceId ||
            preparation.SourceRevision != reservation.SourceRevision)
            throw new PlatformPolicyProofException(
                "policy_fence_conflict", "The frozen target, preparation, and reservation do not agree.");
        if (reservation.TargetId != target.TargetId)
            throw new PlatformPolicyProofException(
                "policy_fence_conflict", "The reservation is bound to another target.");
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character =>
            char.IsAsciiDigit(character) || character is >= 'a' and <= 'f');
}
