using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Agentweaver.Providers;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class GuardrailsStagePlanResolverTests
{
    private const string ShieldsId = PromptShieldsClassifier.DefaultProviderId;
    private const string SecondId = "second-check";
    private static readonly Version Adapter = new(1, 0, 0);

    // Minimal platform policy: allow model turns, but deny any turn that has Guardrails findings.
    private const string PlatformAllowModelTurn = """
        apiVersion: governance.toolkit/v1
        version: "1.0"
        name: platform-policy
        scope: global
        default_action: deny
        rules:
          - name: deny-guardrails-findings
            condition: "guardrails_finding_count > 0"
            action: deny
            priority: 200
          - name: allow-model-turn
            condition: "action_id == 'model.turn'"
            action: allow
            priority: 100
        """;

    [Fact]
    public void ResolvesPlatformOrderAndPinsEveryCheck()
    {
        var resolver = Resolver([Catalog(ShieldsId, true), Catalog(SecondId, true)], [ShieldsId, SecondId]);

        var result = resolver.Resolve("run-1", GuardrailsContentStage.UserInput, GuardrailsContentClass.Text, null);

        Assert.True(result.IsSuccess);
        var plan = result.Plan!;
        Assert.Equal(new[] { ShieldsId, SecondId }, plan.Binding.Bindings.Select(pin => pin.ProviderId));
        Assert.All(plan.Binding.Bindings, pin =>
        {
            Assert.Equal("run-1", pin.RunId);
            Assert.Contains(GuardrailsProviderCapabilities.UserInputText, pin.NegotiatedCapabilities);
            Assert.Contains(GuardrailsProviderCapabilities.PromptInjectionDetection, pin.NegotiatedCapabilities);
        });
        Assert.Equal(new[] { "options-" + ShieldsId, "options-" + SecondId },
            plan.Binding.Bindings.Select(pin => pin.OptionsRevision));
        Assert.Equal(2, plan.Checks.Length);
    }

    [Fact]
    public void AcceptedChecksMatchingPlatformOrderResolve()
    {
        var resolver = Resolver([Catalog(ShieldsId, true), Catalog(SecondId, true)], [ShieldsId, SecondId]);

        var result = resolver.Resolve("run-1", GuardrailsContentStage.UserInput, GuardrailsContentClass.Text,
            [Accepted(ShieldsId), Accepted(SecondId)]);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void ProjectCannotReorderOrAddChecks()
    {
        // "extra" is enabled in the catalog but isn't in the platform order, so a project can't add it.
        var resolver = Resolver([Catalog(ShieldsId, true), Catalog(SecondId, true), Catalog("extra", true)],
            [ShieldsId, SecondId]);

        var reordered = resolver.Resolve("run-1", GuardrailsContentStage.UserInput, GuardrailsContentClass.Text,
            [Accepted(SecondId), Accepted(ShieldsId)]);
        var added = resolver.Resolve("run-1", GuardrailsContentStage.UserInput, GuardrailsContentClass.Text,
            [Accepted(ShieldsId), Accepted(SecondId), Accepted("extra")]);

        Assert.Equal(GuardrailsReasonCode.InvalidConfiguration, reordered.ReasonCode);
        Assert.Equal(GuardrailsReasonCode.InvalidConfiguration, added.ReasonCode);
    }

    [Fact]
    public void DisabledCheckCannotEnterThePlatformOrderOrTheAcceptedSelection()
    {
        // The catalog itself refuses a platform order that names a disabled provider.
        Assert.False(ProviderCatalog.Create(
            [Catalog(ShieldsId, false)], [], [],
            [new ProviderOrderedSelection(ProviderSeam.Guardrails, [ShieldsId])]).IsSuccess);

        // Even with an override permission and a registered implementation, an accepted selection can't
        // bring the disabled provider into the plan.
        var catalog = Assert.IsType<ProviderCatalog>(ProviderCatalog.Create(
            [Catalog(ShieldsId, true), Catalog("disabled", false)],
            [],
            [new ProviderOverridePermission(ProviderSeam.Guardrails, "disabled")],
            [new ProviderOrderedSelection(ProviderSeam.Guardrails, [ShieldsId])]).Value);
        var resolver = new GuardrailsStagePlanResolver(catalog, [Registration(ShieldsId), Registration("disabled")]);

        var result = resolver.Resolve("run-1", GuardrailsContentStage.UserInput, GuardrailsContentClass.Text,
            [Accepted(ShieldsId), Accepted("disabled")]);

        Assert.False(result.IsSuccess);
        Assert.Equal(GuardrailsReasonCode.ProviderUnavailable, result.ReasonCode);
    }

    [Fact]
    public void ChangedAcceptedRevisionIsStale()
    {
        // Simulates a run accepted against an older options revision than the catalog now advertises.
        var resolver = Resolver([Catalog(ShieldsId, true)], [ShieldsId]);

        var result = resolver.Resolve("run-1", GuardrailsContentStage.UserInput, GuardrailsContentClass.Text,
            [Accepted(ShieldsId) with { OptionsRevision = "options-old" }]);

        Assert.Equal(GuardrailsReasonCode.AuthorityStale, result.ReasonCode);
    }

    [Theory]
    [InlineData(GuardrailsContentStage.ModelInput, GuardrailsContentClass.Text)]
    [InlineData(GuardrailsContentStage.ModelOutput, GuardrailsContentClass.Text)]
    [InlineData(GuardrailsContentStage.McpDiscovery, GuardrailsContentClass.ToolSchema)]
    [InlineData(GuardrailsContentStage.ToolResult, GuardrailsContentClass.Image)]
    public void UnsupportedDirectionFailsExplicitly(GuardrailsContentStage stage, GuardrailsContentClass contentClass)
    {
        var resolver = Resolver([Catalog(ShieldsId, true)], [ShieldsId]);

        var result = resolver.Resolve("run-1", stage, contentClass, null);

        Assert.Equal(GuardrailsReasonCode.UnsupportedStageOrContent, result.ReasonCode);
    }

    [Fact]
    public void MissingRegistrationIsUnavailable()
    {
        // The catalog enables two checks, but the host registers an implementation for only one.
        var catalog = CreateCatalog([Catalog(ShieldsId, true), Catalog(SecondId, true)], [ShieldsId, SecondId]);
        var resolver = new GuardrailsStagePlanResolver(catalog, [Registration(ShieldsId)]);

        var result = resolver.Resolve("run-1", GuardrailsContentStage.UserInput, GuardrailsContentClass.Text, null);

        Assert.Equal(GuardrailsReasonCode.ProviderUnavailable, result.ReasonCode);
    }

    [Fact]
    public void InvalidOrMismatchedOptionsAreInvalidConfiguration()
    {
        var catalog = CreateCatalog([Catalog(ShieldsId, true)], [ShieldsId]);
        // One registration has options that fail validation (HTTP endpoint); the other has valid options whose
        // revision differs from the catalog entry.
        var insecure = new GuardrailsStagePlanResolver(catalog,
        [
            Registration(ShieldsId, options => options with { Endpoint = new Uri("http://shields.example.test/") })
        ]);
        var revision = new GuardrailsStagePlanResolver(catalog,
        [
            Registration(ShieldsId, options => options with { OptionsRevision = "options-other" })
        ]);

        Assert.Equal(GuardrailsReasonCode.InvalidConfiguration, insecure.Resolve(
            "run-1", GuardrailsContentStage.UserInput, GuardrailsContentClass.Text, null).ReasonCode);
        Assert.Equal(GuardrailsReasonCode.InvalidConfiguration, revision.Resolve(
            "run-1", GuardrailsContentStage.UserInput, GuardrailsContentClass.Text, null).ReasonCode);
    }

    [Fact]
    public void IsActiveOnlyWithAcceptedChecksOrAPlatformOrderedSet()
    {
        var empty = new GuardrailsStagePlanResolver(
            Assert.IsType<ProviderCatalog>(ProviderCatalog.Create([], [], []).Value), []);
        var configured = Resolver([Catalog(ShieldsId, true)], [ShieldsId]);

        Assert.False(empty.IsActive(null));
        // An empty accepted list still counts as a recorded selection, so Guardrails stays active.
        Assert.True(empty.IsActive([]));
        Assert.True(configured.IsActive(null));
    }

    [Fact]
    public void DuplicateRegistrationIsRejected()
    {
        var catalog = CreateCatalog([Catalog(ShieldsId, true)], [ShieldsId]);

        Assert.Throws<ArgumentException>(() =>
            new GuardrailsStagePlanResolver(catalog, [Registration(ShieldsId), Registration(ShieldsId)]));
    }

    [Theory]
    [InlineData(false, GuardrailsDecision.Allow, GuardrailsReasonCode.Allowed)]
    [InlineData(true, GuardrailsDecision.Withhold, GuardrailsReasonCode.PolicyDenied)]
    public async Task PromptShieldsFindingsNarrowAgtAtTheModelTurnGate(
        bool attack,
        GuardrailsDecision decision,
        GuardrailsReasonCode reason)
    {
        // End to end: a stubbed Prompt Shields service reports an attack or not, the resolver builds the plan,
        // and the content gate combines the finding with the platform AGT policy.
        var handler = new PromptShieldsClassifierTests.StubHandler((_, _) => PromptShieldsClassifierTests.Json(
            HttpStatusCode.OK,
            $$"""{"userPromptAnalysis":{"attackDetected":{{(attack ? "true" : "false")}}},"documentsAnalysis":[]}"""));
        var catalog = CreateCatalog([Catalog(ShieldsId, true)], [ShieldsId]);
        var resolver = new GuardrailsStagePlanResolver(catalog,
        [
            new GuardrailsCheckRegistration<PromptShieldsClassifierOptions>(
                Options(ShieldsId), PromptShieldsClassifierTests.Create(handler), "shields-resource", 1)
        ]);
        var plan = resolver.Resolve("run-1", GuardrailsContentStage.UserInput, GuardrailsContentClass.Text, null);
        Assert.True(plan.IsSuccess);

        // Pin the AGT policy provider for the same run, then build the accepted run binding that the gate uses.
        var policyProvider = new AgtPolicyProvider();
        var policyOptions = new AgtPolicyProviderOptions("policy-resource", 1, "policy-revision-1", [PlatformAllowModelTurn]);
        var policyCatalog = Assert.IsType<ProviderCatalog>(ProviderCatalog.Create(
            [policyProvider.CreateRegistration(policyOptions)],
            [new ProviderSelection(ProviderSeam.Policy, AgtPolicyProvider.ProviderId)],
            []).Value);
        var policyPin = Assert.IsType<PinnedProviderBinding>((await policyProvider.ResolveNegotiateAndPinAsync(
            new ProviderResolver(policyCatalog), policyOptions, "run-1")).Value);
        var run = new GuardrailsAcceptedRunBinding(
            "https://identity.test/", "actor-1", "tenant-1", "project-1", "run-1", "session-1",
            "model.turn", "runtime-action", "1.1.1", new string('a', 64), "grant-1", "1", 3,
            policyPin, policyOptions, [], [plan.Plan!]);
        var content = Encoding.UTF8.GetBytes("prompt fixture");
        var journal = new Journal();
        var gate = new GuardrailsContentGate(policyProvider, new CurrentAuthority(), journal);

        var result = await gate.EvaluateAsync(run, new GuardrailsContentRequest(
            Guid.NewGuid(), "model-turn-1", new string('b', 64), GuardrailsContentStage.UserInput,
            GuardrailsContentClass.Text, null, ImmutableArray.Create(content)));

        Assert.Equal(decision, result.Decision);
        Assert.Equal(reason, result.ReasonCode);
        Assert.Single(handler.Calls);
        var evidence = Assert.Single(journal.Evidence);
        var check = Assert.Single(evidence.Checks);
        Assert.Equal(ShieldsId, check.ProviderId);
        Assert.Equal(0, check.Order);
        // Evidence records only the digest of the content. It must never contain the prompt text or the token.
        Assert.DoesNotContain("prompt fixture", JsonSerializer.Serialize(evidence));
        Assert.DoesNotContain("token-1", JsonSerializer.Serialize(evidence));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(content)), evidence.ContentDigest);
    }

    private static GuardrailsStagePlanResolver Resolver(
        ProviderRegistration[] registrations,
        ImmutableArray<string> order) =>
        new(CreateCatalog(registrations, order),
            registrations.Select(item => Registration(item.Descriptor.Id)).ToArray());

    private static ProviderCatalog CreateCatalog(
        ProviderRegistration[] registrations,
        ImmutableArray<string> order) =>
        Assert.IsType<ProviderCatalog>(ProviderCatalog.Create(
            registrations, [], [], [new ProviderOrderedSelection(ProviderSeam.Guardrails, order)]).Value);

    // Catalog entry whose options revision is "options-<id>", matching the default Registration options.
    private static ProviderRegistration Catalog(string id, bool enabled) => new(
        new ProviderDescriptor(
            ProviderSeam.Guardrails,
            id,
            Adapter,
            1,
            ProviderHostingPattern.InProcess,
            GuardrailsProviderCapabilities.PromptShieldsSupported),
        enabled,
        "options-" + id,
        1);

    private static GuardrailsAcceptedCheck Accepted(string id) => new(id, Adapter.ToString(), 1, "options-" + id);

    private static PromptShieldsClassifierOptions Options(string id) => new(
        id, "options-" + id, new Uri("https://shields.example.test/"), "2024-09-01", TimeSpan.FromSeconds(5));

    // Registers a Prompt Shields classifier under the given ID. The optional change lets a test break the
    // options. The stub service is never expected to be called by resolver-only tests.
    private static IGuardrailsCheckRegistration Registration(
        string id,
        Func<PromptShieldsClassifierOptions, PromptShieldsClassifierOptions>? change = null)
    {
        var handler = new PromptShieldsClassifierTests.StubHandler((_, _) =>
            PromptShieldsClassifierTests.Json(HttpStatusCode.OK, "{}"));
        return new GuardrailsCheckRegistration<PromptShieldsClassifierOptions>(
            (change ?? (options => options))(Options(id)),
            new PromptShieldsClassifier(new HttpClient(handler),
                new PromptShieldsClassifierTests.StubCredential(() => "token-1"), id),
            "resource-" + id,
            1);
    }

    // Always reports that the run's authority is still current.
    private sealed class CurrentAuthority : IGuardrailsCurrentAuthority
    {
        public Task<GuardrailsAuthorityStatus> CheckCurrentAsync(
            GuardrailsAuthorityCheckRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(GuardrailsAuthorityStatus.Current);
    }

    // In-memory evidence journal so the test can inspect what the gate recorded.
    private sealed class Journal : IGuardrailsEvidenceJournal
    {
        public List<GuardrailsEvaluationEvidence> Evidence { get; } = [];

        public Task<GuardrailsJournalAppendStatus> AppendAsync(
            GuardrailsEvaluationEvidence evidence,
            CancellationToken cancellationToken)
        {
            Evidence.Add(evidence);
            return Task.FromResult(GuardrailsJournalAppendStatus.Appended);
        }
    }
}
