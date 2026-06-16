namespace Scaffolder.AgentRuntime.Workflow;

/// <summary>
/// Resolves the on-disk charter for a built-in agent (Scribe, Rai) from the repository's
/// <c>.squad/agents/{name}/charter.md</c>. Tries the lowercase agent name first, then the
/// provided casing. Returns <c>null</c> if no charter file exists.
/// </summary>
public static class BuiltInCharterResolver
{
    /// <summary>
    /// Returns the charter text for <paramref name="agentName"/>, or <c>null</c> if no
    /// charter file is found under <paramref name="repositoryPath"/>.
    /// </summary>
    public static string? Resolve(string repositoryPath, string agentName)
    {
        if (string.IsNullOrEmpty(repositoryPath) || string.IsNullOrEmpty(agentName))
            return null;

        var candidates = new[]
        {
            Path.Combine(repositoryPath, ".squad", "agents", agentName.ToLowerInvariant(), "charter.md"),
            Path.Combine(repositoryPath, ".squad", "agents", agentName, "charter.md"),
        };

        foreach (var path in candidates)
        {
            try
            {
                if (File.Exists(path))
                    return File.ReadAllText(path);
            }
            catch
            {
                // best effort — fall through to next candidate / null
            }
        }

        return null;
    }
}
