namespace Agentweaver.Domain;

public sealed record ProjectProviderSettings
{
    public required ModelSource DefaultProvider { get; init; }
    public string? GitHubCopilotModel { get; init; }
    public string? MicrosoftFoundryModel { get; init; }
}
