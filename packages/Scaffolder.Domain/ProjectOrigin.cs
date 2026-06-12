namespace Scaffolder.Domain;

public enum ProjectOriginKind { Blank, FromGitHub }

public sealed record ProjectOrigin
{
    public required ProjectOriginKind Kind { get; init; }
    public string? SourceRepository { get; init; }  // null for Blank; "owner/repo" for FromGitHub

    public static ProjectOrigin Blank() => new() { Kind = ProjectOriginKind.Blank };
    public static ProjectOrigin FromGitHub(string sourceRepository) =>
        new() { Kind = ProjectOriginKind.FromGitHub, SourceRepository = sourceRepository };

    public string ToApiString() => Kind == ProjectOriginKind.Blank ? "blank" : "github";
    public static ProjectOriginKind KindFromApiString(string s) => s switch
    {
        "blank" => ProjectOriginKind.Blank,
        "github" => ProjectOriginKind.FromGitHub,
        _ => throw new ArgumentException($"Unknown origin kind: {s}")
    };
}
