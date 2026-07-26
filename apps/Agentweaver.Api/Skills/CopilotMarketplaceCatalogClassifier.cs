using System.Text;
using System.Text.Json;
using GitHub.Copilot;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.GitHub.Copilot;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Generation;
using Agentweaver.Domain;

namespace Agentweaver.Api.Skills;

/// <summary>
/// Classifies a marketplace repository's skill catalog from its file-tree listing using a single,
/// constrained, tool-less Copilot completion. Used as the bounded, fail-closed fallback in
/// <see cref="MarketplaceCatalogIndexer"/> when the deterministic SKILL.md heuristic finds nothing.
/// </summary>
public interface IMarketplaceCatalogClassifier
{
    /// <summary>
    /// Returns the classifier's proposed catalog entries, or <c>null</c> on any failure/timeout/missing
    /// Copilot scope. The indexer validates every returned <c>location</c> against the real tree before
    /// use, so an inaccurate response can only shrink the catalog, never inject an unreachable skill.
    /// </summary>
    Task<IReadOnlyList<MarketplaceCatalogEntry>?> ClassifyAsync(
        string owner, string repo, string branch, IReadOnlyList<string> treePaths, string? submittingUser, CancellationToken ct);
}

/// <summary>
/// Production <see cref="IMarketplaceCatalogClassifier"/>. Modeled EXACTLY on
/// <see cref="CopilotWorkflowSelectionModel"/> (empty <c>Tools</c> list so the model physically cannot
/// emit tool calls, streaming, no session store, no config discovery, JSON-only charter, dual-path text
/// capture) and on <see cref="CopilotStoryIndependenceClassifier"/>'s bounded-timeout + fail-closed
/// discipline. Requires the submitting user's Copilot-entitled token scope — installation scope is
/// rejected (installation tokens are not Copilot model credentials), so anonymous / no-Copilot callers
/// simply fall back to the heuristic/empty ladder. This is a constrained classifier completion, NOT the
/// agentic assistant loop, so it lives in-process in the API alongside the other classifier precedents.
/// </summary>
public sealed class CopilotMarketplaceCatalogClassifier : IMarketplaceCatalogClassifier
{
    internal static readonly TimeSpan ClassificationTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Hard cap on how many tree paths are fed to the model (token/latency budget).</summary>
    internal const int MaxTreePaths = 1500;

    /// <summary>Cap on how many characters of a single description are kept.</summary>
    internal const int MaxDescriptionLength = 300;

    private const string ClassifierCharter =
        "You are cataloging a GitHub repository of reusable AI \"skills\". You are given a list of file " +
        "paths from the repository. Identify each distinct skill and return its directory location, a " +
        "short name, and a one-line description. The \"location\" MUST be a directory path that appears " +
        "in the provided paths (the directory that contains the skill's manifest). Respond with ONLY a " +
        "single JSON object — no markdown, no code fences, no prose. Exact format: " +
        "{\"skills\":[{\"location\":\"path/to/skill\",\"name\":\"skill-name\",\"description\":\"one line\"}]}. " +
        "If you cannot identify any skills, return {\"skills\":[]}.";

    private readonly GitHubCopilotClientFactory _copilotClientFactory;
    private readonly IGitHubTokenScopeProvider _scopeProvider;
    private readonly ILogger<CopilotMarketplaceCatalogClassifier> _logger;
    private readonly string? _modelId;

    public CopilotMarketplaceCatalogClassifier(
        GitHubCopilotClientFactory copilotClientFactory,
        IGitHubTokenScopeProvider scopeProvider,
        ILogger<CopilotMarketplaceCatalogClassifier> logger,
        IConfiguration configuration,
        IOptions<GenerationModelOptions>? generationOptions = null)
    {
        _copilotClientFactory = copilotClientFactory;
        _scopeProvider = scopeProvider;
        _logger = logger;
        _modelId = (generationOptions?.Value ?? GenerationModelOptions.FromConfiguration(configuration))
            .ResolveReplyClassificationModel();
    }

