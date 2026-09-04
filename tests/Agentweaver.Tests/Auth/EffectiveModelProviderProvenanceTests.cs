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
    public void Byok_provenance_payload_carries_provider_identity_and_model()
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
        fields["providerId"].Should().Be("provider-1");
        fields["providerType"].Should().Be("anthropic");
        fields["githubLogin"].Should().BeNull();
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
        context.GitHubLogin.Should().Be("platform-bot");
        context.ProviderKey.Should().HaveLength(64);
    }

    [Fact]
    public void Project_copilot_provenance_payload_carries_the_binding_and_account_login()
    {
        EffectiveModelProviderResult result =
            new EffectiveModelProviderResult.ProjectGitHubCopilot("binding-1", "octocat");

        var fields = Fields(result.ToProvenancePayload("run-2", "gpt-5"));

        fields["providerKind"].Should().Be(EffectiveModelProviderProvenance.KindProjectGitHubCopilot);
        fields["providerId"].Should().Be("binding-1");
        fields["githubLogin"].Should().Be("octocat");
        fields["modelSource"].Should().Be("github-copilot");
        fields["modelId"].Should().Be("gpt-5");
    }

    [Fact]
    public void Platform_copilot_provenance_payload_carries_the_platform_binding_and_account_login()
    {
        EffectiveModelProviderResult result =
            new EffectiveModelProviderResult.PlatformGitHubCopilot("binding-2", "platform-bot");

        var fields = Fields(result.ToProvenancePayload("run-3", null));

        fields["providerKind"].Should().Be(EffectiveModelProviderProvenance.KindPlatformGitHubCopilot);
        fields["providerId"].Should().Be("binding-2");
        fields["githubLogin"].Should().Be("platform-bot");
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
    public void Unavailable_event_preserves_legacy_enum_name_while_contract_uses_snake_case()
    {
        EffectiveModelProviderResult result = new EffectiveModelProviderResult.Unavailable(
            EffectiveModelProviderUnavailableReason.NoProvider,
            "not configured");

        var eventFields = Fields(result.ToProvenancePayload(
            "run-unavailable",
            modelId: null,
            EffectiveModelProviderProvenance.ScopePlatform));
        var contract = result.ToContract(EffectiveModelProviderProvenance.ScopePlatform);

        eventFields["unavailableReason"].Should().Be("NoProvider");
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
