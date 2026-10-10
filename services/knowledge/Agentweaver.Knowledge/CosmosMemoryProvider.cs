using Agentweaver.Abstractions;
using Microsoft.Azure.Cosmos;

namespace Agentweaver.Knowledge;

public sealed class CosmosMemoryProvider : KnowledgeDocumentMemoryProviderCore
{
    public const string ProviderId = "cosmos.memory";
    public static Version AdapterVersion { get; } = new(1, 0, 0);

    private readonly ICosmosMemoryDocumentStore _store;
    private readonly CosmosMemoryOptions _options;

    public CosmosMemoryProvider(
        ICosmosMemoryDocumentStore store,
        CosmosMemoryOptions options,
        TimeProvider? timeProvider = null)
        : base(store, CreateDescriptor(options), timeProvider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _store = store;
        _options = options;
    }

    public override async Task<ResourceNegotiation> NegotiateAsync(
        ProviderCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Seam != ProviderSeam.Memory ||
            !string.Equals(candidate.ProviderId, ProviderId, StringComparison.Ordinal) ||
            candidate.AdapterVersion != AdapterVersion ||
            candidate.OptionsSchemaVersion != _options.OptionsSchemaVersion ||
            !string.Equals(candidate.OptionsRevision, _options.OptionsRevision, StringComparison.Ordinal) ||
            !MemoryProviderCapabilities.All.IsSubsetOf(candidate.AdvertisedCapabilities))
            throw new KnowledgeProviderUnavailableException(
                "The selected Memory provider candidate does not match configured Cosmos Memory options.");

        try
        {
            var identity = await _store.ReadContainerIdentityAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(identity.DatabaseId, _options.DatabaseId, StringComparison.Ordinal) ||
                !string.Equals(identity.ContainerId, _options.ContainerId, StringComparison.Ordinal) ||
                identity.PartitionKeyPaths.Length != 1 ||
                !string.Equals(
                    identity.PartitionKeyPaths[0],
                    CosmosMemoryOptions.PartitionKeyPath,
                    StringComparison.Ordinal) ||
                identity.DefaultTimeToLiveSeconds is > 0 ||
                !identity.HasRequiredSearchCompositeIndex)
                throw new KnowledgeProviderUnavailableException(
                    "The configured Cosmos Memory container has an incompatible identity, partition key, indexing, or retention policy.");
            return new ResourceNegotiation(
                new ProviderResourceRef(
                    ProviderSeam.Memory,
                    ProviderId,
                    _options.ResourceId,
                    _options.ResourceGeneration),
                MemoryProviderCapabilities.All);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (KnowledgeProviderUnavailableException)
        {
            throw;
        }
        catch (CosmosException)
        {
            throw new KnowledgeProviderUnavailableException(
                "The configured Cosmos Memory container could not be negotiated.");
        }
    }

    protected override bool IsStorageFailure(Exception exception) => exception is CosmosException;

    private static ProviderDescriptor CreateDescriptor(CosmosMemoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return new ProviderDescriptor(
            ProviderSeam.Memory,
            ProviderId,
            AdapterVersion,
            options.OptionsSchemaVersion,
            ProviderHostingPattern.RemoteService,
            MemoryProviderCapabilities.All);
    }
}
