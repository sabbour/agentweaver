using System.Collections.Immutable;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Agentweaver.Providers;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class GuardrailsContentGateTests
{
    private const string ActionId = "remote.tool.call";
    private static readonly Version AdapterVersion = new(1, 0, 0);

    private const string PlatformAllowAction = """
        apiVersion: governance.toolkit/v1
        version: "1.0"
        name: platform-policy
        scope: global
        default_action: deny
        rules:
          - name: allow-remote-tool
            condition: "action_id == 'remote.tool.call'"
            action: allow
            priority: 100
        """;

    private const string PlatformDenyAction = """
        apiVersion: governance.toolkit/v1
        version: "1.0"
        name: platform-policy
        scope: global
        default_action: deny
        rules: []
        """;

    private const string ProjectDenyAction = """
        apiVersion: governance.toolkit/v1
        version: "1.0"
        name: project-policy
        scope: project
        default_action: allow
        rules:
          - name: deny-remote-tool
            condition: "action_id == 'remote.tool.call'"
            action: deny
            priority: 100
        """;

    private const string ProjectAllowAction = """
        apiVersion: governance.toolkit/v1
        version: "1.0"
        name: project-policy
        scope: project
        default_action: allow
        rules: []
        """;

    private static readonly ImmutableHashSet<string> Capabilities =
        GuardrailsProviderCapabilities.PromptShieldsSupported;

    [Fact]
    public async Task ExecutesEveryPinnedCheckInOrderAndJournalsRedactedEvidenceBeforeAllowingRelease()
    {
        var fixture = await CreateFixtureAsync(providerIds: ["first", "second"]);
        var releaseInvoked = false;

        var result = await fixture.Gate.EvaluateAsync(
            fixture.AcceptedRun,
            fixture.Request);
        releaseInvoked = TestReleaseIfAllowed(result, () => releaseInvoked = true);

        Assert.Equal(GuardrailsDecision.Allow, result.Decision);
        Assert.Equal(GuardrailsReasonCode.Allowed, result.ReasonCode);
        Assert.True(releaseInvoked);
        Assert.Equal(new[] { "first", "second" }, fixture.Classifiers.Select(classifier => classifier.ProviderId));
        Assert.Equal(4, fixture.Authority.ReadCount);
        var contentDigest = Convert.ToHexString(
            SHA256.HashData(fixture.Request.CanonicalContent.AsSpan()));
        Assert.All(fixture.Authority.Requests, authorityRequest =>
        {
            Assert.Equal(fixture.Request.RemoteToolCall, authorityRequest.RemoteToolCall);
            Assert.Equal(fixture.Request.SnapshotDigest, authorityRequest.SnapshotDigest);
            Assert.Equal(contentDigest, authorityRequest.ContentDigest);
            Assert.DoesNotContain("sensitive prompt fixture", JsonSerializer.Serialize(authorityRequest));
        });
        var classifierRequest = Assert.Single(fixture.Classifiers[0].Calls);
        Assert.Equal(contentDigest, classifierRequest.ContentDigest);
        Assert.Equal(
            fixture.Request.CanonicalContent.ToArray(),
            classifierRequest.CanonicalContent.ToArray());

        var evidence = Assert.Single(fixture.Journal.Evidence);
        Assert.Equal(contentDigest, evidence.ContentDigest);
        Assert.Equal(new[] { "first", "second" }, evidence.Checks.Select(check => check.ProviderId));
        Assert.Equal(new[] { 0, 1 }, evidence.Checks.Select(check => check.Order));
        Assert.Equal(new[] { "options-first", "options-second" },
            evidence.Checks.Select(check => check.OptionsRevision));
        Assert.DoesNotContain("sensitive prompt fixture", JsonSerializer.Serialize(evidence));
    }

    [Fact]
    public async Task ClassifierFindingsAreVersionedAndAnAgtDenyWithholds()
    {
        var fixture = await CreateFixtureAsync(
            projectPolicy: ProjectDenyAction,
            outcomes:
            [
                GuardrailsClassifierResult.Detected(
                    [new GuardrailsFinding("prompt_injection", GuardrailsFindingSeverity.High)])
            ]);
        var releaseInvoked = false;

        var result = await fixture.Gate.EvaluateAsync(
            fixture.AcceptedRun,
            fixture.Request);
        releaseInvoked = TestReleaseIfAllowed(result, () => releaseInvoked = true);

        Assert.Equal(GuardrailsDecision.Withhold, result.Decision);
        Assert.Equal(GuardrailsReasonCode.PolicyDenied, result.ReasonCode);
        Assert.Equal(PolicyEvaluationOutcome.Deny, result.PolicyOutcome);
        Assert.False(releaseInvoked);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("prompt_injection", finding.Code);
        Assert.Equal("check", finding.ProviderId);
        Assert.Equal(AdapterVersion.ToString(), finding.AdapterVersion);
        Assert.Equal(1, finding.OptionsSchemaVersion);
        Assert.Equal("options-check", finding.OptionsRevision);
        Assert.Equal(PolicyEvaluationOutcome.Deny, Assert.Single(fixture.Journal.Evidence).PolicyOutcome);
    }

    [Fact]
    public void GuardrailsFindingCannotPromoteAnExistingPolicyDeny()
    {
        var denied = new AgtPolicyEvaluationResult(
            PolicyEvaluationOutcome.Deny,
            PolicyEvaluationReasonCode.ProjectRuleNarrowed,
            AgtPolicyProvider.ProviderId,
            AgtPolicyProvider.AdapterVersion,
            AgtPolicyProvider.OptionsSchemaVersion,
            "policy-revision-1");
        var allowedWithFinding = denied with
        {
            Outcome = PolicyEvaluationOutcome.Allow,
            ReasonCode = PolicyEvaluationReasonCode.Allowed
        };

        Assert.Same(denied, GuardrailsPolicyNarrowing.Apply(denied, allowedWithFinding));
    }

    [Fact]
    public async Task PlatformDenyCannotBeOverriddenByProjectPolicyOrClearClassifier()
    {
        var fixture = await CreateFixtureAsync(
            platformPolicy: PlatformDenyAction,
            projectPolicy: ProjectAllowAction);
        var releaseInvoked = false;

        var result = await fixture.Gate.EvaluateAsync(
            fixture.AcceptedRun,
            fixture.Request);
        releaseInvoked = TestReleaseIfAllowed(result, () => releaseInvoked = true);

        Assert.Equal(GuardrailsDecision.Withhold, result.Decision);
        Assert.Equal(GuardrailsReasonCode.PolicyDenied, result.ReasonCode);
        Assert.False(releaseInvoked);
    }

    [Fact]
    public async Task MissingStageContentPinWithholdsBeforeClassification()
    {
        var fixture = await CreateFixtureAsync();
        var request = fixture.Request with
        {
            Stage = GuardrailsContentStage.ToolResult,
            ContentClass = GuardrailsContentClass.StructuredJson
        };
        var releaseInvoked = false;

        var result = await fixture.Gate.EvaluateAsync(
            fixture.AcceptedRun,
            request);
        releaseInvoked = TestReleaseIfAllowed(result, () => releaseInvoked = true);

        Assert.Equal(GuardrailsDecision.Withhold, result.Decision);
        Assert.Equal(GuardrailsReasonCode.UnsupportedStageOrContent, result.ReasonCode);
        Assert.Empty(fixture.Classifiers[0].Calls);
        Assert.Equal(0, fixture.Journal.AppendCalls);
        Assert.False(releaseInvoked);
    }

    [Fact]
    public async Task InvalidTypedOptionsAndMissingAdaptersFailBeforeClassification()
    {
        var fixture = await CreateFixtureAsync(requestTimeout: TimeSpan.Zero);
        var releaseInvoked = false;

        var result = await fixture.Gate.EvaluateAsync(
            fixture.AcceptedRun,
            fixture.Request);
        releaseInvoked = TestReleaseIfAllowed(result, () => releaseInvoked = true);

        Assert.Equal(GuardrailsDecision.Withhold, result.Decision);
        Assert.Equal(GuardrailsReasonCode.InvalidConfiguration, result.ReasonCode);
        Assert.Empty(fixture.Classifiers[0].Calls);
        Assert.Equal(0, fixture.Journal.AppendCalls);
        Assert.False(releaseInvoked);

        var missing = await CreateFixtureAsync();
        var acceptedWithoutChecks = missing.AcceptedRun with
        {
            StagePlans =
            [
                missing.AcceptedRun.StagePlans[0] with
                {
                    Checks = []
                }
            ]
        };
        var missingResult = await missing.Gate.EvaluateAsync(
            acceptedWithoutChecks,
            missing.Request);

        Assert.Equal(GuardrailsDecision.Withhold, missingResult.Decision);
        Assert.Equal(GuardrailsReasonCode.InvalidConfiguration, missingResult.ReasonCode);
        Assert.Empty(missing.Classifiers[0].Calls);
    }

    [Fact]
    public async Task NullPolicyOptionsAndDefaultOrderedBindingsWithholdAsInvalidConfiguration()
    {
        var nullPolicyOptions = await CreateFixtureAsync();
        var nullOptionsResult = await nullPolicyOptions.Gate.EvaluateAsync(
            nullPolicyOptions.AcceptedRun with { PolicyOptions = null! },
            nullPolicyOptions.Request);

        Assert.Equal(GuardrailsDecision.Withhold, nullOptionsResult.Decision);
        Assert.Equal(GuardrailsReasonCode.InvalidConfiguration, nullOptionsResult.ReasonCode);
        Assert.Empty(nullPolicyOptions.Classifiers[0].Calls);
        Assert.Equal(0, nullPolicyOptions.Authority.ReadCount);

        var defaultOrderedBindings = await CreateFixtureAsync();
        var stagePlan = defaultOrderedBindings.AcceptedRun.StagePlans[0] with
        {
            Binding = CreateDefaultOrderedBinding()
        };
        var defaultBindingsResult = await defaultOrderedBindings.Gate.EvaluateAsync(
            defaultOrderedBindings.AcceptedRun with { StagePlans = [stagePlan] },
            defaultOrderedBindings.Request);

        Assert.Equal(GuardrailsDecision.Withhold, defaultBindingsResult.Decision);
        Assert.Equal(GuardrailsReasonCode.InvalidConfiguration, defaultBindingsResult.ReasonCode);
        Assert.Empty(defaultOrderedBindings.Classifiers[0].Calls);
        Assert.Equal(0, defaultOrderedBindings.Authority.ReadCount);
    }

    [Theory]
    [InlineData(ProviderSeam.Policy, "run-1", 1)]
    [InlineData(ProviderSeam.Guardrails, "run-other", 1)]
    [InlineData(ProviderSeam.Guardrails, "run-1", 2)]
    public async Task ClassifierPinMustMatchTheAcceptedOrderedPinAndResourceGeneration(
        ProviderSeam seam,
        string runId,
        long generation)
    {
        var fixture = await CreateFixtureAsync();
        var mismatchedPin = PinForTests(seam, runId, generation);
        var originalCheck = fixture.AcceptedRun.StagePlans[0].Checks[0];
        var replacementCheck = new GuardrailsClassifierBinding<TestOptions>(
            mismatchedPin,
            Assert.IsType<TestOptions>(originalCheck.Options),
            fixture.Classifiers[0]);
        var stagePlan = fixture.AcceptedRun.StagePlans[0] with
        {
            Checks = [replacementCheck]
        };

        var result = await fixture.Gate.EvaluateAsync(
            fixture.AcceptedRun with { StagePlans = [stagePlan] },
            fixture.Request);

        Assert.Equal(GuardrailsDecision.Withhold, result.Decision);
        Assert.Equal(GuardrailsReasonCode.InvalidConfiguration, result.ReasonCode);
        Assert.Empty(fixture.Classifiers[0].Calls);
        Assert.Equal(0, fixture.Authority.ReadCount);
    }

    [Theory]
    [InlineData("seam")]
    [InlineData("provider")]
    [InlineData("generation")]
    public async Task OrderedPinResourceMustMatchItsSeamProviderAndPositiveGeneration(string invalidField)
    {
        var fixture = await CreateFixtureAsync();
        var pin = fixture.AcceptedRun.StagePlans[0].Binding.Bindings[0];
        var resource = invalidField switch
        {
            "seam" => new ProviderResourceRef(ProviderSeam.Policy, pin.ProviderId, pin.Resource.ResourceId, 1),
            "provider" => new ProviderResourceRef(pin.Seam, "different-provider", pin.Resource.ResourceId, 1),
            "generation" => new ProviderResourceRef(pin.Seam, pin.ProviderId, pin.Resource.ResourceId, 0),
            _ => throw new ArgumentOutOfRangeException(nameof(invalidField))
        };
        SetBackingField(pin, "Resource", resource);

        var result = await fixture.Gate.EvaluateAsync(
            fixture.AcceptedRun,
            fixture.Request);

        Assert.Equal(GuardrailsDecision.Withhold, result.Decision);
        Assert.Equal(GuardrailsReasonCode.InvalidConfiguration, result.ReasonCode);
        Assert.Empty(fixture.Classifiers[0].Calls);
        Assert.Equal(0, fixture.Authority.ReadCount);
    }

    [Theory]
    [InlineData("seam")]
    [InlineData("provider")]
    [InlineData("generation")]
    public async Task PolicyPinResourceMustMatchItsSeamProviderAndOptionsGeneration(string invalidField)
    {
        var fixture = await CreateFixtureAsync();
        var pin = fixture.AcceptedRun.PolicyBinding;
        var resource = invalidField switch
        {
            "seam" => new ProviderResourceRef(
                ProviderSeam.Guardrails, pin.ProviderId, pin.Resource.ResourceId, pin.Resource.Generation),
            "provider" => new ProviderResourceRef(
                pin.Seam, "different-provider", pin.Resource.ResourceId, pin.Resource.Generation),
            "generation" => new ProviderResourceRef(
                pin.Seam, pin.ProviderId, pin.Resource.ResourceId, pin.Resource.Generation + 1),
            _ => throw new ArgumentOutOfRangeException(nameof(invalidField))
        };
        SetBackingField(pin, "Resource", resource);

        var result = await fixture.Gate.EvaluateAsync(
            fixture.AcceptedRun,
            fixture.Request);

        Assert.Equal(GuardrailsDecision.Withhold, result.Decision);
        Assert.Equal(GuardrailsReasonCode.InvalidConfiguration, result.ReasonCode);
        Assert.Empty(fixture.Classifiers[0].Calls);
        Assert.Equal(0, fixture.Authority.ReadCount);
    }

    [Fact]
    public async Task MissingNegotiatedCapabilitiesWithholdBeforeSetComparison()
    {
        var fixture = await CreateFixtureAsync();
        SetBackingField(
            fixture.AcceptedRun.StagePlans[0].Binding.Bindings[0],
            "NegotiatedCapabilities",
            null);

        var result = await fixture.Gate.EvaluateAsync(
            fixture.AcceptedRun,
            fixture.Request);

        Assert.Equal(GuardrailsDecision.Withhold, result.Decision);
        Assert.Equal(GuardrailsReasonCode.InvalidConfiguration, result.ReasonCode);
        Assert.Empty(fixture.Classifiers[0].Calls);
        Assert.Equal(0, fixture.Authority.ReadCount);
    }

    [Fact]
    public async Task ClassifierTimeoutAndTypedFailureAreJournaledAndWithheld()
    {
        var timedOut = await CreateFixtureAsync(
            requestTimeout: TimeSpan.FromMilliseconds(10),
            classifier: async (_, _, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return GuardrailsClassifierResult.Clear();
            });

        var timeoutResult = await timedOut.Gate.EvaluateAsync(
            timedOut.AcceptedRun,
            timedOut.Request);

        Assert.Equal(GuardrailsDecision.Withhold, timeoutResult.Decision);
        Assert.Equal(GuardrailsReasonCode.ClassifierTimedOut, timeoutResult.ReasonCode);
        Assert.Equal(3, timedOut.Authority.ReadCount);
        Assert.Equal(GuardrailsCheckOutcome.Failed, Assert.Single(Assert.Single(timedOut.Journal.Evidence).Checks).Outcome);

        var failed = await CreateFixtureAsync(
            classifier: (_, _, _) => Task.FromResult(
                GuardrailsClassifierResult.Failed(GuardrailsClassifierFailure.Unavailable)));

        var failedResult = await failed.Gate.EvaluateAsync(
            failed.AcceptedRun,
            failed.Request);

        Assert.Equal(GuardrailsDecision.Withhold, failedResult.Decision);
        Assert.Equal(GuardrailsReasonCode.ProviderUnavailable, failedResult.ReasonCode);
        Assert.Equal(3, failed.Authority.ReadCount);
    }

    [Fact]
    public async Task ProviderTransportFailureDoesNotLeakExceptionTextOrAllowRelease()
    {
        var fixture = await CreateFixtureAsync(
            classifier: (_, _, _) => throw new HttpRequestException("secret endpoint and token"));

        var result = await fixture.Gate.EvaluateAsync(
            fixture.AcceptedRun,
            fixture.Request);
        var releaseInvoked = false;
        releaseInvoked = TestReleaseIfAllowed(result, () => releaseInvoked = true);

        Assert.Equal(GuardrailsDecision.Withhold, result.Decision);
        Assert.Equal(GuardrailsReasonCode.ProviderUnavailable, result.ReasonCode);
        Assert.False(releaseInvoked);
        Assert.DoesNotContain("secret endpoint and token",
            JsonSerializer.Serialize(Assert.Single(fixture.Journal.Evidence)));
    }

    [Fact]
    public async Task AuthorityIsRecheckedAfterClassifierAndAfterJournalAcknowledgment()
    {
        var revokedAfterClassifier = await CreateFixtureAsync(
            authorityStatuses:
            [
                GuardrailsAuthorityStatus.Current,
                GuardrailsAuthorityStatus.Revoked
            ]);
        var firstResult = await revokedAfterClassifier.Gate.EvaluateAsync(
            revokedAfterClassifier.AcceptedRun,
            revokedAfterClassifier.Request);

        Assert.Equal(GuardrailsReasonCode.AuthorityRevoked, firstResult.ReasonCode);
        Assert.Equal(0, revokedAfterClassifier.Journal.AppendCalls);

        var staleAfterJournal = await CreateFixtureAsync(
            authorityStatuses:
            [
                GuardrailsAuthorityStatus.Current,
                GuardrailsAuthorityStatus.Current,
                GuardrailsAuthorityStatus.Stale
            ]);
        var secondResult = await staleAfterJournal.Gate.EvaluateAsync(
            staleAfterJournal.AcceptedRun,
            staleAfterJournal.Request);

        Assert.Equal(GuardrailsReasonCode.AuthorityStale, secondResult.ReasonCode);
        Assert.Equal(1, staleAfterJournal.Journal.AppendCalls);
    }

    [Fact]
    public async Task JournalFailureAndDuplicateResponseNeverReleaseContent()
    {
        var unavailable = await CreateFixtureAsync();
        unavailable.Journal.ForcedStatus = GuardrailsJournalAppendStatus.Unavailable;
        var unavailableResult = await unavailable.Gate.EvaluateAsync(
            unavailable.AcceptedRun,
            unavailable.Request);

        Assert.Equal(GuardrailsReasonCode.JournalUnavailable, unavailableResult.ReasonCode);

        var duplicate = await CreateFixtureAsync();
        duplicate.Journal.ForcedStatus = GuardrailsJournalAppendStatus.Duplicate;
        var duplicateResult = await duplicate.Gate.EvaluateAsync(
            duplicate.AcceptedRun,
            duplicate.Request);

        Assert.Equal(GuardrailsReasonCode.JournalDuplicate, duplicateResult.ReasonCode);
    }

    [Fact]
    public async Task ResponseLossAndRestartReplayDoNotReleaseTwice()
    {
        var fixture = await CreateFixtureAsync();
        fixture.Journal.LoseAcknowledgmentAfterFirstAppend = true;
        var firstAttempt = await fixture.Gate.EvaluateAsync(
            fixture.AcceptedRun,
            fixture.Request);

        Assert.Equal(GuardrailsDecision.Withhold, firstAttempt.Decision);
        Assert.Equal(GuardrailsReasonCode.JournalUnavailable, firstAttempt.ReasonCode);
        Assert.Single(fixture.Journal.Evidence);

        fixture.Journal.LoseAcknowledgmentAfterFirstAppend = false;
        var restartedGate = new GuardrailsContentGate(
            fixture.PolicyProvider, fixture.Authority, fixture.Journal);
        var replay = await restartedGate.EvaluateAsync(
            fixture.AcceptedRun,
            fixture.Request);

        Assert.Equal(GuardrailsReasonCode.JournalDuplicate, replay.ReasonCode);
    }

    [Fact]
    public async Task DisabledProviderCannotProduceAnAcceptedOrderedStagePin()
    {
        var enabled = Registration("enabled", enabled: true);
        var disabled = Registration("disabled", enabled: false);
        var catalog = Assert.IsType<ProviderCatalog>(ProviderCatalog.Create(
            [enabled, disabled],
            [],
            [new ProviderOverridePermission(ProviderSeam.Guardrails, "disabled")],
            [new ProviderOrderedSelection(ProviderSeam.Guardrails, ["enabled"])]).Value);
        var resolver = new ProviderResolver(catalog);
        var request = new OrderedProviderResolutionRequest(
            ProviderSeam.Guardrails,
            ["enabled", "disabled"],
            AdapterVersion,
            1,
            RequiredCapabilities());

        var resolution = resolver.ResolveOrdered(request);

        Assert.False(resolution.IsSuccess);
        Assert.Equal(ProviderErrorCode.ProviderDisabled, resolution.Error!.Code);
    }

    private static async Task<Fixture> CreateFixtureAsync(
        string platformPolicy = PlatformAllowAction,
        string? projectPolicy = null,
        ImmutableArray<string> providerIds = default,
        ImmutableArray<GuardrailsClassifierResult> outcomes = default,
        ImmutableArray<GuardrailsAuthorityStatus> authorityStatuses = default,
        TimeSpan? requestTimeout = null,
        Func<string, GuardrailsClassifierRequest, CancellationToken, Task<GuardrailsClassifierResult>>? classifier = null)
    {
        if (providerIds.IsDefault)
            providerIds = ["check"];
        if (outcomes.IsDefault)
            outcomes = providerIds.Select(_ => GuardrailsClassifierResult.Clear()).ToImmutableArray();
        if (outcomes.Length != providerIds.Length)
            throw new ArgumentException("The test requires one classifier outcome per provider.", nameof(outcomes));

        var registrations = providerIds.Select(id => Registration(id, enabled: true)).ToArray();
        var catalog = Assert.IsType<ProviderCatalog>(ProviderCatalog.Create(
            registrations,
            [],
            [],
            [new ProviderOrderedSelection(ProviderSeam.Guardrails, providerIds)]).Value);
        var resolver = new ProviderResolver(catalog);
        var requiredCapabilities = RequiredCapabilities();
        var resolution = Assert.IsType<OrderedProviderResolution>(resolver.ResolveOrdered(
            new OrderedProviderResolutionRequest(
                ProviderSeam.Guardrails,
                null,
                AdapterVersion,
                1,
                requiredCapabilities)).Value);
        var providerInputs = providerIds.Select(id =>
        {
            var resource = $"resource-{id}";
            return new ProviderPinInput(
                resource,
                new ResourceNegotiation(
                    new ProviderResourceRef(ProviderSeam.Guardrails, id, resource, 1),
                    Capabilities));
        }).ToArray();
        var orderedPin = Assert.IsType<PinnedOrderedProviderBinding>(
            resolver.PinOrdered("run-1", resolution, providerInputs).Value);

        var classifiers = new List<ControlledClassifier>();
        var checks = ImmutableArray.CreateBuilder<IGuardrailsClassifierBinding>();
        for (var index = 0; index < providerIds.Length; index++)
        {
            var id = providerIds[index];
            var options = new TestOptions(
                id,
                AdapterVersion,
                1,
                $"options-{id}",
                requestTimeout ?? TimeSpan.FromSeconds(1));
            var outcome = outcomes[index];
            var controlled = new ControlledClassifier(
                id,
                Capabilities,
                classifier is null
                    ? (_, _) => Task.FromResult(outcome)
                    : (request, token) => classifier(id, request, token));
            classifiers.Add(controlled);
            checks.Add(new GuardrailsClassifierBinding<TestOptions>(
                orderedPin.Bindings[index], options, controlled));
        }

        var policyProvider = new AgtPolicyProvider();
        var policyOptions = new AgtPolicyProviderOptions(
            "policy-resource",
            2,
            "policy-revision-1",
            [platformPolicy]);
        var policyCatalog = Assert.IsType<ProviderCatalog>(ProviderCatalog.Create(
            [policyProvider.CreateRegistration(policyOptions)],
            [new ProviderSelection(ProviderSeam.Policy, AgtPolicyProvider.ProviderId)],
            []).Value);
        var policyPinResult = await policyProvider.ResolveNegotiateAndPinAsync(
            new ProviderResolver(policyCatalog), policyOptions, "run-1");
        var policyPin = Assert.IsType<PinnedProviderBinding>(policyPinResult.Value);
        var authority = new RecordingAuthority(authorityStatuses);
        var journal = new RecordingJournal();
        var stagePlan = new GuardrailsAcceptedStagePlan(
            GuardrailsContentStage.ToolResult,
            GuardrailsContentClass.Text,
            orderedPin,
            checks.ToImmutable());
        var acceptedRun = new GuardrailsAcceptedRunBinding(
            "https://identity.test/",
            "actor-1",
            "tenant-1",
            "project-1",
            "run-1",
            "session-1",
            ActionId,
            "remote-tool-result",
            "selection-revision-1",
            new string('A', 64),
            "grant-1",
            "grant-revision-1",
            7,
            policyPin,
            policyOptions,
            projectPolicy is null ? [] : [projectPolicy],
            [stagePlan]);
        var contentRequest = new GuardrailsContentRequest(
            Guid.NewGuid(),
            "snapshot-1",
            new string('B', 64),
            GuardrailsContentStage.ToolResult,
            GuardrailsContentClass.Text,
            new GuardrailsRemoteToolCallIdentity(
                "agent-1",
                "node-1",
                "mcp.weather",
                new string('C', 64),
                new string('D', 64),
                new string('E', 64)),
            ImmutableArray.CreateRange(Encoding.UTF8.GetBytes("sensitive prompt fixture")));

        return new(
            new GuardrailsContentGate(policyProvider, authority, journal),
            policyProvider,
            authority,
            journal,
            acceptedRun,
            contentRequest,
            classifiers);
    }

    private static ProviderRegistration Registration(string id, bool enabled) =>
        new(
            new ProviderDescriptor(
                ProviderSeam.Guardrails,
                id,
                AdapterVersion,
                1,
                ProviderHostingPattern.InProcess,
                Capabilities),
            enabled,
            $"options-{id}",
            1);

    private static PinnedProviderBinding PinForTests(
        ProviderSeam seam,
        string runId,
        long generation)
    {
        const string providerId = "check";
        const string resourceId = "resource-check";
        var registration = new ProviderRegistration(
            new ProviderDescriptor(
                seam,
                providerId,
                AdapterVersion,
                1,
                ProviderHostingPattern.InProcess,
                Capabilities),
            true,
            "options-check",
            1);
        var catalogResult = seam == ProviderSeam.Guardrails
            ? ProviderCatalog.Create(
                [registration],
                [],
                [],
                [new ProviderOrderedSelection(seam, [providerId])])
            : ProviderCatalog.Create(
                [registration],
                [new ProviderSelection(seam, providerId)],
                []);
        var catalog = Assert.IsType<ProviderCatalog>(catalogResult.Value);
        var resolver = new ProviderResolver(catalog);
        var candidate = seam == ProviderSeam.Guardrails
            ? Assert.Single(Assert.IsType<OrderedProviderResolution>(resolver.ResolveOrdered(
                new OrderedProviderResolutionRequest(
                    seam,
                    null,
                    AdapterVersion,
                    1,
                    RequiredCapabilities())).Value).Candidates)
            : Assert.IsType<ProviderCandidate>(resolver.Resolve(new ProviderResolutionRequest(
                seam,
                null,
                AdapterVersion,
                1,
                RequiredCapabilities())).Value!.Candidate);
        var pin = resolver.Pin(
            runId,
            candidate,
            resourceId,
            new ResourceNegotiation(
                new ProviderResourceRef(seam, providerId, resourceId, generation),
                Capabilities));
        return Assert.IsType<PinnedProviderBinding>(pin.Value);
    }

    private static PinnedOrderedProviderBinding CreateDefaultOrderedBinding()
    {
        var constructor = typeof(PinnedOrderedProviderBinding).GetConstructors(
                BindingFlags.Instance | BindingFlags.NonPublic)
            .Single();
        return (PinnedOrderedProviderBinding)constructor.Invoke(
        [
            "run-1",
            ProviderSeam.Guardrails,
            default(ImmutableArray<PinnedProviderBinding>)
        ]);
    }

    private static void SetBackingField(object target, string propertyName, object? value)
    {
        var field = target.GetType().GetField(
            $"<{propertyName}>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(target, value);
    }

    private static ImmutableHashSet<string> RequiredCapabilities() =>
        ImmutableHashSet.Create(
            StringComparer.Ordinal,
            GuardrailsProviderCapabilities.PromptInjectionDetection,
            GuardrailsProviderCapabilities.ToolResultText);

    private static bool TestReleaseIfAllowed(GuardrailsGateResult result, Action release)
    {
        if (result.Decision != GuardrailsDecision.Allow)
            return false;
        release();
        return true;
    }

    private sealed record TestOptions(
        string ProviderId,
        Version AdapterVersion,
        int OptionsSchemaVersion,
        string OptionsRevision,
        TimeSpan RequestTimeout)
        : GuardrailsProviderOptions(
            ProviderId,
            AdapterVersion,
            OptionsSchemaVersion,
            OptionsRevision,
            RequestTimeout);

    private sealed class ControlledClassifier(
        string providerId,
        ImmutableHashSet<string> capabilities,
        Func<GuardrailsClassifierRequest, CancellationToken, Task<GuardrailsClassifierResult>> classify)
        : IGuardrailsClassifier<TestOptions>
    {
        public string ProviderId { get; } = providerId;
        public Version AdapterVersion => GuardrailsContentGateTests.AdapterVersion;
        public int OptionsSchemaVersion => 1;
        public ImmutableHashSet<string> Capabilities { get; } = capabilities;
        public List<GuardrailsClassifierRequest> Calls { get; } = [];

        public Task<GuardrailsClassifierResult> ClassifyAsync(
            TestOptions options,
            GuardrailsClassifierRequest request,
            CancellationToken cancellationToken)
        {
            Calls.Add(request);
            return classify(request, cancellationToken);
        }
    }

    private sealed class RecordingAuthority(ImmutableArray<GuardrailsAuthorityStatus> statuses)
        : IGuardrailsCurrentAuthority
    {
        private readonly Queue<GuardrailsAuthorityStatus> _statuses =
            statuses.IsDefault ? new() : new(statuses);

        public int ReadCount { get; private set; }
        public List<GuardrailsAuthorityCheckRequest> Requests { get; } = [];

        public Task<GuardrailsAuthorityStatus> CheckCurrentAsync(
            GuardrailsAuthorityCheckRequest request,
            CancellationToken cancellationToken)
        {
            ReadCount++;
            Requests.Add(request);
            var status = _statuses.Count == 0
                ? GuardrailsAuthorityStatus.Current
                : _statuses.Dequeue();
            return Task.FromResult(status);
        }
    }

    private sealed class RecordingJournal : IGuardrailsEvidenceJournal
    {
        private readonly HashSet<Guid> _operationIds = [];

        public List<GuardrailsEvaluationEvidence> Evidence { get; } = [];
        public int AppendCalls { get; private set; }
        public GuardrailsJournalAppendStatus? ForcedStatus { get; set; }
        public bool LoseAcknowledgmentAfterFirstAppend { get; set; }

        public Task<GuardrailsJournalAppendStatus> AppendAsync(
            GuardrailsEvaluationEvidence evidence,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppendCalls++;
            if (ForcedStatus is { } forced)
                return Task.FromResult(forced);
            if (!_operationIds.Add(evidence.OperationId))
                return Task.FromResult(GuardrailsJournalAppendStatus.Duplicate);

            Evidence.Add(evidence);
            if (LoseAcknowledgmentAfterFirstAppend && AppendCalls == 1)
                return Task.FromResult(GuardrailsJournalAppendStatus.Unavailable);
            return Task.FromResult(GuardrailsJournalAppendStatus.Appended);
        }
    }

    private sealed record Fixture(
        GuardrailsContentGate Gate,
        AgtPolicyProvider PolicyProvider,
        RecordingAuthority Authority,
        RecordingJournal Journal,
        GuardrailsAcceptedRunBinding AcceptedRun,
        GuardrailsContentRequest Request,
        List<ControlledClassifier> Classifiers);
}
