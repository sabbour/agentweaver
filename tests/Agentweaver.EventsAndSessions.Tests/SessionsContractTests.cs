using System.Collections.Immutable;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.EventsAndSessions;
using Agentweaver.Providers;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;
using Xunit;

namespace Agentweaver.EventsAndSessions.Tests;

public sealed class SessionsContractTests
{
    [Fact]
    public void AddressedMessagePersistenceIsNotAnExportedServiceApi()
    {
        var exportedTypes = typeof(PostgresAddressedMessageStore).Assembly.GetExportedTypes();

        Assert.DoesNotContain(typeof(PostgresAddressedMessageStore), exportedTypes);
        Assert.DoesNotContain(typeof(AddressedMessageException), exportedTypes);
        Assert.DoesNotContain(typeof(MessagingProviderUnavailableException), exportedTypes);
        Assert.DoesNotContain(typeof(MessagingProviderBindingConflictException), exportedTypes);
    }

    [Fact]
    public void PayloadKindsAreValidatedAndHaveNoCredentialBearingFields()
    {
        var payloads = new SessionEventPayload[]
        {
            new TurnSessionPayload("user", Ref("turns/input")),
            new ToolCallSessionPayload("call-1", "search", "completed", Ref("calls/args"), Ref("calls/result")),
            PolicyEvaluation(),
            new AcceptedDecisionSessionPayload("decision-1", "route", "approved", null, ["effect-1"]),
            new AcceptedEffectSessionPayload("effect-1", "publish", Ref("effects/receipt")),
            new ArtifactReferenceSessionPayload(Ref("artifacts/result")),
            new CacheReferenceSessionPayload(Ref("cache/copilot"), "runtime-v1", "binding-1"),
            new AddressedMessageSessionPayload(
                Guid.NewGuid(),
                new SessionIdentity("project-1", "run-1", "session-1"),
                new SessionIdentity("project-1", "run-2", "session-2"),
                Guid.NewGuid(),
                1,
                AddressedMessagePurpose.Handoff),
            CapturePayload(),
        };

        Assert.Equal(Enum.GetValues<SessionEventKind>(),
            payloads.Select(SessionEventPayloadValidation.KindOf));
        Assert.Throws<ArgumentException>(() =>
            SessionEventPayloadValidation.ValidateAndGetReferences(
                new TurnSessionPayload("human", Ref("turns/input"))));
        Assert.Throws<ArgumentException>(() =>
            SessionEventPayloadValidation.ValidateAndGetReferences(
                new ToolCallSessionPayload("call-1", "search", "credential", null, null)));
        Assert.Throws<ArgumentException>(() =>
            SessionEventPayloadValidation.ValidateAndGetReferences(
                new ToolCallSessionPayload("call-1", "search", "completed",
                    Ref("calls/shared"), Ref("calls/shared"))));
        Assert.Throws<ArgumentException>(() =>
            SessionEventPayloadValidation.ValidateAndGetReferences(
                new AcceptedDecisionSessionPayload("d", "type", "choice", null, default)));
        Assert.Throws<ArgumentException>(() =>
            SessionEventPayloadValidation.ValidateAndGetReferences(
                PolicyEvaluation() with { Fence = 0 }));
        Assert.Throws<ArgumentException>(() =>
            SessionEventPayloadValidation.ValidateAndGetReferences(
                PolicyEvaluation() with
                {
                    Outcome = PolicyEvaluationOutcome.Error,
                    ReasonCode = PolicyEvaluationReasonCode.Allowed
                }));
        Assert.Throws<ArgumentException>(() =>
            SessionEventPayloadValidation.ValidateAndGetReferences(
                PolicyEvaluation() with { ActionId = "action with user text" }));
        Assert.Throws<ArgumentException>(() =>
            SessionEventPayloadValidation.ValidateAndGetReferences(
                PolicyEvaluation() with { GrantRevision = "revision with user text" }));
        Assert.Throws<ArgumentException>(() =>
            SessionEventPayloadValidation.ValidateAndGetReferences(
                PolicyEvaluation() with { TenantId = "" }));
    }

    [Fact]
    public void PolicyEvaluationPayloadIsTypedAndContainsNoFreeFormEvidence()
    {
        SessionEventPayload payload = PolicyEvaluation();
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());

        var json = JsonSerializer.Serialize(payload, options);
        var decoded = Assert.IsType<PolicyEvaluationSessionPayload>(
            JsonSerializer.Deserialize<SessionEventPayload>(json, options));

