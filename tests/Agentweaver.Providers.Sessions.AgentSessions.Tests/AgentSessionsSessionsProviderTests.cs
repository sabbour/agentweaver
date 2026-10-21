using System.Collections.Immutable;
using System.Security.Claims;
using Agentweaver.Abstractions;
using Xunit;

namespace Agentweaver.Providers.Sessions.AgentSessions.Tests;

public sealed class AgentSessionsSessionsProviderTests
{
    private const string NativeProviderId = "postgres.native-sessions";
    private static readonly AgentSessionsProviderOptions Options = new("agentsessions-fixture", 1, "rev-1");

    private static readonly ImmutableHashSet<string> ReadOnlyRequirement = ImmutableHashSet.Create(
        StringComparer.Ordinal, SessionsCapabilities.Replay, SessionsCapabilities.Subscribe);

    [Fact]
    public void DescriptorIsAnInProcessSessionsProviderWithoutAppendOrFork()
    {
        var descriptor = new AgentSessionsSessionsProvider().Descriptor;

        Assert.Equal(ProviderSeam.Sessions, descriptor.Seam);
        Assert.Equal(AgentSessionsSessionsProvider.ProviderId, descriptor.Id);
        Assert.Equal(ProviderHostingPattern.InProcess, descriptor.Hosting);
        Assert.True(descriptor.AdvertisedCapabilities.IsSubsetOf(SessionsCapabilities.All));
        Assert.DoesNotContain(SessionsCapabilities.Append, descriptor.AdvertisedCapabilities);
        Assert.DoesNotContain(SessionsCapabilities.Fork, descriptor.AdvertisedCapabilities);
    }

    [Fact]
    public void NativeProviderStaysTheDefaultWhenAgentSessionsIsRegistered()
    {
        var resolver = CreateResolver();

        var resolution = resolver.Resolve(Request(projectOverride: null, ReadOnlyRequirement));

        Assert.True(resolution.IsSuccess);
        Assert.Equal(NativeProviderId, resolution.Value!.Candidate!.ProviderId);
    }

    [Fact]
    public void ProjectOverrideSelectsNegotiatesAndPinsAgentSessions()
    {
        var provider = new AgentSessionsSessionsProvider();

        var pinned = provider.ResolveNegotiateAndPin(
            CreateResolver(), Request(AgentSessionsSessionsProvider.ProviderId, ReadOnlyRequirement), Options, "run-1");

        Assert.True(pinned.IsSuccess, pinned.Error?.Message);
        var binding = AgentSessionsSessionsProvider.ToSessionBinding("project-1", pinned.Value!);
        Assert.Equal(AgentSessionsSessionsProvider.ProviderId, binding.ProviderId);
        Assert.Equal("agentsessions-fixture", binding.ResourceId);
        Assert.Equal(1, binding.ResourceGeneration);
        Assert.True(binding.NegotiatedCapabilities.SetEquals(ReadOnlyRequirement));
    }

    [Fact]
    public void RunsThatRequireAppendCannotSelectAgentSessions()
    {
        var pinned = new AgentSessionsSessionsProvider().ResolveNegotiateAndPin(
            CreateResolver(), Request(AgentSessionsSessionsProvider.ProviderId, SessionsCapabilities.All), Options, "run-1");

        Assert.False(pinned.IsSuccess);
    }

    [Fact]
    public void NegotiationRejectsAnOptionsRevisionMismatch()
    {
        var provider = new AgentSessionsSessionsProvider();
        var resolution = CreateResolver().Resolve(Request(AgentSessionsSessionsProvider.ProviderId, ReadOnlyRequirement));

        var negotiation = provider.Negotiate(resolution.Value!.Candidate!, Options with { OptionsRevision = "rev-2" });

        Assert.False(negotiation.IsSuccess);
        Assert.Equal(ProviderErrorCode.InvalidConfiguration, negotiation.Error!.Code);
    }

    [Theory]
    [InlineData("", 1, "rev-1")]
    [InlineData("resource id", 1, "rev-1")]
    [InlineData("resource", 0, "rev-1")]
    [InlineData("resource", 1, "")]
    public void InvalidOptionsAreRejected(string resourceId, long generation, string revision) =>
        Assert.Throws<ArgumentException>(() =>
            new AgentSessionsSessionsProvider().CreateRegistration(new(resourceId, generation, revision)));

