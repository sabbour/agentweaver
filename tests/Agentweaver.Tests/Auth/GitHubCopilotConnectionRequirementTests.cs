using System.Text.Json;
using Agentweaver.Api.Auth;
using Agentweaver.Domain;
using FluentAssertions;

namespace Agentweaver.Tests.Auth;

public sealed class GitHubCopilotConnectionRequirementTests
{
    [Fact]
    public void ForProject_exposes_the_single_redacted_handoff_action_contract()
    {
        var projectId = ProjectId.New();

        var requirement = GitHubCopilotConnectionRequirement.ForProject(projectId);

        requirement.Code.Should().Be(GitHubCopilotConnectionRequirement.RequirementCode);
        requirement.Message.Should().Be(GitHubCopilotConnectionRequirement.RequirementMessage);
        requirement.Action.Type.Should().Be(GitHubCopilotConnectionAction.ConnectProjectCopilotApp);
        requirement.Action.ProjectId.Should().Be(projectId.ToString());
        var json = JsonSerializer.Serialize(requirement);
        json.Should().NotContain("token").And.NotContain("secret")
            .And.NotContain("authorization_url").And.NotContain("transaction")
            .And.NotContain("callback").And.NotContain("repository").And.NotContain("installation");
    }
}
