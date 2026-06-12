using System.Text;
using Scaffolder.Squad.Model;

namespace Scaffolder.Squad.Squad;

/// <summary>
/// Renders and parses the <c>.squad/team.md</c> document.
/// </summary>
internal static class TeamMarkdown
{
    public static string Render(Team team, string owner, DateTimeOffset createdAt)
    {
        var sb = new StringBuilder();
        sb.Append("# Squad Team\n\n");
        sb.Append("> ").Append(team.ProjectName).Append("\n\n");
        sb.Append("## Members\n\n");
        sb.Append("| Name | Role | Charter | Status |\n");
        sb.Append("|------|------|---------|--------|\n");
        foreach (var m in team.Members)
        {
            var status = m.Status == CastMemberStatus.Retired ? "retired" : "active";
            var charter = $".squad/agents/{SquadPaths.SlugName(m.Name)}/charter.md";
            sb.Append("| ").Append(m.Name)
              .Append(" | ").Append(m.Role.Title)
              .Append(" | ").Append(charter)
              .Append(" | ").Append(status)
              .Append(" |\n");
        }
        sb.Append('\n');
        sb.Append("## Project Context\n\n");
        sb.Append("- **Project:** ").Append(team.ProjectName).Append('\n');
        sb.Append("- **Universe:** ").Append(team.Universe).Append('\n');
        sb.Append("- **Created:** ").Append(createdAt.ToString("yyyy-MM-dd")).Append('\n');
        sb.Append("- **Requested by:** ").Append(owner).Append('\n');
        return sb.ToString();
    }

    public static Team Parse(string markdown, CastingRegistry registry)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');

        var projectName = string.Empty;
        var universe = string.Empty;
        var members = new List<CastMember>();
        var inMembersTable = false;

        foreach (var raw in lines)
        {
            var line = raw.Trim();

            if (line.StartsWith("> ", StringComparison.Ordinal) && projectName.Length == 0)
                projectName = line[2..].Trim();

            if (line.StartsWith("- **Universe:**", StringComparison.Ordinal))
                universe = line["- **Universe:**".Length..].Trim();

            if (line.StartsWith("- **Project:**", StringComparison.Ordinal) && projectName.Length == 0)
                projectName = line["- **Project:**".Length..].Trim();

            if (line.StartsWith("## Members", StringComparison.Ordinal))
            {
                inMembersTable = true;
                continue;
            }
            if (inMembersTable && line.StartsWith("## ", StringComparison.Ordinal))
                inMembersTable = false;

            if (inMembersTable && line.StartsWith("|", StringComparison.Ordinal))
            {
                var cols = line.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
                if (cols.Length < 4) continue;
                if (string.Equals(cols[0], "Name", StringComparison.OrdinalIgnoreCase)) continue;
                if (cols[0].Length == 0) continue;
                if (cols[0].All(c => c == '-')) continue;

                var name = cols[0];
                var roleTitle = cols[1];
                var charterPath = cols[2];
                var status = string.Equals(cols[3], "retired", StringComparison.OrdinalIgnoreCase)
                    ? CastMemberStatus.Retired
                    : CastMemberStatus.Active;

                var defaultModel = registry.Agents.TryGetValue(name, out var reg) ? reg.DefaultModel : string.Empty;
                var role = new Role(
                    Id: roleTitle.ToLowerInvariant().Replace(' ', '-'),
                    Title: roleTitle,
                    Summary: string.Empty,
                    DefaultModel: defaultModel,
                    Capabilities: [],
                    Responsibilities: [],
                    Boundaries: []);

                var isNamed = !name.StartsWith("member-", StringComparison.OrdinalIgnoreCase);
                members.Add(new CastMember(name, role, charterPath, status, isNamed));
            }
        }

        return new Team(projectName, universe, members);
    }
}
