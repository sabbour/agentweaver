using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.Environment.Tests;

public sealed partial class RuntimeResumeBootstrapTests
{
    [Fact]
    public void JournalRestoredReadinessKeepsSourceEvidenceAndDoesNotClaimANewNativeCache()
    {
        var request = Create();
        var prefix = JournalPrefix(request);
        var receipt = JournalCache(request, prefix);

        Assert.Equal(receipt, receipt.ValidateFor(request));
        Assert.Equal(request.Resume.SourceManifest.CacheAcknowledgment, receipt.CacheAcknowledgment);
        Assert.NotEqual(receipt.CacheAcknowledgment.Reference.Material!.SdkVersion, receipt.Source.SdkVersion);
        Assert.Equal(RuntimeResumeNativeCacheState.PendingUntilAuthorizedTurn,
            receipt.JournalRebuild!.NativeCacheState);
        var replay = JsonSerializer.Deserialize<RuntimeHostResumeCacheReceipt>(
            JsonSerializer.Serialize(receipt, JsonOptions), JsonOptions)!;
        replay.ValidateFor(request);
        Assert.True(RuntimeWorkspaceResumeHttpClient.SameJson(receipt, replay));
        Assert.False(JsonSerializer.SerializeToElement(Cache(request), JsonOptions)
            .TryGetProperty("journalRebuild", out _));
    }

    [Theory]
    [InlineData("checkpoint")]
    [InlineData("position")]
    [InlineData("hash")]
    [InlineData("attempt")]
    [InlineData("unspecified")]
    [InlineData("unknown-state")]
    [InlineData("source-cache")]
    [InlineData("registration")]
    [InlineData("session")]
    [InlineData("native-cache")]
    [InlineData("unknown-mode")]
    public void JournalRestoredReadinessRejectsChangedProvenanceOrCurrentBindings(string fault)
    {
        var request = Create();
        var receipt = JournalCache(request, JournalPrefix(request));
        var proof = receipt.JournalRebuild!;
        proof = fault switch
        {
            "checkpoint" => proof with { Checkpoint = proof.Checkpoint with { Revision = 99 } },
            "position" => proof with { ThroughPosition = proof.ThroughPosition + 1 },
            "hash" => proof with { JournalSha256 = "invalid" },
            "attempt" => proof with { ResumeAttemptId = Guid.NewGuid() },
            "unspecified" => proof with { NativeCacheState = RuntimeResumeNativeCacheState.Unspecified },
            "unknown-state" => proof with { NativeCacheState = (RuntimeResumeNativeCacheState)99 },
            _ => proof
        };
        receipt = receipt with { JournalRebuild = proof };
        receipt = fault switch
        {
            "source-cache" => receipt with
            {
                CacheAcknowledgment = receipt.CacheAcknowledgment with { EventId = Guid.NewGuid() }
            },
            "registration" => receipt with
            {
                Registration = receipt.Registration with { RuntimeInstanceId = Guid.NewGuid() }
            },
            "session" => receipt with { Source = receipt.Source with { SdkSessionId = "foreign-session" } },
            "native-cache" => receipt with { RecoveryMode = "native-cache" },
            "unknown-mode" => receipt with { RecoveryMode = "unknown" },
            _ => receipt
        };
        Assert.Throws<RuntimeAuthorizationException>(() => receipt.ValidateFor(request));
    }

    [Fact]
    public void SavedSessionPrefixAllowsInterleavedRunPositionsAndAuthenticatedForkHistory()
    {
        var request = Create();
        var prefix = JournalPrefix(request);
        Assert.Equal(new long[] { 8, 9 }, prefix.Select(recorded => recorded.Position));
        var hash = RuntimeResumeJournalRebuildReceipt.ComputeJournalSha256(request, prefix);
        var replay = JsonSerializer.Deserialize<SessionEventEnvelope[]>(
            JsonSerializer.Serialize(prefix, JsonOptions), JsonOptions)!;
        Assert.Equal(hash, RuntimeResumeJournalRebuildReceipt.ComputeJournalSha256(request, replay));
        var inherited = JournalEvent(request, Guid.NewGuid(), 3,
            new AcceptedEffectSessionPayload("effect", "recorded", null),
            new(request.Resume.SourceManifest.Identity.ProjectId, request.Resume.SourceManifest.Identity.RunId, "parent"));
        Assert.NotEqual(hash, RuntimeResumeJournalRebuildReceipt.ComputeJournalSha256(request, [inherited, .. prefix]));
    }

