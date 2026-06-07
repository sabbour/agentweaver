namespace Scaffolder.Api.Configuration;

/// <summary>
/// Root configuration options for the Scaffolder API.
/// </summary>
public sealed class ScaffolderOptions
{
    public const string SectionName = "Scaffolder";

    /// <summary>
    /// Absolute (or relative-to-CWD) path to the git repository that the agent
    /// will edit. When null or empty, the service auto-detects the repo root by
    /// running <c>git rev-parse --show-toplevel</c> from the process working
    /// directory. Override this when the API process starts in a location that
    /// is not inside the target repository.
    /// </summary>
    public string? RepoRoot { get; init; }

    /// <summary>
    /// Absolute (or relative-to-RepoRoot) path to the directory where per-run
    /// git worktrees are created.
    /// </summary>
    public required string RunRoot { get; init; }

    /// <summary>
    /// Default maximum number of agent loop steps per run.
    /// </summary>
    public int DefaultMaxSteps { get; init; } = 200;

    /// <summary>
    /// Default maximum wall-clock duration per run in seconds.
    /// </summary>
    public int DefaultMaxDurationSeconds { get; init; } = 1800;

    /// <summary>
    /// Model source provider configurations.
    /// </summary>
    public ModelSourceOptions ModelSources { get; init; } = new();
}

/// <summary>
/// Configuration for the supported model-source providers.
/// </summary>
public sealed class ModelSourceOptions
{
    /// <summary>
    /// GitHub Copilot SDK adapter configuration.
    /// </summary>
    public CopilotSdkOptions CopilotSdk { get; init; } = new();

    /// <summary>
    /// Microsoft Foundry adapter configuration.
    /// </summary>
    public MicrosoftFoundryOptions MicrosoftFoundry { get; init; } = new();
}

/// <summary>
/// GitHub Copilot SDK provider configuration.
/// Credentials come from environment variables, not this file.
/// </summary>
public sealed class CopilotSdkOptions
{
    /// <summary>
    /// Whether this provider is enabled.
    /// </summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>
/// Microsoft Foundry provider configuration.
/// Credentials come from environment variables, not this file.
/// </summary>
public sealed class MicrosoftFoundryOptions
{
    /// <summary>
    /// Whether this provider is enabled.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Azure AI Foundry endpoint URL.
    /// Read from environment variable SCAFFOLDER_FOUNDRY_ENDPOINT.
    /// </summary>
    public string? Endpoint { get; init; }
}
