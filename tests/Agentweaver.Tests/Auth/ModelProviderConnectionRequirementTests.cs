using System.Text.Json;
using Agentweaver.Api.Auth;
using Agentweaver.Domain;
using FluentAssertions;

namespace Agentweaver.Tests.Auth;

public sealed class ModelProviderConnectionRequirementTests
{
    [Fact]
    public void ForProject_exposes_the_single_redacted_handoff_action_contract()
    {
        var projectId = ProjectId.New();

        var requirement = ModelProviderConnectionRequirement.ForProject(projectId);

        requirement.Code.Should().Be(ModelProviderConnectionRequirement.RequirementCode);
        requirement.Message.Should().Be(ModelProviderConnectionRequirement.RequirementMessage);
        requirement.Action.Type.Should().Be(ModelProviderConnectionAction.ConfigureProjectModelProvider);
        requirement.Action.ProjectId.Should().Be(projectId.ToString());
        var json = JsonSerializer.Serialize(requirement);
        json.Should().NotContain("token").And.NotContain("secret")
            .And.NotContain("authorization_url").And.NotContain("transaction")
            .And.NotContain("callback").And.NotContain("repository").And.NotContain("installation");
    }

    [Fact]
    public void ForPlatformDefault_uses_a_distinct_platform_scoped_action_type_so_clients_route_to_platform_settings()
    {
        var requirement = ModelProviderConnectionRequirement.ForPlatformDefault();

        requirement.Code.Should().Be(ModelProviderConnectionRequirement.RequirementCode);
        requirement.Action.Type.Should().Be(ModelProviderConnectionAction.ConfigurePlatformModelProvider);
        requirement.Action.Type.Should().NotBe(ModelProviderConnectionAction.ConfigureProjectModelProvider);
        requirement.Action.ProjectId.Should().Be(string.Empty);
    }
}
