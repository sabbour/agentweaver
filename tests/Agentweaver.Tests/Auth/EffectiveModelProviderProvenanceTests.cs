using System.Text.Json;
using Agentweaver.Api.Auth;
using Agentweaver.Domain;
using FluentAssertions;

namespace Agentweaver.Tests.Auth;

/// <summary>
/// Covers the run-level model-provider bookkeeping that used to be hardcoded: the durable
/// <see cref="ModelSource"/> stamped on a <see cref="Run"/>, the <c>run.model_provider_resolved</c>
/// provenance payload, and the SCOPE of the connection-required handoff.
/// </summary>
public sealed class EffectiveModelProviderProvenanceTests
{
    [Fact]
    public void Byok_result_persists_the_byok_model_source_not_the_copilot_literal()
    {
        EffectiveModelProviderResult result = new EffectiveModelProviderResult.Byok("provider-1", "azure");

        result.ToModelSource().Should().Be(ModelSource.Byok,
            "a BYOK run must not be persisted (and rendered) as GitHub Copilot");
    }

    [Fact]
    public void Project_copilot_result_persists_the_github_copilot_model_source()
    {
        EffectiveModelProviderResult result =
            new EffectiveModelProviderResult.ProjectGitHubCopilot("binding-1", "octocat");

        result.ToModelSource().Should().Be(ModelSource.GitHubCopilot);
    }

    [Fact]
    public void Platform_copilot_result_persists_the_github_copilot_model_source()
    {
        EffectiveModelProviderResult result =
            new EffectiveModelProviderResult.PlatformGitHubCopilot("binding-2", "platform-bot");

        result.ToModelSource().Should().Be(ModelSource.GitHubCopilot);
    }

    [Fact]
    public void Byok_provenance_payload_carries_public_provider_metadata_without_raw_identity()
    {
        EffectiveModelProviderResult result = new EffectiveModelProviderResult.Byok("provider-1", "anthropic");

        var payload = result.ToProvenancePayload(
            "run-1",
            "claude-opus-4.8",
            EffectiveModelProviderProvenance.ScopeProject);

        var fields = Fields(payload);
        fields["runId"].Should().Be("run-1");
        fields["state"].Should().Be(EffectiveModelProviderProvenance.StateResolved);
        fields["providerKind"].Should().Be(EffectiveModelProviderProvenance.KindByok);
        fields.Should().NotContainKey("providerId");
        fields["providerType"].Should().Be("anthropic");
        fields.Should().NotContainKey("githubLogin");
        fields["modelSource"].Should().Be("byok");
        fields["modelId"].Should().Be("claude-opus-4.8");
        fields["resolutionScope"].Should().Be(EffectiveModelProviderProvenance.ScopeProject);
        fields["providerScope"].Should().Be(EffectiveModelProviderProvenance.ScopePlatform);
        fields["providerKey"].Should().BeOfType<string>().Which.Should().HaveLength(64);
        fields["timestamp_utc"].Should().NotBeNull();
    }

    [Fact]
    public void Project_execution_inheriting_platform_byok_exposes_both_scopes()
    {
        EffectiveModelProviderResult result = new EffectiveModelProviderResult.Byok("provider-1", "azure");

        var context = result.ToContract(
            EffectiveModelProviderProvenance.ScopeProject,
            "gpt-4.1");

        context.State.Should().Be("resolved");
        context.ProviderKind.Should().Be("byok");
        context.ResolutionScope.Should().Be("project");
        context.ProviderScope.Should().Be("platform");
        context.ProviderType.Should().Be("azure");
        context.ModelId.Should().Be("gpt-4.1");
        context.ProviderKey.Should().HaveLength(64);
    }

    [Fact]
    public void Legacy_provenance_payload_is_projected_without_falling_back_to_run_model_source()
    {
        var payload = new
        {
            providerKind = "platform_github_copilot",
            providerId = "binding-1",
            githubLogin = "platform-bot",
            modelSource = "github-copilot",
            modelId = "gpt-5",
        };

        var context = EffectiveModelProviderProvenance.TryReadContract(payload);

        context.Should().NotBeNull();
        context!.ProviderKind.Should().Be("platform_github_copilot");
        context.ProviderScope.Should().Be("platform");
        context.ResolutionScope.Should().Be("unknown",
            "legacy events did not record whether a platform provider was resolved for project or platform execution");
        context.ProviderKey.Should().HaveLength(64);
    }

