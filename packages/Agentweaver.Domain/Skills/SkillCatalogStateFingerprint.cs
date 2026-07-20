using System.Security.Cryptography;
using System.Text;

namespace Agentweaver.Domain.Skills;

/// <summary>Canonical, complete fingerprint of the persisted state guarded by defaults apply.</summary>
public static class SkillCatalogStateFingerprint
{
    public static string Compute(
        IEnumerable<Skill> skills,
        IEnumerable<SkillAssignment> assignments)
    {
        var canonical = new StringBuilder();
        foreach (var skill in skills.OrderBy(s => s.Id.ToString(), StringComparer.Ordinal))
        {
            Append(canonical, skill.Id.ToString());
            Append(canonical, skill.ProjectId.ToString());
            Append(canonical, skill.Name);
            Append(canonical, skill.Description);
            Append(canonical, skill.Instructions);
            Append(canonical, skill.Provenance.ToApiString());
            Append(canonical, skill.SourceRepository);
            Append(canonical, skill.SourceLocation);
            Append(canonical, skill.ContentHash);
            Append(canonical, skill.Status.ToApiString());
            foreach (var resource in skill.Resources.OrderBy(r => r.RelativePath, StringComparer.Ordinal))
            {
                Append(canonical, resource.RelativePath);
                Append(canonical, resource.Content);
            }
        }

        canonical.Append("assignments\0");
        foreach (var assignment in assignments
            .OrderBy(a => a.SkillId.ToString(), StringComparer.Ordinal)
            .ThenBy(a => a.AgentName, StringComparer.Ordinal))
        {
            Append(canonical, assignment.ProjectId.ToString());
            Append(canonical, assignment.SkillId.ToString());
            Append(canonical, assignment.AgentName);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    private static void Append(StringBuilder builder, string? value)
    {
        builder.Append(value?.Length ?? -1).Append(':').Append(value).Append('\0');
    }
}