        Assert.Equal(payload, decoded);
        Assert.Contains("\"kind\":\"policy_evaluation\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("credential", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("arguments", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("message", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ruleText", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"grantId\":\"grant-1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"grantRevision\":\"revision-1\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void AddressedMessagePayloadRejectsCrossProjectReferences()
    {
        Assert.Throws<ArgumentException>(() =>
            SessionEventPayloadValidation.ValidateAndGetReferences(
                new AddressedMessageSessionPayload(
                    Guid.NewGuid(),
                    new SessionIdentity("project-1", "run-1", "session-1"),
                    new SessionIdentity("project-2", "run-2", "session-2"),
                    Guid.NewGuid(),
                    1,
                    AddressedMessagePurpose.Progress)));
    }

    [Fact]
    public void AddressedMessageContractSeparatesUserQuoteAndCoordinatorInstructions()
    {
        using var payload = JsonDocument.Parse("""{"question":"Proceed?"}""");
        var draft = new AddressedMessageDraft(
            new SessionIdentity("project-1", "run-1", "session-1"),
            new SessionIdentity("project-1", "run-2", "session-2"),
            "message-key",
            AddressedMessageDeliveryMode.Immediate,
            AddressedMessagePurpose.NeedsInput,
            AddressedMessageKind.Question,
            payload.RootElement.Clone(),
            SenderFence: 4,
            RecipientFence: 9,
            RequestId: "gate-42",
            UserQuote: "Please check the deployment.",
            CoordinatorInstructions: "Answer only from the recorded evidence.");

        var normalized = AddressedMessageValidation.ValidateAndNormalize(draft);

        Assert.Equal("Please check the deployment.", normalized.UserQuote);
        Assert.Equal("Answer only from the recorded evidence.", normalized.CoordinatorInstructions);
        Assert.NotEqual(normalized.UserQuote, normalized.CoordinatorInstructions);
        Assert.Throws<ArgumentException>(() =>
            AddressedMessageValidation.ValidateAndNormalize(draft with
            {
                Recipient = new SessionIdentity("project-2", "run-2", "session-2")
            }));
        Assert.Throws<ArgumentException>(() =>
            AddressedMessageValidation.ValidateAndNormalize(draft with { RequestId = null }));
    }

    [Fact]
    public void VersionedEventPayloadRoundTripsAsAProviderNeutralReference()
    {
        var input = new AppendSessionEvent(
            Guid.NewGuid(),
            SessionsContractVersions.CurrentSchemaVersion,
            SessionsContractVersions.CurrentEventVersion,
            new CacheReferenceSessionPayload(Ref("cache/runtime"), "runtime-v1", "binding-1"));
        var json = JsonSerializer.Serialize(input, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var decoded = JsonSerializer.Deserialize<AppendSessionEvent>(
            json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var payload = Assert.IsType<CacheReferenceSessionPayload>(decoded!.Payload);
        Assert.Equal("cache/runtime", payload.Cache.Key.Value);
        Assert.Equal(SessionEventKind.CacheReference, SessionEventPayloadValidation.KindOf(payload));
        Assert.DoesNotContain("credential", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClaimsRequireExactlyOneWellFormedProjectAndRun()
    {
        var valid = IdentityBrokerPrincipal(("project_id", "project-1"), ("run_id", "run-1"));
        Assert.True(SessionIdentityClaims.TryGetScope(valid, out var scope));
        Assert.Equal("project-1", scope!.Value.ProjectId);

        Assert.False(SessionIdentityClaims.TryGetScope(IdentityBrokerPrincipal(("project_id", "project-1")), out _));
        Assert.False(SessionIdentityClaims.TryGetScope(IdentityBrokerPrincipal(
            ("project_id", "project-1"), ("project_id", "project-2"), ("run_id", "run-1")), out _));
        Assert.False(SessionIdentityClaims.TryGetScope(
            IdentityBrokerPrincipal(("project_id", " "), ("run_id", "run-1")), out _));
        Assert.False(SessionIdentityClaims.TryGetScope(IdentityBrokerPrincipal(), out _));
        Assert.False(SessionIdentityClaims.TryGetScope(IdentityBrokerPrincipal(
            ("sub", "44444444-4444-4444-4444-444444444444"),
            ("project_id", "project-1"), ("run_id", "run-1")), out _));
    }

    [Fact]
    public void IdentityBrokerPrincipalProfileNeedsNoTenantOrRoleClaims()
    {
        var principal = IdentityBrokerPrincipal(("project_id", "project-1"), ("run_id", "run-1"));

        Assert.True(principal.Identity!.IsAuthenticated);
        Assert.Equal("33333333-3333-3333-3333-333333333333",
            principal.FindFirst(Claims.Subject)?.Value);
        Assert.True(SessionIdentityClaims.TryGetScope(principal, out var scope));
        Assert.Equal(new SessionRunScope("project-1", "run-1"), scope!.Value);
        Assert.DoesNotContain(principal.Claims, claim =>
            claim.Type is "tenant_id" or Claims.Role ||
            claim.Value is "platform_admin" or "orchestrator");
    }

    [Fact]
    public void ExistingProviderCatalogAndResolverControlSessionsSelectionAndPinning()
    {
        var provider = new NativePostgresSessionsProvider();
        var options = Options();
        var registration = provider.CreateRegistration(options);
        Assert.Equal(ProviderSeam.Sessions, registration.Descriptor.Seam);
        Assert.Equal(NativePostgresSessionsProvider.ProviderId, registration.Descriptor.Id);
        Assert.Equal(SessionsCapabilities.All, registration.Descriptor.AdvertisedCapabilities);
        Assert.Contains(SessionsCapabilities.PolicyEvaluations, registration.Descriptor.AdvertisedCapabilities);
        Assert.Contains(SessionsCapabilities.Fork, registration.Descriptor.AdvertisedCapabilities);
        var policyRequest = new ProviderResolutionRequest(
            ProviderSeam.Sessions,
            null,
            NativePostgresSessionsProvider.AdapterVersion,
            NativePostgresSessionsProvider.OptionsSchemaVersion,
            ImmutableHashSet.Create(StringComparer.Ordinal, SessionsCapabilities.PolicyEvaluations));
        Assert.Equal("options-2026-10", registration.OptionsRevision);

        var catalog = Assert.IsType<ProviderCatalog>(ProviderCatalog.Create(
            [registration],
            [new ProviderSelection(ProviderSeam.Sessions, NativePostgresSessionsProvider.ProviderId)],
            [new ProviderOverridePermission(ProviderSeam.Sessions, NativePostgresSessionsProvider.ProviderId)]).Value);
        var resolver = new ProviderResolver(catalog);
        var availablePolicy = resolver.Resolve(policyRequest);
        Assert.True(availablePolicy.IsSuccess);
        var defaultCandidate = resolver.Resolve(Request()).Value!.Candidate!;
        var overrideCandidate = resolver.Resolve(Request(NativePostgresSessionsProvider.ProviderId)).Value!.Candidate!;
        Assert.Equal(defaultCandidate.ProviderId, overrideCandidate.ProviderId);

        var deniedCatalog = Assert.IsType<ProviderCatalog>(ProviderCatalog.Create(
            [registration],
            [new ProviderSelection(ProviderSeam.Sessions, NativePostgresSessionsProvider.ProviderId)],
            []).Value);
        var denied = new ProviderResolver(deniedCatalog).Resolve(Request(NativePostgresSessionsProvider.ProviderId));
        Assert.False(denied.IsSuccess);
        Assert.Equal(ProviderErrorCode.OverrideNotPermitted, denied.Error!.Code);

        var negotiated = new ResourceNegotiation(
            new ProviderResourceRef(ProviderSeam.Sessions, defaultCandidate.ProviderId, "db-resource", 3),
            SessionsCapabilities.All);
        var pinned = resolver.Pin("run-1", defaultCandidate, "db-resource", negotiated);
        Assert.True(pinned.IsSuccess);
        Assert.Equal("options-2026-10", pinned.Value!.OptionsRevision);
        Assert.Equal(new Version(1, 0, 0), pinned.Value.AdapterVersion);
        Assert.Equal(3, pinned.Value.Resource.Generation);

        Assert.Equal(ProviderErrorCode.ResourceMismatch,
            resolver.Pin("run-1", defaultCandidate, "another-db", negotiated).Error!.Code);
        Assert.Equal(ProviderErrorCode.CapabilityUnavailable,
            resolver.Pin("run-1", defaultCandidate, "db-resource",
                negotiated with { Capabilities = ImmutableHashSet<string>.Empty }).Error!.Code);

        var changedCatalog = Assert.IsType<ProviderCatalog>(ProviderCatalog.Create(
            [registration with { OptionsRevision = "options-2027-01" }],
            [new ProviderSelection(ProviderSeam.Sessions, NativePostgresSessionsProvider.ProviderId)],
            []).Value);
        Assert.Equal("options-2026-10", pinned.Value.OptionsRevision);
        Assert.Equal("options-2027-01",
            new ProviderResolver(changedCatalog).Resolve(Request()).Value!.Candidate!.OptionsRevision);
    }

    [Fact]
    public void ProviderOptionsAreTypedBoundedAndValidated()
    {
        var invalid = Options() with { PollIntervalMilliseconds = 1 };
        Assert.Throws<ArgumentException>(invalid.Validate);
        Assert.Throws<ArgumentException>(() =>
            new NativePostgresMessagingProviderOptions("messaging-v1", ClaimLeaseSeconds: 1).Validate());
    }

    [Fact]
    public void MessagingProviderIsPlatformSingletonAndCannotBeProjectOverridden()
    {
        var provider = new NativePostgresMessagingProvider();
        var options = new NativePostgresMessagingProviderOptions("messaging-v1");
        var registration = provider.CreateRegistration(options);
        Assert.Equal(ProviderSeam.Messaging, registration.Descriptor.Seam);
        Assert.Equal(AddressedMessageCapabilities.All, registration.Descriptor.AdvertisedCapabilities);

        var catalog = Assert.IsType<ProviderCatalog>(ProviderCatalog.Create(
            [registration],
            [new ProviderSelection(ProviderSeam.Messaging, NativePostgresMessagingProvider.ProviderId)],
            []).Value);
        var resolver = new ProviderResolver(catalog);
        var defaultResult = resolver.Resolve(new ProviderResolutionRequest(
            ProviderSeam.Messaging,
            null,
            NativePostgresMessagingProvider.AdapterVersion,
            NativePostgresMessagingProvider.OptionsSchemaVersion,
            AddressedMessageCapabilities.All));
        Assert.True(defaultResult.IsSuccess);

        var overrideResult = resolver.Resolve(new ProviderResolutionRequest(
            ProviderSeam.Messaging,
            NativePostgresMessagingProvider.ProviderId,
            NativePostgresMessagingProvider.AdapterVersion,
            NativePostgresMessagingProvider.OptionsSchemaVersion,
            AddressedMessageCapabilities.All));
        Assert.False(overrideResult.IsSuccess);
        Assert.Equal(ProviderErrorCode.OverrideNotPermitted, overrideResult.Error!.Code);
    }

    private static SessionObjectReference Ref(string key) =>
        new(new ObjectKey(key), "transcript", 10);

    private static ProducedRunCaptureSessionPayload CapturePayload()
    {
        var identity = new SessionIdentity("project-1", "run-1", "session-1");
        var acceptedSelectionHash = new string('A', 64);
        var manifestHash = new string('c', 64);
        var captureIdentity = ProducedRunCaptureContractValidation.CreateIdentity(
            identity,
            "pin-1",
            acceptedSelectionHash,
            "workspace-1",
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            "repo-1",
            1,
            new string('a', 40),
            new string('b', 40),
            manifestHash);
        var proof = new ProducedRunCaptureProof(
            ProducedRunCaptureLimits.ContractVersion,
            identity,
            captureIdentity.CaptureId,
            captureIdentity.EventId,
            "https://identity.test",
            "actor-1",
            "tenant-1",
            "pin-1",
            acceptedSelectionHash,
            "workspace-1",
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            "repo-1",
            1,
            new string('a', 40),
            new string('b', 40),
            manifestHash,
            1,
            new string('d', 64),
            0,
            new string('e', 64),
            ProducedRunCaptureLimits.PackageHeaderBytes,
            DateTimeOffset.UnixEpoch);
        return new(proof, ProducedRunCaptureContractValidation.CreatePackageReference(proof));
    }

    private static PolicyEvaluationSessionPayload PolicyEvaluation() =>
        new(
            "33333333-3333-3333-3333-333333333333",
            "tenant-1",
            "step-1",
            "grant-1",
            "revision-1",
            "coordination.decision",
            "coordinator.question.respond",
            PolicyEvaluationOutcome.Allow,
            PolicyEvaluationReasonCode.Allowed,
            1,
            "agt.default",
            "1.0.0",
            1,
            "options-2026-10");

    private static ClaimsPrincipal IdentityBrokerPrincipal(params (string Type, string Value)[] claims)
    {
        var identity = new ClaimsIdentity("identity-broker-profile", Claims.Name, Claims.Role);
        identity.AddClaim(new Claim(Claims.Subject, "33333333-3333-3333-3333-333333333333"));
        identity.AddClaims(claims.Select(claim => new Claim(claim.Type, claim.Value)));
        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(["openid"]);
        principal.SetResources(["agentweaver.events"]);
        return principal;
    }

    private static PostgresSessionsProviderOptions Options() =>
        new("db-resource", "agentweaver", 3, "events_sessions", "options-2026-10");

    private static ProviderResolutionRequest Request(string? projectOverride = null) =>
        new(ProviderSeam.Sessions, projectOverride, NativePostgresSessionsProvider.AdapterVersion,
            NativePostgresSessionsProvider.OptionsSchemaVersion, SessionsCapabilities.All);
}