    [Fact]
    public void Legacy_provenance_payload_is_redacted_before_public_serialization()
    {
        var payload = new
        {
            providerKind = "platform_github_copilot",
            providerId = "binding-1",
            githubLogin = "platform-bot",
            modelSource = "github-copilot",
            modelId = "gpt-5",
        };

        var publicPayload = EffectiveModelProviderProvenance.RedactPublicPayload(payload);

        publicPayload.ContainsKey("providerId").Should().BeFalse();
        publicPayload.ContainsKey("githubLogin").Should().BeFalse();
        publicPayload["providerIdentityVersion"]!.GetValue<int>().Should().Be(2);
        publicPayload["providerKey"]!.GetValue<string>().Should().HaveLength(64);
        publicPayload.ToJsonString().Should().NotContain("binding-1");
        publicPayload.ToJsonString().Should().NotContain("platform-bot");
    }

    [Fact]
    public void Legacy_byok_provenance_fails_closed_without_a_configuration_fingerprint()
    {
        var payload = new
        {
            providerKind = "byok",
            providerId = "provider-1",
            providerType = "azure",
            modelId = "gpt-5",
        };
        var current = new EffectiveModelProviderResult.Byok(
            "provider-1",
            "azure",
            "new-configuration-fingerprint");

        EffectiveModelProviderProvenance.MatchesDurableProvider(
            payload,
            current,
            EffectiveModelProviderProvenance.ScopeProject,
            "gpt-5").Should().BeFalse();
        EffectiveModelProviderProvenance.MatchesDurableProvider(
            payload,
            current with { ProviderId = "provider-2" },
            EffectiveModelProviderProvenance.ScopeProject,
            "gpt-5").Should().BeFalse();
    }

    [Fact]
    public void Version_two_provenance_without_a_provider_fingerprint_fails_closed()
    {
        var payload = new
        {
            providerIdentityVersion = 2,
            providerKind = "byok",
            providerType = "azure",
            modelSource = "byok",
        };

        EffectiveModelProviderProvenance.MatchesDurableProvider(
            payload,
            new EffectiveModelProviderResult.Byok("provider-1", "azure", "configuration-fingerprint"),
            EffectiveModelProviderProvenance.ScopeProject,
            "gpt-5").Should().BeFalse();
    }

    [Fact]
    public void Project_copilot_provenance_payload_omits_binding_and_account_metadata()
    {
        EffectiveModelProviderResult result =
            new EffectiveModelProviderResult.ProjectGitHubCopilot("binding-1", "octocat");

        var fields = Fields(result.ToProvenancePayload("run-2", "gpt-5"));

        fields["providerKind"].Should().Be(EffectiveModelProviderProvenance.KindProjectGitHubCopilot);
        fields.Should().NotContainKey("providerId");
        fields.Should().NotContainKey("githubLogin");
        fields["modelSource"].Should().Be("github-copilot");
        fields["modelId"].Should().Be("gpt-5");
    }

    [Fact]
    public void Public_provider_contract_never_serializes_the_github_login()
    {
        EffectiveModelProviderResult result =
            new EffectiveModelProviderResult.ProjectGitHubCopilot("binding-1", "octocat");

        var contract = JsonSerializer.SerializeToElement(
            result.ToContract(EffectiveModelProviderProvenance.ScopeProject, "gpt-5"));

        contract.TryGetProperty("github_login", out _).Should().BeFalse();
        contract.ToString().Should().NotContain("octocat");
    }

    [Fact]
    public void Copilot_provider_fingerprint_uses_credential_version_not_github_login()
    {
        var original = new EffectiveModelProviderResult.PlatformGitHubCopilot(
            "platform-default", "octocat", "credential-version-1");
        var renamed = original with { GitHubLogin = "different-login" };
        var rebound = original with { CredentialVersion = "credential-version-2" };

        renamed.ProviderKey().Should().Be(original.ProviderKey(),
            "public fingerprints must not encode or change with the GitHub login");
        rebound.ProviderKey().Should().NotBe(original.ProviderKey(),
            "a replaced credential must invalidate accepted provider identity");
    }

