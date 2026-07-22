using System.Text;
using System.Text.Json;
using GitHub.Copilot;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.GitHub.Copilot;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Api.Generation;
using Agentweaver.Domain;

namespace Agentweaver.Api.Coordinator;

public sealed record StoryComponentInput(
    string StoryKey,
    string Title,
    string Scope,
    IReadOnlyList<string> DependsOnStoryKeys);

public sealed record StoryIndependenceClassificationContext(
    string RunId,
    string? ProjectId,
    string SubmittingUser,
    string DesiredOutcome,
    string? Scope,
    string? Assumptions,
    IReadOnlyList<StoryComponentInput> ComponentStories,
    IReadOnlyList<StoryComponentInput> OtherStories);

public sealed record StoryIndependenceClassificationResult(
    bool IsIndependentDeliverable,
    string IndependenceRationale);

public interface IStoryIndependenceClassifier
{
    Task<StoryIndependenceClassificationResult?> ClassifyAsync(
        StoryIndependenceClassificationContext context,
        CancellationToken ct);
}

public sealed class CopilotStoryIndependenceClassifier : IStoryIndependenceClassifier
{
    // Keep the outer classifier deadline aligned with AgentRuntime's established 30-second
    // default operation window instead of pre-empting otherwise healthy Copilot turns at 8 seconds.
    internal static readonly TimeSpan ClassificationTimeout = TimeSpan.FromSeconds(30);
    internal const int MaxClassificationAttempts = 2;

    private const string ClassifierCharter =
        "You are deciding whether a dependency-connected group of stories from a PRD decomposition " +
        "is a genuinely independent deliverable that should become its own backlog run. Promote only " +
        "when the WHOLE component is a coherent, separately shippable product/service/deliverable " +
        "relative to the rest of the initiative. Keep inline when the component is merely technical " +
        "layers, implementation aspects, or internal phases of one larger deliverable. Example: " +
        "\"storefront frontend + storefront backend\" is NOT independent; \"storefront\" and " +
        "\"pipeline service\" CAN be independent. Respond with ONLY one JSON object, no prose or " +
        "markdown: {\"is_independent_deliverable\": true|false, \"independence_rationale\": " +
        "\"short explanation\"}. If unsure, choose false.";

    private readonly GitHubCopilotClientFactory _copilotClientFactory;
    private readonly IGitHubTokenScopeProvider _scopeProvider;
    private readonly ILogger<CopilotStoryIndependenceClassifier> _logger;
    private readonly string? _modelId;

    public CopilotStoryIndependenceClassifier(
        GitHubCopilotClientFactory copilotClientFactory,
        IGitHubTokenScopeProvider scopeProvider,
        ILogger<CopilotStoryIndependenceClassifier> logger,
        IConfiguration configuration,
        IOptions<GenerationModelOptions>? generationOptions = null)
    {
        _copilotClientFactory = copilotClientFactory;
        _scopeProvider = scopeProvider;
        _logger = logger;
        _modelId = (generationOptions?.Value ?? GenerationModelOptions.FromConfiguration(configuration))
            .ResolveReplyClassificationModel();
    }