    [Theory]
    [InlineData("cache-reference")]
    [InlineData("configuration-reference")]
    [InlineData("cache-missing")]
    [InlineData("configuration-missing")]
    [InlineData("duplicate-id")]
    [InlineData("order")]
    [InlineData("upper-bound")]
    [InlineData("foreign-run")]
    [InlineData("schema")]
    [InlineData("version")]
    public void SavedSessionPrefixRejectsChangedRequiredEvidenceOrInvalidOrdering(string fault)
    {
        var request = Create();
        var prefix = JournalPrefix(request);
        var configuration = request.Resume.SourceRelease.HostReceipt.ConfigurationAcknowledgment!;
        var cache = request.Resume.SourceManifest.CacheAcknowledgment!;
        prefix = fault switch
        {
            "cache-reference" => [prefix[0], JournalEvent(request, cache.EventId, cache.Position,
                new CacheReferenceSessionPayload(cache.Reference with { Key = new("foreign/cache") }, "runtime", "binding"))],
            "configuration-reference" => [JournalEvent(request, configuration.EventId, configuration.Position,
                new RuntimeConfigurationSessionPayload(configuration.Reference with { Key = new("foreign/config") })), prefix[1]],
            "cache-missing" => [prefix[0]],
            "configuration-missing" => [prefix[1]],
            "duplicate-id" => [prefix[0], JournalEvent(request, configuration.EventId, cache.Position,
                new CacheReferenceSessionPayload(cache.Reference, "runtime", "binding"))],
            "order" => [prefix[1], prefix[0]],
            "upper-bound" => [.. prefix, JournalEvent(request, Guid.NewGuid(), cache.Position + 1,
                new AcceptedEffectSessionPayload("effect", "recorded", null))],
            "foreign-run" => [JournalEvent(request, configuration.EventId, configuration.Position,
                new RuntimeConfigurationSessionPayload(configuration.Reference), new("project", "foreign-run", "session")),
                prefix[1]],
            "schema" => [JournalEvent(request, configuration.EventId, configuration.Position,
                new RuntimeConfigurationSessionPayload(configuration.Reference), schemaVersion: 99), prefix[1]],
            _ => [JournalEvent(request, configuration.EventId, configuration.Position,
                new RuntimeConfigurationSessionPayload(configuration.Reference), eventVersion: 99), prefix[1]]
        };
        Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeResumeJournalRebuildReceipt.ComputeJournalSha256(request, prefix));
    }

    [Fact]
    public void SavedSessionPrefixHasABoundedSerializedSize()
    {
        var request = Create();
        var events = Enumerable.Range(1, 3000).Select(position => JournalEvent(request, Guid.NewGuid(), position,
            new AcceptedEffectSessionPayload(new string('e', 256), "recorded", null))).ToList();
        var host = request.Resume.SourceRelease.HostReceipt;
        var configuration = host.ConfigurationAcknowledgment! with { Position = 3001 };
        var cache = host.CacheAcknowledgment with { Position = 3002 };
        host = host with { ConfigurationAcknowledgment = configuration, CacheAcknowledgment = cache };
        request = request with
        {
            Resume = request.Resume with
            {
                SourceRelease = request.Resume.SourceRelease with { HostReceipt = host },
                SourceManifest = request.Resume.SourceManifest with
                {
                    CacheAcknowledgment = cache, FlushedJournalPosition = cache.Position
                }
            }
        };
        events.Add(JournalEvent(request, configuration.EventId, configuration.Position,
            new RuntimeConfigurationSessionPayload(configuration.Reference)));
        events.Add(JournalEvent(request, cache.EventId, cache.Position,
            new CacheReferenceSessionPayload(cache.Reference, "runtime", "binding")));
        Assert.Equal("runtime_resume_journal_prefix_too_large", Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeResumeJournalRebuildReceipt.ComputeJournalSha256(request, events)).Code);
    }

    private static RuntimeHostResumeCacheReceipt JournalCache(
        RuntimeResumeBootstrapRequest request, IReadOnlyList<SessionEventEnvelope> prefix) =>
        Cache(request) with
        {
            RecoveryMode = "journal-rebuild",
            Source = Cache(request).Source with { SdkVersion = "different-sdk", RuntimeVersion = "different-runtime" },
            JournalRebuild = new(request.Resume.SourceManifest.Checkpoint!,
                request.Resume.SourceManifest.FlushedJournalPosition!.Value,
                RuntimeResumeJournalRebuildReceipt.ComputeJournalSha256(request, prefix),
                request.ResumeAttemptId, RuntimeResumeNativeCacheState.PendingUntilAuthorizedTurn)
        };

    private static SessionEventEnvelope[] JournalPrefix(RuntimeResumeBootstrapRequest request)
    {
        var configuration = request.Resume.SourceRelease.HostReceipt.ConfigurationAcknowledgment!;
        var cache = request.Resume.SourceManifest.CacheAcknowledgment!;
        return
        [
            JournalEvent(request, configuration.EventId, configuration.Position,
                new RuntimeConfigurationSessionPayload(configuration.Reference)),
            JournalEvent(request, cache.EventId, cache.Position,
                new CacheReferenceSessionPayload(cache.Reference, cache.Reference.Material!.RuntimeVersion, "binding"))
        ];
    }

    private static SessionEventEnvelope JournalEvent(
        RuntimeResumeBootstrapRequest request, Guid eventId, long position, SessionEventPayload payload,
        SessionIdentity? identity = null, int schemaVersion = 1, int eventVersion = 2) =>
        JsonSerializer.Deserialize<SessionEventEnvelope>(JsonSerializer.Serialize(new
        {
            SchemaVersion = schemaVersion, EventVersion = eventVersion, EventId = eventId,
            Identity = identity ?? request.Resume.SourceManifest.Identity, Position = position,
            OccurredAt = DateTimeOffset.UnixEpoch, Payload = payload,
            ObjectReferences = Array.Empty<StoredSessionObjectReference>()
        }, JsonOptions), JsonOptions)!;
}