    [Fact]
    public void Platform_copilot_provenance_payload_omits_binding_and_account_metadata()
    {
        EffectiveModelProviderResult result =
            new EffectiveModelProviderResult.PlatformGitHubCopilot("binding-2", "platform-bot");

        var fields = Fields(result.ToProvenancePayload("run-3", null));

        fields["providerKind"].Should().Be(EffectiveModelProviderProvenance.KindPlatformGitHubCopilot);
        fields.Should().NotContainKey("providerId");
        fields.Should().NotContainKey("githubLogin");
    }

    [Fact]
    public void Platform_binding_failure_names_the_platform_scope_even_when_a_project_id_is_known()
    {
        var projectId = ProjectId.New();
        EffectiveModelProviderResult result =
            new EffectiveModelProviderResult.PlatformGitHubCopilot("binding-2", "platform-bot");

        var exception = result.ToConnectionRequiredException(projectId);

        exception.Requirement.Action.Type.Should()
            .Be(ModelProviderConnectionAction.ConfigurePlatformModelProvider,
                "a platform-default binding failure must route the human to Platform Settings");
        exception.Requirement.Message.Should()
            .Be(ModelProviderConnectionRequirement.PlatformDefaultRequirementMessage);
        exception.Requirement.Action.ProjectId.Should().BeEmpty();
    }

    [Fact]
    public void Project_binding_failure_names_the_project_scope()
    {
        var projectId = ProjectId.New();
        EffectiveModelProviderResult result =
            new EffectiveModelProviderResult.ProjectGitHubCopilot("binding-1", "octocat");

        var exception = result.ToConnectionRequiredException(projectId);

        exception.Requirement.Action.Type.Should()
            .Be(ModelProviderConnectionAction.ConfigureProjectModelProvider);
        exception.Requirement.Action.ProjectId.Should().Be(projectId.ToString());
    }

    [Fact]
    public void Project_binding_reauthorization_failure_stays_project_scoped()
    {
        var projectId = ProjectId.New();
        EffectiveModelProviderResult result = new EffectiveModelProviderResult.Unavailable(
            EffectiveModelProviderUnavailableReason.ProjectBindingRequiresReauthorization,
            "reconnect");

        var exception = result.ToConnectionRequiredException(projectId);

        exception.Requirement.Action.Type.Should()
            .Be(ModelProviderConnectionAction.ConfigureProjectModelProvider);
        exception.Requirement.Action.ProjectId.Should().Be(projectId.ToString());
    }

    [Fact]
    public void Platform_scope_is_used_when_no_project_is_in_play()
    {
        EffectiveModelProviderResult result = new EffectiveModelProviderResult.Unavailable(
            EffectiveModelProviderUnavailableReason.NoProvider,
            "nothing configured");

        var exception = result.ToConnectionRequiredException(projectId: null);

        exception.Requirement.Action.Type.Should()
            .Be(ModelProviderConnectionAction.ConfigurePlatformModelProvider);
    }

    [Fact]
    public void Unavailable_event_and_contract_use_the_same_actionable_reason_code()
    {
        EffectiveModelProviderResult result = new EffectiveModelProviderResult.Unavailable(
            EffectiveModelProviderUnavailableReason.NoProvider,
            "not configured");

        var eventFields = Fields(result.ToProvenancePayload(
            "run-unavailable",
            modelId: null,
            EffectiveModelProviderProvenance.ScopePlatform));
        var contract = result.ToContract(EffectiveModelProviderProvenance.ScopePlatform);

        eventFields["unavailableReason"].Should().Be("no_provider");
        contract.UnavailableReason.Should().Be("no_provider");
    }

    [Fact]
    public void No_resolver_result_preserves_the_legacy_project_scoped_handoff()
    {
        var projectId = ProjectId.New();

        var exception = ((EffectiveModelProviderResult?)null).ToConnectionRequiredException(projectId);

        exception.Requirement.Action.Type.Should()
            .Be(ModelProviderConnectionAction.ConfigureProjectModelProvider);
        exception.Requirement.Action.ProjectId.Should().Be(projectId.ToString());
    }

    private static Dictionary<string, object?> Fields(object payload) =>
        payload.GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(payload));
}