    public async Task<StoryIndependenceClassificationResult?> ClassifyAsync(
        StoryIndependenceClassificationContext context,
        CancellationToken ct)
    {
        if (context.ComponentStories.Count == 0 || string.IsNullOrWhiteSpace(context.SubmittingUser))
            return null;

        try
        {
            var scope = _scopeProvider.Resolve(context.SubmittingUser);
            if (string.Equals(scope.Key, GitHubTokenScope.Installation.Key, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Story-independence classification requires a user Copilot token scope; installation scope is not permitted.");

            var prompt = BuildPrompt(context);
            var result = await RunWithRetryAsync(
                token => RunModelTurnAsync(scope, prompt, token),
                ClassificationTimeout,
                MaxClassificationAttempts,
                ct,
                onTimeout: attempt => _logger.LogWarning(
                    "Story-independence classification for run {RunId} timed out on attempt {Attempt}/{MaxAttempts}.",
                    context.RunId,
                    attempt,
                    MaxClassificationAttempts),
                onRetryableError: (ex, attempt) => _logger.LogWarning(
                    ex,
                    "Story-independence classification for run {RunId} failed on attempt {Attempt}/{MaxAttempts}; retrying once.",
                    context.RunId,
                    attempt,
                    MaxClassificationAttempts)).ConfigureAwait(false);

            _logger.LogInformation(
                "Story-independence classification for run {RunId}: {Decision}",
                context.RunId,
                result?.IsIndependentDeliverable.ToString() ?? "unparseable/timed-out");
            if (result is null)
            {
                _logger.LogWarning(
                    "Story-independence classification for run {RunId} exhausted its bounded attempts; failing closed to inline.",
                    context.RunId);
            }
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Story-independence classification failed for run {RunId}; caller will fail closed to inline.",
                context.RunId);
            return null;
        }
    }

    internal static async Task<StoryIndependenceClassificationResult?> RunWithTimeoutAsync(
        Func<CancellationToken, Task<string?>> modelTurn,
        TimeSpan timeout,
        CancellationToken ct,
        Action? onTimeout = null)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            return ParseResult(await modelTurn(timeoutCts.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            onTimeout?.Invoke();
            return null;
        }
    }

    internal static async Task<StoryIndependenceClassificationResult?> RunWithRetryAsync(
        Func<CancellationToken, Task<string?>> modelTurn,
        TimeSpan timeout,
        int maxAttempts,
        CancellationToken ct,
        Action<int>? onTimeout = null,
        Action<Exception, int>? onRetryableError = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var timedOut = false;
            try
            {
                var result = await RunWithTimeoutAsync(
                    modelTurn,
                    timeout,
                    ct,
                    onTimeout: () =>
                    {
                        timedOut = true;
                        onTimeout?.Invoke(attempt);
                    }).ConfigureAwait(false);

                if (result is not null || !timedOut || attempt == maxAttempts)
                    return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                onRetryableError?.Invoke(ex, attempt);
            }
        }

        return null;
    }

    private async Task<string?> RunModelTurnAsync(
        GitHubTokenScope scope,
        string prompt,
        CancellationToken ct)
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

    internal static string BuildPrompt(StoryIndependenceClassificationContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Decide whether the TARGET COMPONENT below is a genuinely independent deliverable.");
        sb.AppendLine("Independence means separately shippable/coherent relative to the rest of the initiative.");
        sb.AppendLine("If it is only a technical layer/aspect/phase of one larger deliverable, return false.");
        sb.AppendLine();
        sb.AppendLine("OVERALL PRD CONTEXT:");
        sb.AppendLine($"desired_outcome: {context.DesiredOutcome}");
        if (!string.IsNullOrWhiteSpace(context.Scope))
            sb.AppendLine($"scope: {context.Scope}");
        if (!string.IsNullOrWhiteSpace(context.Assumptions))
            sb.AppendLine($"assumptions: {context.Assumptions}");
        sb.AppendLine();
        sb.AppendLine("TARGET COMPONENT:");
        AppendStories(sb, context.ComponentStories);
        sb.AppendLine();
        sb.AppendLine("REMAINDER OF DECOMPOSITION:");
        AppendStories(sb, context.OtherStories);
        sb.AppendLine();
        sb.AppendLine("Return JSON only with keys \"is_independent_deliverable\" and \"independence_rationale\".");
        return sb.ToString();
    }

    internal static StoryIndependenceClassificationResult? ParseResult(string? response)
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
            var root = doc.RootElement;
            if (!root.TryGetProperty("is_independent_deliverable", out var isIndependentElement)
                || (isIndependentElement.ValueKind != JsonValueKind.True && isIndependentElement.ValueKind != JsonValueKind.False))
                return null;
            if (!root.TryGetProperty("independence_rationale", out var rationaleElement)
                || rationaleElement.ValueKind != JsonValueKind.String)
                return null;

            var rationale = rationaleElement.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(rationale))
                return null;

            return new StoryIndependenceClassificationResult(isIndependentElement.GetBoolean(), rationale);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void AppendStories(StringBuilder sb, IReadOnlyList<StoryComponentInput> stories)
    {
        if (stories.Count == 0)
        {
            sb.AppendLine("- none");
            return;
        }

        foreach (var story in stories)
        {
            sb.Append("- ").Append(story.StoryKey).Append(": ").AppendLine(story.Title);
            sb.Append("  scope: ").AppendLine(story.Scope);
            sb.Append("  depends_on: ").AppendLine(
                story.DependsOnStoryKeys.Count == 0 ? "[]" : $"[{string.Join(", ", story.DependsOnStoryKeys)}]");
        }
    }
}