    internal static ProviderResolver CreateResolver()
    {
        // Stands in for the native PostgreSQL registration that the Events & Sessions host adds.
        var native = new ProviderRegistration(
            new ProviderDescriptor(
                ProviderSeam.Sessions, NativeProviderId, new Version(1, 0, 0), 1,
                ProviderHostingPattern.RemoteService, SessionsCapabilities.All),
            Enabled: true, "rev-1", 1);
        var catalog = ProviderCatalog.Create(
            [native, new AgentSessionsSessionsProvider().CreateRegistration(Options)],
            [new ProviderSelection(ProviderSeam.Sessions, NativeProviderId)],
            [new ProviderOverridePermission(ProviderSeam.Sessions, AgentSessionsSessionsProvider.ProviderId)]);
        Assert.True(catalog.IsSuccess, catalog.Error?.Message);
        return new ProviderResolver(catalog.Value!);
    }

    private static ProviderResolutionRequest Request(string? projectOverride, ImmutableHashSet<string> required) =>
        new(ProviderSeam.Sessions, projectOverride, AgentSessionsSessionsProvider.AdapterVersion, 1, required);
}

public sealed class AgentSessionsRecordJournalTests
{
    private const string ProjectId = "project-1";
    private const string RunId = "run-1";

    [Fact]
    public async Task ReplayRunServesEveryVerifiedRecordInRunOrder()
    {
        var journal = CreateJournal();

        var page = await journal.ReplayRunAsync(Principal(), new SessionRunEventPageRequest(ProjectId, RunId, Limit: 1000));

        var parentCount = Fixture("parent.json").Records.Count;
        var childCount = Fixture("child.json").Records.Count;
        Assert.False(page.HasMore);
        Assert.Equal(parentCount + childCount, page.Events.Length);
        Assert.Equal(Enumerable.Range(1, page.Events.Length).Select(i => (long)i), page.Events.Select(e => e.Position));
        Assert.Equal(page.Events.Length, page.Events.Select(e => e.EventId).Distinct().Count());
        Assert.All(page.Events.Take(parentCount), e => Assert.Equal("parent", e.Identity.SessionId));
        Assert.All(page.Events.Skip(parentCount), e => Assert.Equal("child", e.Identity.SessionId));
        Assert.All(page.Events, e => Assert.Equal(SessionsContractVersions.CurrentEventVersion, e.EventVersion));
    }

    [Fact]
    public async Task EachEnvelopeReferencesItsStoredRecord()
    {
        var page = await CreateJournal().ReplayAsync(Principal(), new SessionEventPageRequest("parent", Limit: 1000));

        var keys = page.Events.Select(e => Assert.Single(e.ObjectReferences).Reference.Key.Value).ToArray();
        Assert.Equal(
            Fixture("parent.json").Records.Select(r => $"agentsessions/agentweaver-fixture-parent/{r.Seq}"),
            keys);
        Assert.All(page.Events, e => Assert.Equal(
            AgentSessionsRecordJournal.RecordPurpose, e.ObjectReferences[0].Reference.Purpose));
        Assert.Contains(page.Events, e => e.Payload is TurnSessionPayload);
    }

    [Fact]
    public async Task SessionReplayPagesWithCursors()
    {
        var journal = CreateJournal();
        var seen = new List<long>();
        string? cursor = null;
        bool hasMore;
        do
        {
            var page = await journal.ReplayAsync(Principal(), new SessionEventPageRequest("child", cursor, Limit: 4));
            seen.AddRange(page.Events.Select(e => e.Position));
            cursor = page.NextCursor;
            hasMore = page.HasMore;
        }
        while (hasMore);

        Assert.Equal(Fixture("child.json").Records.Count, seen.Count);
        Assert.Equal(seen.Order(), seen);
    }

    [Fact]
    public async Task SubscriptionDeliversStoredEventsAndEnds()
    {
        var deliveries = new List<SessionEventDelivery>();
        await foreach (var delivery in CreateJournal().SubscribeAsync(
            Principal(), new SessionSubscriptionRequest("parent", MaximumEvents: 3)))
            deliveries.Add(delivery);

        Assert.Equal([1L, 2L, 3L], deliveries.Select(d => d.Event.Position));
        Assert.Equal("agentsessions:3", deliveries[^1].NextCursor);
    }

