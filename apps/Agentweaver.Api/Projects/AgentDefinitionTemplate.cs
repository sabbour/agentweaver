using System.Reflection;

namespace Agentweaver.Api.Projects;

/// <summary>
/// The code-embedded GitHub Copilot agent definition that drives Agentweaver via its MCP tools.
///
/// The single source of truth for the agent's "## Tool map" is the MCP server source
/// (<c>apps/Agentweaver.Mcp/Tools/*.cs</c>): <c>scripts/gen-docs.mjs</c> regenerates the tool-map
/// block in the repo file <c>.github/agents/agentweaver.agent.md</c> and writes a byte-identical copy
/// to <c>Projects/Templates/agentweaver.agent.md</c>, which this assembly embeds as a resource. CI
/// (<c>.github/workflows/docs-drift.yml</c>) plus a unit test assert the embedded copy never drifts
/// from the committed <c>.github</c> file, so the per-project materialized copy can never go stale.
///
/// It is materialized per-project at instantiation time into
/// <c>&lt;projectWorkingDir&gt;/.github/agents/agentweaver.agent.md</c> (see <see cref="TryMaterialize"/>),
/// mirroring the review-policy / workflow template materialization pattern: best-effort, non-clobbering,
/// and never fatal to project creation. No emojis appear in any shipped surface (Principle VIII).
/// </summary>
public static class AgentDefinitionTemplate
{
    /// <summary>The relative path, within a project working directory, where the agent file is written.</summary>
    public const string RelativeFilePath = ".github/agents/agentweaver.agent.md";

    /// <summary>The manifest resource name of the embedded agent definition (see the API .csproj LogicalName).</summary>
    internal const string ResourceName = "Agentweaver.Api.Projects.Templates.agentweaver.agent.md";

    private static readonly Lazy<string> LazyContent = new(LoadEmbedded);

    /// <summary>The full agent-definition markdown, loaded once from the embedded resource.</summary>
    public static string Content => LazyContent.Value;

    private static string LoadEmbedded()
    {
        var assembly = typeof(AgentDefinitionTemplate).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded agent definition resource '{ResourceName}' was not found. " +
                "Ensure Projects/Templates/agentweaver.agent.md is included as an EmbeddedResource.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Best-effort materialization of the agent definition into a project's working directory at
    /// <see cref="RelativeFilePath"/>. Returns true if the file was written, false if it already existed
    /// (never clobbered — user edits are preserved) or the write failed. Never throws — project creation
    /// must not fail if this write fails.
    /// </summary>
    public static bool TryMaterialize(string workingDirectory, out string? error)
    {
        error = null;
        try
        {
            var path = Path.Combine(workingDirectory, ".github", "agents", "agentweaver.agent.md");
            if (File.Exists(path)) return false;

            var dir = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, Content);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            error = ex.Message;
            return false;
        }
    }
}