    public async Task<IReadOnlyList<MarketplaceCatalogEntry>?> ClassifyAsync(
        string owner, string repo, string branch, IReadOnlyList<string> treePaths, string? submittingUser, CancellationToken ct)
    {
        if (treePaths.Count == 0 || string.IsNullOrWhiteSpace(submittingUser))
            return null;

        try
        {
            // Copilot model turns require the submitting user's Copilot-entitled token. Installation
            // scope is not a Copilot model credential and would yield empty/no-auth turns.
            var scope = _scopeProvider.Resolve(submittingUser);
            if (string.Equals(scope.Key, GitHubTokenScope.Installation.Key, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Marketplace catalog classification requires a user Copilot token scope; installation scope is not permitted.");

            var prompt = BuildPrompt(owner, repo, branch, treePaths);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ClassificationTimeout);
            try
            {
                var raw = await RunModelTurnAsync(scope, prompt, timeoutCts.Token).ConfigureAwait(false);
                var parsed = ParseResult(raw);
                _logger.LogInformation(
                    "Marketplace catalog classification for {Owner}/{Repo} produced {Count} candidate(s).",
                    owner, repo, parsed?.Count ?? 0);
                return parsed;
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "Marketplace catalog classification for {Owner}/{Repo} timed out; falling back to heuristic/empty.",
                    owner, repo);
                return null;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Marketplace catalog classification failed for {Owner}/{Repo}; falling back to heuristic/empty.",
                owner, repo);
            return null;
        }
    }

    private async Task<string?> RunModelTurnAsync(GitHubTokenScope scope, string prompt, CancellationToken ct)
    {
        CopilotClient? client = null;
        AIAgent? agent = null;
        try
        {
            client = await _copilotClientFactory.CreateClientAsync(scope, _modelId, ct).ConfigureAwait(false);
            await client.StartAsync(ct).ConfigureAwait(false);

            var sessionConfig = new SessionConfig
            {
                SystemMessage = new SystemMessageConfig
                {
                    Mode = SystemMessageMode.Append,
                    Content = ClassifierCharter,
                },
                Tools = [],
                Model = _modelId,
                EnableConfigDiscovery = false,
                Streaming = true,
                EnableSessionStore = false,
                InfiniteSessions = new InfiniteSessionConfig { Enabled = false },
            };

            agent = client.AsAIAgent(sessionConfig, ownsClient: false, id: null, name: null, description: null);
            var session = await agent.CreateSessionAsync(ct).ConfigureAwait(false);
            return await CopilotWorkflowSelectionModel.CaptureResponseTextAsync(
                agent.RunStreamingAsync(prompt, session, options: null, ct), ct).ConfigureAwait(false);
        }
        finally
        {
            if (agent is IAsyncDisposable disposableAgent)
                await disposableAgent.DisposeAsync().ConfigureAwait(false);
            if (client is IAsyncDisposable disposableClient)
                await disposableClient.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal static string BuildPrompt(string owner, string repo, string branch, IReadOnlyList<string> treePaths)
    {
        var sb = new StringBuilder();
        sb.Append("Repository: ").Append(owner).Append('/').Append(repo).Append(" (branch ").Append(branch).AppendLine(")");
        sb.AppendLine("File paths:");
        var count = 0;
        foreach (var path in treePaths)
        {
            if (count++ >= MaxTreePaths)
            {
                sb.AppendLine("… (truncated)");
                break;
            }
            sb.Append("- ").AppendLine(path);
        }
        sb.AppendLine();
        sb.AppendLine("Return JSON only: {\"skills\":[{\"location\",\"name\",\"description\"}]}.");
        return sb.ToString();
    }

    /// <summary>
    /// Extracts the skills array from the model's JSON response (first <c>{</c> … last <c>}</c>), trimming
    /// and capping descriptions. Returns <c>null</c> on any parse failure (fail-closed).
    /// </summary>
    internal static IReadOnlyList<MarketplaceCatalogEntry>? ParseResult(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return null;

        var start = response.IndexOf('{');
        var end = response.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;

        try
        {
            using var doc = JsonDocument.Parse(response[start..(end + 1)]);
            if (!doc.RootElement.TryGetProperty("skills", out var skills) || skills.ValueKind != JsonValueKind.Array)
                return null;

            var entries = new List<MarketplaceCatalogEntry>();
            foreach (var item in skills.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;
                var location = item.TryGetProperty("location", out var locEl) && locEl.ValueKind == JsonValueKind.String
                    ? locEl.GetString()?.Trim()
                    : null;
                if (string.IsNullOrWhiteSpace(location))
                    continue;
                var name = item.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                    ? nameEl.GetString()?.Trim()
                    : null;
                var description = item.TryGetProperty("description", out var descEl) && descEl.ValueKind == JsonValueKind.String
                    ? Normalize(descEl.GetString())
                    : null;
                entries.Add(new MarketplaceCatalogEntry(location!, name ?? "", description));
            }
            return entries;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var collapsed = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= MaxDescriptionLength ? collapsed : collapsed[..MaxDescriptionLength];
    }
}
