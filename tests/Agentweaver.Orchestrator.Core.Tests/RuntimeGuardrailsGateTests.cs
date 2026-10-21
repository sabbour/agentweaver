using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator.Core;
using Agentweaver.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class RuntimeGuardrailsGateTests
{
    [Theory]
    [InlineData(GuardrailsReasonCode.PolicyDenied, "runtime_guardrails_policy_denied", false)]
    [InlineData(GuardrailsReasonCode.ProviderUnavailable, "runtime_guardrails_provider_unavailable", true)]
    [InlineData(GuardrailsReasonCode.ClassifierTimedOut, "runtime_guardrails_classifier_timed_out", true)]
    [InlineData(GuardrailsReasonCode.JournalUnavailable, "runtime_guardrails_journal_unavailable", true)]
    [InlineData(GuardrailsReasonCode.AuthorityStale, "runtime_guardrails_authority_stale", false)]
    [InlineData(GuardrailsReasonCode.UnsupportedStageOrContent, "runtime_guardrails_unsupported_stage_or_content", false)]
    public void ReasonsMapToStablePublicCodes(GuardrailsReasonCode reason, string code, bool transient)
    {
        Assert.Equal(code, RuntimeGuardrailsGate.Code(reason));
        Assert.Equal(transient, RuntimeGuardrailsGate.IsTransient(code));
    }

    [Fact]
    public void SnapshotWithoutGuardrailsSelectionHasNoAcceptedChecks()
    {
        Assert.Null(RuntimeGuardrailsGate.ReadAcceptedChecks(Snapshot("{}")));
        Assert.Null(RuntimeGuardrailsGate.ReadAcceptedChecks(Snapshot("""{"providers":[]}""")));
    }

    [Fact]
    public void OrderedGuardrailsSelectionIsReadInOrder()
    {
        var snapshot = SnapshotWith(Selection(ProviderCardinality.OrderedComposite, "first", "second"));

        var checks = RuntimeGuardrailsGate.ReadAcceptedChecks(snapshot);

        Assert.NotNull(checks);
        Assert.Equal(new[] { "first", "second" }, checks.Select(item => item.ProviderId));
        Assert.All(checks, item => Assert.Equal("1.0.0", item.AdapterVersion));
        Assert.Equal("options-first", checks[0].OptionsRevision);
    }

    [Fact]
    public void MalformedGuardrailsSelectionIsInvalidConfiguration()
    {
        var single = SnapshotWith(Selection(ProviderCardinality.Exclusive, "first"));
        var duplicate = SnapshotWith(
            Selection(ProviderCardinality.OrderedComposite, "first"),
            Selection(ProviderCardinality.OrderedComposite, "second"));

        Assert.Equal("runtime_guardrails_invalid_configuration", Assert.Throws<RuntimeAuthorizationException>(
            () => RuntimeGuardrailsGate.ReadAcceptedChecks(single)).Code);
        Assert.Equal("runtime_guardrails_invalid_configuration", Assert.Throws<RuntimeAuthorizationException>(
            () => RuntimeGuardrailsGate.ReadAcceptedChecks(duplicate)).Code);
        Assert.Equal("runtime_guardrails_invalid_configuration", Assert.Throws<RuntimeAuthorizationException>(
            () => RuntimeGuardrailsGate.ReadAcceptedChecks(Snapshot("""{"providers":[1]}"""))).Code);
    }

    [Fact]
    public void PrepareSkipsOtherActionsAndUnconfiguredPlatforms()
    {
        var services = new ServiceCollection().BuildServiceProvider();

        Assert.Null(RuntimeGuardrailsGate.Prepare(services, Request("tool.invoke"), Accepted(Snapshot("{}")), null));
        Assert.Null(RuntimeGuardrailsGate.Prepare(services, Request("model.turn"), Accepted(Snapshot("{}")), null));
    }

    [Fact]
    public void PrepareFailsClosedWhenAcceptedChecksHaveNoClassifier()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var accepted = Accepted(SnapshotWith(Selection(ProviderCardinality.OrderedComposite, "first")));

        var failure = Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeGuardrailsGate.Prepare(services, Request("model.turn"), accepted, [1]));

        Assert.Equal("runtime_guardrails_unavailable", failure.Code);
    }

    [Fact]
    public void PrepareRequiresContentOnlyWhenChecksApply()
    {
        var resolver = new GuardrailsStagePlanResolver(
            Assert.IsType<ProviderCatalog>(ProviderCatalog.Create([], [], []).Value), []);
        var services = new ServiceCollection().AddSingleton(resolver).BuildServiceProvider();
        var accepted = Accepted(SnapshotWith(Selection(ProviderCardinality.OrderedComposite, "first")));

        Assert.Null(RuntimeGuardrailsGate.Prepare(services, Request("model.turn"), Accepted(Snapshot("{}")), null));
        Assert.Equal(RuntimeGuardrailsGate.ContentRequiredCode, Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeGuardrailsGate.Prepare(services, Request("model.turn"), accepted, null)).Code);
        var prepared = RuntimeGuardrailsGate.Prepare(services, Request("model.turn"), accepted, [1, 2]);
        Assert.NotNull(prepared);
        Assert.Equal(new byte[] { 1, 2 }, prepared.Content);
        Assert.Single(prepared.AcceptedChecks!);
    }

    [Fact]
    public async Task AuthorityCheckMapsCurrentStaleRevokedAndUnavailable()
    {
        var registration = Registration();
        var accepted = Accepted(Snapshot("{}"));
        var hash = RuntimeGuardrailsGate.SelectionHash(accepted.Selection);
        Task<RuntimeRegistration> Same(CancellationToken _) => Task.FromResult(registration);
        Task<AuthorizedRunSelection> SameSelection(CancellationToken _) => Task.FromResult(accepted);

        Assert.Equal(GuardrailsAuthorityStatus.Current,
            await RuntimeGuardrailsGate.AuthorityCheck(Same, SameSelection, registration, hash)(default));
        Assert.Equal(GuardrailsAuthorityStatus.Stale, await RuntimeGuardrailsGate.AuthorityCheck(
            _ => Task.FromResult(registration with { Revision = 9 }), SameSelection, registration, hash)(default));
        Assert.Equal(GuardrailsAuthorityStatus.Stale, await RuntimeGuardrailsGate.AuthorityCheck(
            Same, _ => Task.FromResult(Accepted(Snapshot("""{"changed":true}"""))), registration, hash)(default));
        Assert.Equal(GuardrailsAuthorityStatus.Revoked, await RuntimeGuardrailsGate.AuthorityCheck(
            _ => throw new RuntimeAuthorizationException("runtime_registration_revoked"), SameSelection,
            registration, hash)(default));
        Assert.Equal(GuardrailsAuthorityStatus.Revoked, await RuntimeGuardrailsGate.AuthorityCheck(
            Same, _ => throw new CoordinationException("denied", StatusCodes.Status403Forbidden),
            registration, hash)(default));
        Assert.Equal(GuardrailsAuthorityStatus.Unavailable, await RuntimeGuardrailsGate.AuthorityCheck(
            Same, _ => throw new CoordinationException("down", StatusCodes.Status503ServiceUnavailable),
            registration, hash)(default));
        Assert.Equal(GuardrailsAuthorityStatus.Unavailable, await RuntimeGuardrailsGate.AuthorityCheck(
            _ => throw new HttpRequestException("network"), SameSelection, registration, hash)(default));
    }

    [Fact]
    public async Task AuthorityCheckRethrowsCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var registration = Registration();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RuntimeGuardrailsGate.AuthorityCheck(
            token => Task.FromCanceled<RuntimeRegistration>(token),
            _ => Task.FromResult(Accepted(Snapshot("{}"))),
            registration,
            "hash")(cancellation.Token));
    }

    private static JsonElement Snapshot(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static JsonElement SnapshotWith(params EffectiveProviderSelection[] selections) =>
        Snapshot(JsonSerializer.Serialize(
            new { providers = selections }, SourceControlEndpoints.ProviderSelectionJsonOptions));

    private static EffectiveProviderSelection Selection(ProviderCardinality cardinality, params string[] ids) =>
        new(cardinality, ProviderSeam.Guardrails, ids.Select(id => new EffectiveProviderCandidate(
            ProviderSeam.Guardrails,
            id,
            "1.0.0",
            1,
            "options-" + id,
            ProviderHostingPattern.InProcess,
            [GuardrailsProviderCapabilities.PromptInjectionDetection],
            [])).ToImmutableArray());

    private static AuthorizedRunSelection Accepted(JsonElement snapshot) => new(
        new EffectiveRunSelection("project-1", "run-1", 1, 1, 1, "context-1", snapshot),
        new ProjectsAuthorizationContext(1, "https://identity.test/", "actor-1", "tenant-1", 1,
            "project-1", "run-1", []));

    private static RuntimeActionRequest Request(string actionId) =>
        new(1, Guid.NewGuid(), 1, 1, Guid.NewGuid(), actionId, new string('a', 64));

    private static RuntimeRegistration Registration() =>
        new(Guid.NewGuid(), 1, null!, RuntimeRegistrationState.Active, DateTimeOffset.UnixEpoch);
}
