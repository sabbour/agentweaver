using Agentweaver.Domain;
using Agentweaver.Squad.Catalog;
using Agentweaver.Squad.Squad;

namespace Agentweaver.Api.Workflows;

public sealed record WorkflowRoleRequirement(
    string NodeId,
    string? Role,
    string? Agent,
    IReadOnlyList<string> AvailableRoles);

internal sealed record WorkflowTeamBindingResult(
    WorkflowDefinition Workflow,
    IReadOnlyList<WorkflowRoleRequirement> UnresolvedRoles,
    bool WasBound)
{
    public bool IsResolved => UnresolvedRoles.Count == 0;
}

/// <summary>Resolves authored worker-node roles to the names in a confirmed project team.</summary>
internal static class WorkflowTeamBinding
{
    public static IReadOnlyList<string>? ReadRoles(Project project)
    {
        var team = ReadTeam(project);
        if (team is null) return null;

        var roles = team.Members
            .Where(IsDispatchable)
            .Select(member => member.Role.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return roles.Count == 0 ? null : roles;
    }

    public static WorkflowTeamBindingResult Bind(Project project, WorkflowDefinition workflow)
    {
        var members = ReadTeam(project)?.Members.Where(IsDispatchable).ToList() ?? [];
        var availableRoles = members
            .Select(member => member.Role.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(role => role, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var unresolved = new List<WorkflowRoleRequirement>();
        var wasBound = false;

        var nodes = workflow.Nodes.Select(node =>
        {
            if (!IsAssignableAgentNode(node) ||
                (string.IsNullOrWhiteSpace(node.Role) && string.IsNullOrWhiteSpace(node.Agent)))
                return node;

            var member = members.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, node.Agent, StringComparison.OrdinalIgnoreCase))
                ?? members.FirstOrDefault(candidate =>
                    string.Equals(candidate.Role.Id, node.Role, StringComparison.OrdinalIgnoreCase))
                ?? members.FirstOrDefault(candidate =>
                    string.Equals(candidate.Role.Id, node.Agent, StringComparison.OrdinalIgnoreCase));

            if (member is null)
            {
                unresolved.Add(new WorkflowRoleRequirement(node.Id, node.Role, node.Agent, availableRoles));
                return node;
            }

            wasBound |= !string.Equals(node.Role, member.Role.Id, StringComparison.Ordinal) ||
                         !string.Equals(node.Agent, member.Name, StringComparison.Ordinal);
            return node with { Role = member.Role.Id, Agent = member.Name };
        }).ToList();

        return new WorkflowTeamBindingResult(workflow with { Nodes = nodes }, unresolved, wasBound);
    }

    private static bool IsDispatchable(Agentweaver.Squad.Model.CastMember member) =>
        member.Status == Agentweaver.Squad.Model.CastMemberStatus.Active &&
        !ReservedRoles.IsReserved(member.Role.Id) &&
        !ReservedRoles.IsReserved(member.Name);

    private static bool IsAssignableAgentNode(WorkflowNode node) =>
        node.Type != WorkflowNodeType.BuildTest &&
        NodeClassifier.Classify(node) is NodeKind.Agent or NodeKind.PeerReview;

    private static Agentweaver.Squad.Model.Team? ReadTeam(Project project)
    {
        try
        {
            return new SquadReader(project.WorkingDirectory).ReadTeam();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