    [Fact]
    public async Task BindingReadsReturnThePinnedAgentSessionsBinding()
    {
        var journal = CreateJournal();

        Assert.Equal(AgentSessionsSessionsProvider.ProviderId,
            (await journal.GetProviderBindingAsync(Principal(), "child")).ProviderId);
        Assert.Equal(AgentSessionsSessionsProvider.ProviderId,
            (await journal.GetRunProviderBindingAsync(Principal(), ProjectId, RunId)).ProviderId);
    }

    [Fact]
    public async Task WritesAndForksAreNotSupported()
    {
        var journal = CreateJournal();

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            journal.CreateSessionAsync(Principal(), "new", Binding()));
        await Assert.ThrowsAsync<NotSupportedException>(() => journal.AppendAsync(
            Principal(), "parent",
            new AppendSessionEvent(Guid.NewGuid(), 1, 1, new ArtifactReferenceSessionPayload(
                new SessionObjectReference(new ObjectKey("a/b"), "artifact")))));
        await Assert.ThrowsAsync<SessionForkUnsupportedException>(() => journal.ForkFromExplicitEventAsync(
            Principal(), "parent", new SessionForkRequest("fork", Guid.NewGuid(), "agentsessions:1", "key"),
            _ => Task.CompletedTask));
    }

    [Fact]
    public async Task ReadsRequireTheBoundProjectRunAndAnExistingSession()
    {
        var journal = CreateJournal();

        await Assert.ThrowsAsync<SessionAccessDeniedException>(() =>
            journal.ReplayAsync(Principal(runId: "run-2"), new SessionEventPageRequest("parent")));
        await Assert.ThrowsAsync<SessionAccessDeniedException>(() =>
            journal.ReplayAsync(new ClaimsPrincipal(new ClaimsIdentity()), new SessionEventPageRequest("parent")));
        await Assert.ThrowsAsync<SessionNotFoundException>(() =>
            journal.ReplayAsync(Principal(), new SessionEventPageRequest("missing")));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            journal.ReplayAsync(Principal(), new SessionEventPageRequest("parent", Cursor: "opaque:1")));
    }

    [Fact]
    public void AlteredExportIsRejectedBeforeAnyEventIsServed()
    {
        var parent = Fixture("parent.json");
        var records = parent.Records.ToArray();
        records[2] = records[2] with { ContentHash = new string('0', 64) };

        var exception = Assert.Throws<AgentSessionsVerificationException>(() => AgentSessionsRecordJournal.Create(
            Binding(), [new("parent", parent with { Records = records })]));

        Assert.Equal("parent", exception.SessionId);
        Assert.False(exception.Verification.Verified);
        Assert.Equal(3, exception.Verification.Seq);
    }

    [Fact]
    public void BindingForAnotherProviderIsRejected()
    {
        var native = new SessionProviderBinding(
            ProjectId, RunId, "postgres.native-sessions", new Version(1, 0, 0), 1, "rev-1", "sessions", 1,
            SessionsCapabilities.All);

        Assert.Throws<ArgumentException>(() =>
            AgentSessionsRecordJournal.Create(native, [new("parent", Fixture("parent.json"))]));
    }

    private static AgentSessionsRecordJournal CreateJournal() => AgentSessionsRecordJournal.Create(
        Binding(), [new("parent", Fixture("parent.json")), new("child", Fixture("child.json"))]);

    private static SessionProviderBinding Binding() => new(
        ProjectId, RunId, AgentSessionsSessionsProvider.ProviderId, AgentSessionsSessionsProvider.AdapterVersion,
        AgentSessionsSessionsProvider.OptionsSchemaVersion, "rev-1", "agentsessions-fixture", 1,
        AgentSessionsSessionsProvider.Capabilities);

    private static ClaimsPrincipal Principal(string projectId = ProjectId, string runId = RunId) => new(
        new ClaimsIdentity(
            [new Claim("sub", Guid.NewGuid().ToString("D")), new Claim("project_id", projectId), new Claim("run_id", runId)],
            authenticationType: "test"));

    private static AgentSessionsExportDocument Fixture(string name) => AgentSessionsRecordVerifier.ParseExport(
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "AgentSessions", name)));
}
