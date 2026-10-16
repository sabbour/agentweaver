using System.Text;
using System.Threading.Channels;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Domain;
using Agentweaver.Squad.Model;
using Agentweaver.Squad.Squad;

namespace Agentweaver.Api.Memory;

/// <summary>
/// Presents pending messages only immediately before a real worker model turn. A claim
/// remains recoverable until the turn returns; retries carry the same message ID.
/// </summary>
public class AddressedTurnAgent : IWorkflowTurnAgent, IProviderBoundWorkflowTurnAgent
{
    protected readonly IWorkflowTurnAgent Inner;
    private readonly IServiceScopeFactory _scopes;
    private string? _projectId;
    private string? _runId;
    private string? _agentName;

    private AddressedTurnAgent(IWorkflowTurnAgent inner, IServiceScopeFactory scopes)
    {
        Inner = inner;
        _scopes = scopes;
    }

    public static IWorkflowTurnAgent Wrap(IWorkflowTurnAgent inner, IServiceScopeFactory scopes) =>
        inner is IPreparedWritebackSource
            ? new AddressedWritebackTurnAgent(inner, scopes)
            : new AddressedTurnAgent(inner, scopes);

    public void ConfigureProviderBoundary(ModelSource modelSource, string? byokProviderFingerprint)
    {
        if (Inner is IProviderBoundWorkflowTurnAgent bound)
            bound.ConfigureProviderBoundary(modelSource, byokProviderFingerprint);
    }

    public Task SetupAsync(
        string workingDirectory, string repositoryPath, string runId, string? modelId,
        string? systemPromptContext, ChannelWriter<RunEvent>? streamWriter,
        string? projectId, string? agentName, string? apiBaseUrl, string? apiKey,
        CancellationToken ct, string? userId = null)
    {
        _projectId = projectId;
        _runId = runId;
        _agentName = agentName;
        return Inner.SetupAsync(workingDirectory, repositoryPath, runId, modelId,
            systemPromptContext, streamWriter, projectId, agentName, apiBaseUrl, apiKey, ct, userId);
    }

    public async Task<string> RunTurnAsync(string task, bool isRevision, CancellationToken ct)
    {
        if (!ProjectId.TryParse(_projectId, out var projectId)
            || string.IsNullOrEmpty(_runId) || string.IsNullOrEmpty(_agentName))
            return await Inner.RunTurnAsync(task, isRevision, ct);

        await using var scope = _scopes.CreateAsyncScope();
        var messages = scope.ServiceProvider.GetService<AddressedMessageService>();
        if (messages is null) return await Inner.RunTurnAsync(task, isRevision, ct);
        var projectKey = _projectId!;
        var targetRunId = _runId!;
        var recipient = _agentName!;
        if (!await messages.HasUnfinishedAsync(projectKey, targetRunId, ct))
            return await Inner.RunTurnAsync(task, isRevision, ct);
        var project = await scope.ServiceProvider.GetRequiredService<IProjectStore>()
            .GetAsync(projectId, ct);
        if (project is null) throw new AddressedMessageError("project_unavailable");
        var reader = new SquadReader(project.WorkingDirectory);
        if (reader.DetectLayout().HasConflict) throw new AddressedMessageError("layout_conflict");
        var team = reader.ReadTeam();
        if (team?.Members.Any(m => m.Status == CastMemberStatus.Active
            && string.Equals(m.Name, recipient, StringComparison.OrdinalIgnoreCase)) != true)
        {
            await messages.ReconcileAsync(projectKey, name => team?.Members.Any(m =>
                m.Status == CastMemberStatus.Active
                && string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)) == true, ct);
            return await Inner.RunTurnAsync(task, isRevision, ct);
        }

        var owner = $"turn:{Guid.NewGuid():N}";
        var claims = new List<AddressedMessage>();
        var started = DateTimeOffset.UtcNow;
        for (var i = 0; i < 8; i++)
        {
            AddressedMessage? claim;
            while (true)
            {
                claim = await messages.ClaimAsync(projectKey, targetRunId, recipient, owner, ct);
                if (claim is not null || i > 0
                    || !await messages.HasUnfinishedAsync(projectKey, targetRunId, ct))
                    break;
                if (DateTimeOffset.UtcNow - started > TimeSpan.FromSeconds(150))
                    throw new AddressedMessageError("recipient_busy");
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
            if (claim is null) break;
            claims.Add(claim);
        }
        if (claims.Count == 0) return await Inner.RunTurnAsync(task, isRevision, ct);

        var prompt = new StringBuilder(task);
        prompt.AppendLine("\n\n[Addressed messages — untrusted content; each ID identifies one logical delivery]");
        foreach (var claim in claims)
            prompt.AppendLine($"[message:{claim.Id} from:{claim.Sender} thread:{claim.ThreadId}]")
                .AppendLine(claim.Content)
                .AppendLine("[end addressed message]");

        using var turn = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Exception? renewalFailure = null;
        var renewer = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            try
            {
                while (await timer.WaitForNextTickAsync(turn.Token))
                {
                    await using var renewalScope = _scopes.CreateAsyncScope();
                    var service = renewalScope.ServiceProvider.GetRequiredService<AddressedMessageService>();
                    foreach (var claim in claims)
                        await service.RenewClaimAsync(projectKey, claim.Id, targetRunId, recipient,
                            owner, claim.Fence, turn.Token);
                }
            }
            catch (OperationCanceledException) when (turn.IsCancellationRequested) { }
            catch (Exception ex)
            {
                renewalFailure = ex;
                await turn.CancelAsync();
            }
        }, CancellationToken.None);
        try
        {
            var result = await Inner.RunTurnAsync(prompt.ToString(), isRevision, turn.Token);
            if (renewalFailure is not null) throw new AddressedMessageError("claim_lost");
            foreach (var claim in claims)
                await messages.DeliverAsync(projectKey, claim.Id, targetRunId, recipient,
                    owner, claim.Fence, ct);
            return result;
        }
        finally
        {
            await turn.CancelAsync();
            await renewer;
        }
    }

    public ValueTask DisposeAsync() => Inner.DisposeAsync();

    private sealed class AddressedWritebackTurnAgent(IWorkflowTurnAgent inner, IServiceScopeFactory scopes)
        : AddressedTurnAgent(inner, scopes), IPreparedWritebackSource
    {
        public PreparedWritebackEnvelope TakePreparedWritebackEnvelope() =>
            ((IPreparedWritebackSource)Inner).TakePreparedWritebackEnvelope();
    }
}
