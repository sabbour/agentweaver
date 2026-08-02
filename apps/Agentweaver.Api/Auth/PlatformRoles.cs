namespace Agentweaver.Api.Auth;

public static class PlatformRoles
{
    public const string PlatformAdmin = "PlatformAdmin";
    public const string ProjectCreator = "ProjectCreator";
    public const string Contributor = "Contributor";
    public const string Viewer = "Viewer";

    private static readonly string[] OrderedRoles =
    [
        PlatformAdmin,
        ProjectCreator,
        Contributor,
        Viewer,
    ];

    public static IReadOnlyList<string> All => OrderedRoles;

    public static bool IsRecognized(string role) =>
        OrderedRoles.Contains(role, StringComparer.Ordinal);

    public static IReadOnlyList<string> FilterRecognized(IEnumerable<string> roles) =>
        OrderedRoles.Where(role => roles.Contains(role, StringComparer.Ordinal)).ToArray();

    public static string? SelectPrimaryRole(IEnumerable<string> roles) =>
        OrderedRoles.FirstOrDefault(role => roles.Contains(role, StringComparer.Ordinal));
}
