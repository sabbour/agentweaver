using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Agentweaver.Domain;
using Agentweaver.Squad.Model;

namespace Agentweaver.Api.Blueprints;

/// <summary>
/// Lightweight repository-signal based blueprint recommender for the project-creation Suggested tab.
/// It intentionally avoids model calls: public repo metadata is enough for a best-effort first pick,
/// and callers fall back to the template catalog when GitHub analysis is unavailable.
/// </summary>
public sealed class GitHubRepoBlueprintSuggestionService
{
    private static readonly Regex OwnerRepoPattern =
        new(@"(?:github\.com/)?(?<owner>[\w.-]+)/(?<repo>[\w.-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly BlueprintService _blueprints;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IGitHubTokenScopeProvider _scopeProvider;
    private readonly IGitHubAccessTokenProvider _accessTokenProvider;
    private readonly ILogger<GitHubRepoBlueprintSuggestionService> _logger;

    public GitHubRepoBlueprintSuggestionService(
        BlueprintService blueprints,
        IHttpClientFactory httpClientFactory,
        IGitHubTokenScopeProvider scopeProvider,
        IGitHubAccessTokenProvider accessTokenProvider,
        ILogger<GitHubRepoBlueprintSuggestionService> logger)
    {
        _blueprints = blueprints;
        _httpClientFactory = httpClientFactory;
        _scopeProvider = scopeProvider;
        _accessTokenProvider = accessTokenProvider;
        _logger = logger;
    }

    public async Task<SuggestBlueprintResponse> SuggestAsync(
        string repository,
        string? userId,
        CancellationToken ct)
    {
        var catalog = _blueprints.GetPredefined();
        if (!TryParseOwnerRepo(repository, out var owner, out var repo))
            return Fallback("Repository must be an owner/repo string or a GitHub URL.");

        try
        {
            using var http = _httpClientFactory.CreateClient("github");
            var scope = _scopeProvider.Resolve(userId);
            var token = await _accessTokenProvider.GetValidAccessTokenAsync(scope, ct).ConfigureAwait(false);

            var repoInfo = await GetJsonAsync<GitHubRepoInfo>(
                http, $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}", token, ct)
                .ConfigureAwait(false);
            if (repoInfo is null)
                return Fallback("GitHub repository metadata was unavailable.");

            var languages = await GetJsonAsync<Dictionary<string, long>>(
                    http, $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/languages", token, ct)
                .ConfigureAwait(false) ?? [];

            var contents = await GetJsonAsync<GitHubContentItem[]>(
                    http, $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/contents", token, ct)
                .ConfigureAwait(false) ?? [];

            var signals = BuildSignals(repoInfo, languages, contents);
            var (id, confidence, rationale) = PickBlueprint(repoInfo, languages.Keys, contents.Select(c => c.Name), signals);
            var blueprint = catalog.FirstOrDefault(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase))
                ?? catalog.FirstOrDefault(b => string.Equals(b.Id, "blueprint-software-development", StringComparison.OrdinalIgnoreCase))
                ?? catalog.FirstOrDefault();

            return blueprint is null
                ? Fallback("No blueprint templates are available.")
                : new SuggestBlueprintResponse
                {
                    RecommendedBlueprint = BlueprintDto.FromModel(blueprint),
                    Rationale = rationale,
                    Confidence = confidence,
                    Signals = signals,
                    Fallback = false,
                };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            if (ex is OperationCanceledException && ct.IsCancellationRequested) throw;
            _logger.LogWarning(ex, "Could not analyze GitHub repository {Repository} for blueprint suggestion", repository);
            return Fallback("Repository analysis was unavailable; choose a template instead.");
        }

        SuggestBlueprintResponse Fallback(string reason) => new()
        {
            RecommendedBlueprint = catalog.FirstOrDefault() is Blueprint b ? BlueprintDto.FromModel(b) : null,
            Rationale = reason,
            Confidence = 0,
            Signals = [],
            Fallback = true,
        };
    }

    private static async Task<T?> GetJsonAsync<T>(HttpClient http, string url, string? token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Agentweaver/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return default;
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
    }

    private static bool TryParseOwnerRepo(string value, out string owner, out string repo)
    {
        owner = string.Empty;
        repo = string.Empty;
        var match = OwnerRepoPattern.Match(value.Trim().TrimEnd('/'));
        if (!match.Success) return false;
        owner = match.Groups["owner"].Value;
        repo = match.Groups["repo"].Value.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? match.Groups["repo"].Value[..^4]
            : match.Groups["repo"].Value;
        return owner.Length > 0 && repo.Length > 0;
    }

    private static IReadOnlyList<string> BuildSignals(
        GitHubRepoInfo repo,
        IReadOnlyDictionary<string, long> languages,
        IReadOnlyList<GitHubContentItem> contents)
    {
        var signals = new List<string>();
        if (!string.IsNullOrWhiteSpace(repo.Description)) signals.Add($"Description: {repo.Description}");
        if (repo.Topics is { Count: > 0 }) signals.Add($"Topics: {string.Join(", ", repo.Topics.Take(5))}");
        if (languages.Count > 0) signals.Add($"Languages: {string.Join(", ", languages.OrderByDescending(kv => kv.Value).Take(5).Select(kv => kv.Key))}");
        var rootFiles = contents.Select(c => c.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Take(8).ToList();
        if (rootFiles.Count > 0) signals.Add($"Root files: {string.Join(", ", rootFiles)}");
        if (repo.HasIssues) signals.Add("Issues enabled");
        return signals;
    }

    private static (string BlueprintId, double Confidence, string Rationale) PickBlueprint(
        GitHubRepoInfo repo,
        IEnumerable<string> languages,
        IEnumerable<string?> rootNames,
        IReadOnlyList<string> signals)
    {
        var text = string.Join(" ", new[]
        {
            repo.Name,
            repo.Description,
            string.Join(" ", repo.Topics ?? []),
            string.Join(" ", languages),
            string.Join(" ", rootNames.Where(n => !string.IsNullOrWhiteSpace(n))),
        }).ToLowerInvariant();

        bool HasAny(params string[] needles) => needles.Any(n => text.Contains(n, StringComparison.OrdinalIgnoreCase));
        var languageSet = new HashSet<string>(languages.Select(l => l.ToLowerInvariant()));
        var hasCode = languageSet.Count > 0 && !languageSet.SetEquals(["markdown"]);

        if (HasAny("agent", "llm", "copilot", "prompt", "rag", "ai-", "openai", "semantic-kernel", "langchain"))
            return ("blueprint-ai-agent-engineering", 0.86,
                "Recommended because repository metadata points to AI agent or LLM work.");

        if (HasAny("docs", "documentation", "blog", "content", "book", "site", "website") && !hasCode)
            return ("blueprint-content-authoring", 0.78,
                "Recommended because the repository appears focused on documentation or content.");

        if (HasAny("prd", "roadmap", "product", "prototype", "ux", "design") && !hasCode)
            return ("blueprint-product-management", 0.74,
                "Recommended because the repo signals product discovery, design, or planning work.");

        if (HasAny("frontend", "backend", "api", "service", "app", "kubernetes", "terraform", "docker") || hasCode)
            return ("blueprint-software-development", 0.82,
                "Recommended because languages, repo structure, or topics indicate a software codebase.");

        return ("blueprint-software-development", signals.Count > 0 ? 0.58 : 0.35,
            "Recommended as a general software-delivery starting point from the available repository signals.");
    }

    private sealed class GitHubRepoInfo
    {
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("description")] public string? Description { get; init; }
        [JsonPropertyName("topics")] public IReadOnlyList<string>? Topics { get; init; }
        [JsonPropertyName("has_issues")] public bool HasIssues { get; init; }
    }

    private sealed class GitHubContentItem
    {
        [JsonPropertyName("name")] public string? Name { get; init; }
    }
}
