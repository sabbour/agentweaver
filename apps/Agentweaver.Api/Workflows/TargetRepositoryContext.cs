using System.Text.RegularExpressions;

namespace Agentweaver.Api.Workflows;

internal static partial class TargetRepositoryContext
{
    public static string Describe(string description, string? explicitTarget)
    {
        var target = Normalize(explicitTarget) ?? ExtractFromDescription(description);
        return target ?? "(none)";
    }

    private static string? Normalize(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        var trimmed = target.Trim();
        if (trimmed.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed["https://github.com/".Length..];
        if (trimmed.StartsWith("http://github.com/", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed["http://github.com/".Length..];

        var parts = trimmed.Split(['/', '#', '?'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $"{parts[0]}/{parts[1]}" : trimmed;
    }

    private static string? ExtractFromDescription(string description)
    {
        var match = GitHubUrlRegex().Match(description);
        return match.Success ? $"{match.Groups["owner"].Value}/{match.Groups["repo"].Value}" : null;
    }

    [GeneratedRegex(@"https?://github\.com/(?<owner>[^/\s)]+)/(?<repo>[^/\s)]+)", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubUrlRegex();
}
