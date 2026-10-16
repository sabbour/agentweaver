using Agentweaver.Abstractions;
using StackExchange.Redis;

namespace Agentweaver.Knowledge;

public sealed class RedisMemoryProvider : KnowledgeDocumentMemoryProviderCore
{
    public const string ProviderId = "redis.memory";
    public static Version AdapterVersion { get; } = new(1, 0, 0);

    private readonly RedisMemoryDocumentStore _store;
    private readonly RedisMemoryOptions _options;

    public RedisMemoryProvider(
        RedisMemoryDocumentStore store,
        RedisMemoryOptions options,
        TimeProvider? timeProvider = null)
        : base(store, CreateDescriptor(options), timeProvider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _store = store;
        _options = options;
    }

    protected override bool RequiresStoreLeaseExpiryCheck => true;

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
                "The selected Memory provider candidate does not match configured Redis Memory options.");

        await _store.VerifyProviderCompatibilityAsync(cancellationToken).ConfigureAwait(false);
        return new ResourceNegotiation(
            new ProviderResourceRef(
                ProviderSeam.Memory,
                ProviderId,
                _options.ResourceId,
                _options.ResourceGeneration),
            MemoryProviderCapabilities.All);
    }

    protected override bool IsStorageFailure(Exception exception) =>
        exception is RedisException or TimeoutException or IOException;

    private static ProviderDescriptor CreateDescriptor(RedisMemoryOptions options)
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
