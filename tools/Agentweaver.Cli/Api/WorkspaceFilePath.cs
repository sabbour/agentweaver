namespace Agentweaver.Cli.Api;

/// <summary>
/// Applies the Gateway and Orchestrator rules for a Workspace repository file path.
/// </summary>
/// <remarks>
/// Keep these rules equal to <c>GatewayOwnerClient.ExpandWorkspaceFilePath</c> and
/// <c>IsSafeWorkspaceFilePath</c>. The CLI uses them before network use and before it
/// sends a catch-all path parameter.
/// </remarks>
internal static class WorkspaceFilePath
{
    private const int MaximumLength = 1024;
    private const int MaximumSegments = 64;
    private const int MaximumDecodeDepth = 8;

    public static bool IsSafe(string? path)
    {
        if (path is null ||
            path.Contains("%2f", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("%5c", StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.IsNullOrWhiteSpace(path) ||
            path.Length > MaximumLength ||
            path.StartsWith('/') ||
            path.EndsWith('/') ||
            path.Contains('\\') ||
            path.Any(char.IsControl) ||
            path.Split('/').Length > MaximumSegments ||
            HasUnsafeSegment(path))
            return false;
        var candidate = path;
        for (var depth = 0; depth < MaximumDecodeDepth; depth++)
        {
            var decoded = Uri.UnescapeDataString(candidate);
            if (string.Equals(decoded, candidate, StringComparison.Ordinal))
                return true;
            if (decoded.Count(character => character == '/') != candidate.Count(character => character == '/') ||
                decoded.Contains('\\') ||
                decoded.Any(char.IsControl) ||
                HasUnsafeSegment(decoded))
                return false;
            candidate = decoded;
        }
        return string.Equals(Uri.UnescapeDataString(candidate), candidate, StringComparison.Ordinal);
    }

    private static bool HasUnsafeSegment(string path) =>
        path.Split('/').Any(segment => segment is "" or "." or "..");
}
