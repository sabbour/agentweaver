namespace Agentweaver.Domain;

/// <summary>
/// The permitted model providers. The deployment selects exactly one source.
/// </summary>
public enum ModelSource
{
    GitHubCopilot,
    Byok,

    [Obsolete("Use Byok. This alias is retained while stored data and public API values migrate.")]
    MicrosoftFoundry = Byok
}

public static class ModelSourceExtensions
{
    public static string ToApiString(this ModelSource src) => src switch
    {
        ModelSource.GitHubCopilot => "github-copilot",
        ModelSource.Byok => "microsoft-foundry",
        _ => throw new ArgumentOutOfRangeException(nameof(src))
    };

    public static ModelSource FromApiString(string s) => s switch
    {
        "github-copilot" => ModelSource.GitHubCopilot,
        "microsoft-foundry" => ModelSource.Byok,
        _ => throw new ArgumentException($"Unknown model source: {s}", nameof(s))
    };
}
