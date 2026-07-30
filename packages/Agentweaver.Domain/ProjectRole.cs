namespace Agentweaver.Domain;

public enum ProjectRole
{
    Viewer = 0,
    Contributor = 1,
    Owner = 2,
}

public static class ProjectRoleExtensions
{
    public static string ToApiString(this ProjectRole role) => role switch
    {
        ProjectRole.Owner => "Owner",
        ProjectRole.Contributor => "Contributor",
        _ => "Viewer",
    };

    public static bool TryParse(string? raw, out ProjectRole role)
    {
        role = ProjectRole.Viewer;
        if (string.Equals(raw, "Owner", StringComparison.Ordinal))
        {
            role = ProjectRole.Owner;
            return true;
        }

        if (string.Equals(raw, "Contributor", StringComparison.Ordinal))
        {
            role = ProjectRole.Contributor;
            return true;
        }

        if (string.Equals(raw, "Viewer", StringComparison.Ordinal))
        {
            role = ProjectRole.Viewer;
            return true;
        }

        return false;
    }

    public static ProjectRole Parse(string raw) =>
        TryParse(raw, out var role)
            ? role
            : throw new ArgumentException($"Unsupported project role '{raw}'.", nameof(raw));

    public static bool Satisfies(this ProjectRole actual, ProjectRole minimumRole) => actual >= minimumRole;
}
