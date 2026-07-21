using System.Text.RegularExpressions;

namespace Agentweaver.Api.Infrastructure;

public interface IAppVersionProvider
{
    /// <summary>
    /// Base semver, e.g. "0.9.70". For a real release build this is the released
    /// version (from IMAGE_TAG); otherwise it falls back to the VERSION file baked
    /// into the image, so a SHA-tagged local/upgrade build still shows what semver
    /// line it's based on.
    /// </summary>
    string Version { get; }

    /// <summary>
    /// Short git SHA the running image was built from, or null when unavailable
    /// (e.g. a real release build, or running outside the container build pipeline).
    /// </summary>
    string? GitSha { get; }

    /// <summary>
    /// True only when the running image was tagged with a real semver release tag
    /// (via `npm run azure:release`), as opposed to a git-SHA-tagged upgrade/local
    /// deploy build.
    /// </summary>
    bool IsRelease { get; }
}

public class AppVersionProvider : IAppVersionProvider
{
    // Matches "0.9.70" or "v0.9.70" — a real release tag, not a short git SHA like "a1c11f1".
    private static readonly Regex SemverTagRegex = new(@"^v?\d+\.\d+\.\d+$", RegexOptions.Compiled);

    public string Version { get; }
    public string? GitSha { get; }
    public bool IsRelease { get; }

    public AppVersionProvider(IWebHostEnvironment env)
    {
        var fileVersion = ReadVersionFile(env);

        var imageTag = Environment.GetEnvironmentVariable("IMAGE_TAG");
        var gitSha = Environment.GetEnvironmentVariable("GIT_SHA");

        IsRelease = !string.IsNullOrWhiteSpace(imageTag) && SemverTagRegex.IsMatch(imageTag);

        var normalizedGitSha = string.IsNullOrWhiteSpace(gitSha) || gitSha.Equals("unknown", StringComparison.OrdinalIgnoreCase)
            ? null
            : ShortenSha(gitSha);

        if (IsRelease)
        {
            // The IMAGE_TAG *is* the authoritative version for a real release build
            // (it's what `azure:release` bumped VERSION to and tagged) — prefer it over
            // whatever happens to be baked into the image's VERSION file, and there's no
            // need to show a SHA suffix for a tagged release.
            Version = imageTag!.TrimStart('v');
            GitSha = null;
        }
        else
        {
            // Local `dotnet run` (no IMAGE_TAG/GIT_SHA set) or a git-SHA-tagged
            // `azure:upgrade`/`azure:deploy-from-local` build: fall back to the
            // VERSION file for the base semver, and surface the git SHA (if present)
            // separately so the two pieces of information aren't collapsed together.
            Version = fileVersion;
            GitSha = normalizedGitSha;
        }
    }

    // GIT_SHA is plumbed in as the full 40-char commit SHA (see
    // scripts/azure/steps/20-build-push-images.mjs -> git.currentGitSha({ cwd }).full),
    // but the repo's established convention (e.g. IMAGE_TAG) is the 7-char short SHA
    // (lib/git.mjs's currentGitSha() -> short: full.slice(0, 7)). Truncate here so the
    // version badge matches that convention; leave shorter values untouched.
    private static string ShortenSha(string sha) => sha.Length > 7 ? sha[..7] : sha;

    private static string ReadVersionFile(IWebHostEnvironment env)
    {
        // Try reading VERSION file from content root or repo root
        var versionFile = Path.Combine(env.ContentRootPath, "VERSION");
        if (!File.Exists(versionFile))
            versionFile = Path.Combine(env.ContentRootPath, "..", "VERSION");
        if (!File.Exists(versionFile))
            versionFile = Path.Combine(env.ContentRootPath, "..", "..", "VERSION");

        return File.Exists(versionFile)
            ? File.ReadAllText(versionFile).Trim()
            : "0.0.0";
    }
}
