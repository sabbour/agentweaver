using System.Net;
using Agentweaver.Domain;

namespace Agentweaver.AgentRuntime.Providers;

public enum AgentProviderFailureKind
{
    Authorization,
    Configuration,
    ProviderUnavailable,
    RateLimited,
}

/// <summary>
/// Machine-readable failure from an agent model provider.
/// </summary>
public class AgentProviderException : Exception
{
    public AgentProviderException(
        ModelSource modelSource,
        AgentProviderFailureKind failureKind,
        string errorCode,
        string userMessage,
        bool isRetryable,
        Exception? innerException = null)
        : base(userMessage, innerException)
    {
        ModelSource = modelSource;
        FailureKind = failureKind;
        ErrorCode = errorCode;
        UserMessage = userMessage;
        IsRetryable = isRetryable;
    }

    public ModelSource ModelSource { get; }
    public AgentProviderFailureKind FailureKind { get; }
    public string ErrorCode { get; }
    public string UserMessage { get; }
    public bool IsRetryable { get; }

    public static AgentProviderException? Classify(
        ModelSource modelSource,
        Exception ex,
        string? runId = null)
    {
        if (ex is AgentProviderException providerException)
            return providerException;

        return modelSource switch
        {
            ModelSource.GitHubCopilot => ClassifyGitHubCopilot(ex, runId),
            _ => null,
        };
    }

    private static AgentProviderException? ClassifyGitHubCopilot(Exception ex, string? runId)
    {
        if (GitHubCopilotClientFactory.IsUnauthorized(ex) || ContainsAny(ex,
                "was not created with authentication info",
                "authentication info or custom provider"))
        {
            return new AgentProviderException(
                ModelSource.GitHubCopilot,
                AgentProviderFailureKind.Authorization,
                "github_copilot_auth_required",
                WithRunPrefix(runId, "GitHub Copilot is not authorized for this user. Sign in with a Copilot-entitled GitHub account and retry."),
                isRetryable: false,
                ex);
        }

        if (GitHubCopilotClientFactory.IsRateLimited(ex))
        {
            return new AgentProviderException(
                ModelSource.GitHubCopilot,
                AgentProviderFailureKind.RateLimited,
                "github_copilot_rate_limited",
                WithRunPrefix(runId, "GitHub Copilot rate-limited the model request. Retry after the provider limit resets."),
                isRetryable: true,
                ex);
        }

        if (ContainsAny(ex, "Failed to list models", "list models failed"))
        {
            return new AgentProviderException(
                ModelSource.GitHubCopilot,
                AgentProviderFailureKind.ProviderUnavailable,
                "github_copilot_models_unavailable",
                WithRunPrefix(runId, "GitHub Copilot could not list available models. Verify the user has Copilot model access, the configured Copilot runtime is valid, and GitHub is reachable."),
                isRetryable: false,
                ex);
        }

        if (ContainsAny(ex,
                "unsupported model",
                "model is not supported",
                "model not found",
                "unknown model",
                "invalid model",
                "model does not exist",
                "model is not available",
                "model unavailable"))
        {
            return new AgentProviderException(
                ModelSource.GitHubCopilot,
                AgentProviderFailureKind.Configuration,
                "github_copilot_model_unavailable",
                WithRunPrefix(runId, "The configured GitHub Copilot model is not available for this user. Choose a supported Copilot model and retry."),
                isRetryable: false,
                ex);
        }

        if (ex is FileNotFoundException or DirectoryNotFoundException ||
            ContainsAny(ex, "runtime cli path", "copilot executable", "copilot runtime"))
        {
            return new AgentProviderException(
                ModelSource.GitHubCopilot,
                AgentProviderFailureKind.Configuration,
                "github_copilot_runtime_not_configured",
                WithRunPrefix(runId, "The GitHub Copilot runtime is not configured correctly. Set a valid Copilot CLI runtime path or install the bundled runtime."),
                isRetryable: false,
                ex);
        }

        if (HasTransientStatus(ex))
        {
            return new AgentProviderException(
                ModelSource.GitHubCopilot,
                AgentProviderFailureKind.ProviderUnavailable,
                "github_copilot_provider_unavailable",
                WithRunPrefix(runId, "GitHub Copilot is temporarily unavailable. Retry when the provider is healthy."),
                isRetryable: true,
                ex);
        }

        return null;
    }

    private static bool HasTransientStatus(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException { StatusCode: HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout })
                return true;
        }

        return false;
    }

    private static bool ContainsAny(Exception ex, params string[] needles)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            foreach (var needle in needles)
            {
                if (current.Message.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private static string WithRunPrefix(string? runId, string message) =>
        string.IsNullOrWhiteSpace(runId) ? message : $"Run {runId}: {message}";
}
