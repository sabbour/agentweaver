using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Agentweaver.Providers;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class AgtPolicyProviderTests
{
    private const string PlatformAllowMerge = """
        apiVersion: governance.toolkit/v1
        version: "1.0"
        name: platform-policy
        scope: global
        default_action: deny
        rules:
          - name: allow-merge
            condition: "action_id == 'merge'"
            action: allow
            priority: 100
        """;

    private static AgtPolicyProviderOptions Options(params string[] platformPolicies) =>
        new(
            "agt-policy-resource",
            3,
            "agt-policy-v1",
            [.. platformPolicies]);

    private static string Policy(
        string name,
        string defaultAction,
        string ruleName,
        string condition,
        string action,
        int priority) => $$"""
        apiVersion: governance.toolkit/v1
        version: "1.0"
        name: {{name}}
        scope: global
        default_action: {{defaultAction}}
        rules:
          - name: {{ruleName}}
            condition: "{{condition}}"
            action: {{action}}
            priority: {{priority}}
        """;

    private static ProviderCatalog Catalog(AgtPolicyProvider provider, AgtPolicyProviderOptions options) =>
        Assert.IsType<ProviderCatalog>(ProviderCatalog.Create(
            [provider.CreateRegistration(options)],
            [new ProviderSelection(ProviderSeam.Policy, AgtPolicyProvider.ProviderId)],
            []).Value);

    [Fact]
    public void RegistersEnabledPlatformSingletonWithNoProjectOverride()
    {
        var provider = new AgtPolicyProvider();
        var options = Options(PlatformAllowMerge);
        var registration = provider.CreateRegistration(options);
        var catalog = Catalog(provider, options);
        var resolver = new ProviderResolver(catalog);

        Assert.Equal(ProviderSeam.Policy, registration.Descriptor.Seam);
        Assert.True(registration.Enabled);
        Assert.Equal(PolicyProviderCapabilities.All, registration.Descriptor.AdvertisedCapabilities);
        Assert.True(resolver.Resolve(new ProviderResolutionRequest(
            ProviderSeam.Policy,
            null,
            AgtPolicyProvider.AdapterVersion,
            AgtPolicyProvider.OptionsSchemaVersion,
            PolicyProviderCapabilities.All)).IsSuccess);

        var projectOverride = resolver.Resolve(new ProviderResolutionRequest(
            ProviderSeam.Policy,
            AgtPolicyProvider.ProviderId,
            AgtPolicyProvider.AdapterVersion,
            AgtPolicyProvider.OptionsSchemaVersion,
            PolicyProviderCapabilities.All));
        Assert.False(projectOverride.IsSuccess);
        Assert.Equal(ProviderErrorCode.OverrideNotPermitted, projectOverride.Error!.Code);
    }

    [Fact]
    public async Task NegotiatesAndPinsTheValidatedPlatformPolicyResource()
    {
        var provider = new AgtPolicyProvider();
        var options = Options(PlatformAllowMerge);

        var result = await provider.ResolveNegotiateAndPinAsync(
            new ProviderResolver(Catalog(provider, options)),
            options,
            "run-1");

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(ProviderSeam.Policy, result.Value!.Seam);
        Assert.Equal(AgtPolicyProvider.ProviderId, result.Value.ProviderId);
        Assert.Equal(options.OptionsRevision, result.Value.OptionsRevision);
        Assert.Equal(options.ResourceId, result.Value.Resource.ResourceId);
        Assert.Equal(options.ResourceGeneration, result.Value.Resource.Generation);
        Assert.Equal(PolicyProviderCapabilities.All, result.Value.NegotiatedCapabilities);
    }

    [Fact]
    public void RejectsEmptyPlatformPolicyAndUnsafeActionsBeforeRegistration()
    {
        var provider = new AgtPolicyProvider();

        Assert.Throws<ArgumentException>(() => provider.CreateRegistration(Options()));
        Assert.Throws<ArgumentException>(() => provider.CreateRegistration(Options(
            Policy("platform", "allow", "allow-merge", "action_id == 'merge'", "allow", 1))));
        Assert.Throws<ArgumentException>(() => provider.CreateRegistration(Options(
            Policy("platform", "deny", "warn-merge", "action_id == 'merge'", "warn", 1))));
        Assert.Throws<ArgumentException>(() => provider.CreateRegistration(Options(
            Enumerable.Repeat(PlatformAllowMerge, 33).ToArray())));
    }

    [Fact]
    public void ProjectDenyNarrowsPlatformAllowRegardlessOfRulePriority()
    {
        var provider = new AgtPolicyProvider();
        var options = Options(PlatformAllowMerge);
        var request = new AgtPolicyEvaluationRequest(
            "actor-1",
            "merge",
            ProjectPolicyDocuments:
            [
                Policy("project-policy", "allow", "deny-merge", "action_id == 'merge'", "deny", 1)
            ]);

        var result = provider.Evaluate(options, request);

        Assert.Equal(PolicyEvaluationOutcome.Deny, result.Outcome);
        Assert.Equal(PolicyEvaluationReasonCode.ProjectRuleNarrowed, result.ReasonCode);
    }

    [Fact]
    public void ProjectAllowCannotWidenPlatformDeny()
    {
        var provider = new AgtPolicyProvider();
        var platformDeny = Policy(
            "platform-policy",
            "deny",
            "allow-other-action",
            "action_id == 'inspect'",
            "allow",
            100);
        var projectAllow = Policy(
            "project-policy",
            "allow",
            "allow-merge",
            "action_id == 'merge'",
            "allow",
            1);

        var result = provider.Evaluate(
            Options(platformDeny),
            new AgtPolicyEvaluationRequest(
                "actor-1",
                "merge",
                ProjectPolicyDocuments: [projectAllow]));

        Assert.Equal(PolicyEvaluationOutcome.Deny, result.Outcome);
        Assert.Equal(PolicyEvaluationReasonCode.PlatformRuleDenied, result.ReasonCode);
    }

    [Fact]
    public void EveryConfiguredPolicyDocumentMustAllow()
    {
        var provider = new AgtPolicyProvider();
        var platformDeny = Policy(
            "platform-deny",
            "deny",
            "allow-other",
            "action_id == 'inspect'",
            "allow",
            100);
        var projectDeny = Policy(
            "project-deny",
            "deny",
            "allow-other",
            "action_id == 'inspect'",
            "allow",
            100);
        var projectAllow = Policy(
            "project-allow",
            "allow",
            "allow-merge",
            "action_id == 'merge'",
            "allow",
            1);

        var platformResult = provider.Evaluate(
            Options(PlatformAllowMerge, platformDeny),
            new AgtPolicyEvaluationRequest("actor-1", "merge"));
        var projectResult = provider.Evaluate(
            Options(PlatformAllowMerge),
            new AgtPolicyEvaluationRequest(
                "actor-1",
                "merge",
                ProjectPolicyDocuments: [projectDeny, projectAllow]));

        Assert.Equal(PolicyEvaluationOutcome.Deny, platformResult.Outcome);
        Assert.Equal(PolicyEvaluationReasonCode.PlatformRuleDenied, platformResult.ReasonCode);
        Assert.Equal(PolicyEvaluationOutcome.Deny, projectResult.Outcome);
        Assert.Equal(PolicyEvaluationReasonCode.ProjectRuleNarrowed, projectResult.ReasonCode);
    }

    [Fact]
    public void EvaluatesOpaqueAgentweaverActorFromContextWithoutPassingItAsAnAgtDid()
    {
        var provider = new AgtPolicyProvider();
        var policy = Policy(
            "platform-policy",
            "deny",
            "allow-actor",
            "actor_id == 'actor-1'",
            "allow",
            1);

        var result = provider.Evaluate(
            Options(policy),
            new AgtPolicyEvaluationRequest("actor-1", "merge"));

        Assert.Equal(PolicyEvaluationOutcome.Allow, result.Outcome);
        Assert.Equal(PolicyEvaluationReasonCode.Allowed, result.ReasonCode);
    }

    [Fact]
    public void FailsClosedForInvalidPolicyAndUnsupportedContext()
    {
        var provider = new AgtPolicyProvider();
        var invalidOptions = Options(PlatformAllowMerge) with
        {
            PlatformPolicyDocuments = ImmutableArray<string>.Empty
        };
        var invalidPolicy = provider.Evaluate(
            invalidOptions,
            new AgtPolicyEvaluationRequest("actor-1", "merge"));
        var unsupportedContext = provider.Evaluate(
            Options(PlatformAllowMerge),
            new AgtPolicyEvaluationRequest(
                "actor-1",
                "merge",
                new Dictionary<string, object> { ["arguments"] = new object() }));

        Assert.Equal(PolicyEvaluationOutcome.Error, invalidPolicy.Outcome);
        Assert.Equal(PolicyEvaluationReasonCode.EvaluationFailed, invalidPolicy.ReasonCode);
        Assert.Equal(PolicyEvaluationOutcome.Error, unsupportedContext.Outcome);
        Assert.Equal(PolicyEvaluationReasonCode.EvaluationFailed, unsupportedContext.ReasonCode);
        Assert.DoesNotContain("arguments", unsupportedContext.ToString(), StringComparison.Ordinal);
    }
}
