namespace Agentweaver.Domain.Skills;

/// <summary>
/// How a catalog skill entered the project catalog. Recorded per skill so users can tell where a
/// skill came from and whether it can be re-synced from its source.
/// </summary>
public enum SkillProvenance
{
    /// <summary>Imported from an arbitrary Git repository (clone + checkout).</summary>
    RepoImport,

    /// <summary>Uploaded directly as a file/folder/archive (no live source to re-sync).</summary>
    FileUpload,

    /// <summary>Discovered/synced from the project's connected repository at a recognized location.</summary>
    ConnectedRepoSync,
}

public static class SkillProvenanceExtensions
{
    public static string ToApiString(this SkillProvenance p) => p switch
    {
        SkillProvenance.RepoImport => "repo-import",
        SkillProvenance.FileUpload => "file-upload",
        SkillProvenance.ConnectedRepoSync => "connected-repo-sync",
        _ => throw new ArgumentOutOfRangeException(nameof(p)),
    };

    public static SkillProvenance ParseProvenance(string s) => s switch
    {
        "repo-import" => SkillProvenance.RepoImport,
        "file-upload" => SkillProvenance.FileUpload,
        "connected-repo-sync" => SkillProvenance.ConnectedRepoSync,
        _ => throw new ArgumentException($"Unknown skill provenance: {s}"),
    };
}
