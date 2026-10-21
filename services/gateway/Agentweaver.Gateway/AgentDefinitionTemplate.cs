namespace Agentweaver.Gateway;

/// <summary>Reads the operator definition embedded in the Gateway distribution.</summary>
internal static class AgentDefinitionTemplate
{
    // Read once when the type is first used. The text never changes at run time,
    // so each download returns the same cached string without disk access.
    internal static readonly string Content = ReadContent();

    private static string ReadContent()
    {
        // The project file sets this logical name. The Gateway image has no copy of
        // the repository, so the file must ship inside the assembly. A missing resource
        // is a packaging error, so fail fast instead of serving an empty file.
        using var stream = typeof(AgentDefinitionTemplate).Assembly.GetManifestResourceStream(
            "Agentweaver.Gateway.agentweaver.agent.md")
            ?? throw new InvalidOperationException("The embedded Agentweaver operator definition is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
