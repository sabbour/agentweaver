namespace Agentweaver.Gateway;

/// <summary>Reads the operator definition embedded in the Gateway distribution.</summary>
internal static class AgentDefinitionTemplate
{
    internal static readonly string Content = ReadContent();

    private static string ReadContent()
    {
        using var stream = typeof(AgentDefinitionTemplate).Assembly.GetManifestResourceStream(
            "Agentweaver.Gateway.agentweaver.agent.md")
            ?? throw new InvalidOperationException("The embedded Agentweaver operator definition is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
