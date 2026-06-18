namespace Agentweaver.Domain;

/// <summary>
/// The permitted model providers. Exactly two sources are allowed per the
/// constitution (Principle II) and FR-009.
/// </summary>
public enum ModelSource
{
    GitHubCopilot,
    MicrosoftFoundry
}

public static class ModelSourceExtensions
{
    public static string ToApiString(this ModelSource src) => src switch
    {
        ModelSource.GitHubCopilot => "github-copilot",
        ModelSource.MicrosoftFoundry => "microsoft-foundry",
        _ => throw new ArgumentOutOfRangeException(nameof(src))
    };

    public static ModelSource FromApiString(string s) => s switch
    {
        "github-copilot" => ModelSource.GitHubCopilot,
        "microsoft-foundry" => ModelSource.MicrosoftFoundry,
        _ => throw new ArgumentException($"Unknown model source: {s}", nameof(s))
    };
}
