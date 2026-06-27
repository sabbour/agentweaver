using System.Text.Json;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Api.Infrastructure.Ef;

public sealed class EfProjectStore : IProjectStore
{
    private readonly IDbContextFactory<MemoryDbContext> _factory;
    private readonly ILogger<EfProjectStore>? _logger;

    public EfProjectStore(IDbContextFactory<MemoryDbContext> factory, ILogger<EfProjectStore>? logger = null)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task InsertAsync(Project project, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.Projects.Add(ToRecord(project));
        await db.SaveChangesAsync(ct);
    }

    public async Task<Project?> GetAsync(ProjectId id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rec = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.ProjectId == id.ToString(), ct);
        return rec is null ? null : FromRecord(rec);
    }

    public async Task<IReadOnlyList<Project>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var recs = await db.Projects.AsNoTracking().OrderByDescending(p => p.CreatedAt).ToListAsync(ct);
        return recs.Select(r => FromRecord(r)).ToList();
    }

    public async Task UpdateNameAsync(ProjectId id, string name, DateTimeOffset updatedAt, CancellationToken ct = default)
    {
        var pid = id.ToString();
        await using var db = await _factory.CreateDbContextAsync(ct);
        await db.Projects.Where(p => p.ProjectId == pid)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Name, name)
                .SetProperty(p => p.UpdatedAt, updatedAt), ct);
    }

    public async Task UpdateProviderSettingsAsync(ProjectId id, ProjectProviderSettings settings, DateTimeOffset updatedAt, CancellationToken ct = default)
    {
        var pid = id.ToString();
        await using var db = await _factory.CreateDbContextAsync(ct);
        await db.Projects.Where(p => p.ProjectId == pid)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.DefaultProvider, settings.DefaultProvider.ToApiString())
                .SetProperty(p => p.DefaultModelCopilot, settings.GitHubCopilotModel)
                .SetProperty(p => p.DefaultModelFoundry, settings.MicrosoftFoundryModel)
                .SetProperty(p => p.UpdatedAt, updatedAt), ct);
    }

    public async Task UpdateWorkingDirectoryAsync(ProjectId id, string workingDirectory, string defaultBranch, DateTimeOffset updatedAt, CancellationToken ct = default)
    {
        var pid = id.ToString();
        await using var db = await _factory.CreateDbContextAsync(ct);
        await db.Projects.Where(p => p.ProjectId == pid)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.WorkingDirectory, workingDirectory)
                .SetProperty(p => p.DefaultBranch, defaultBranch)
                .SetProperty(p => p.UpdatedAt, updatedAt), ct);
    }

    public async Task<bool> TryBeginDeleteAsync(ProjectId id, CancellationToken ct = default)
    {
        var pid = id.ToString();
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rows = await db.Projects
            .Where(p => p.ProjectId == pid && p.State == "active")
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.State, "deleting"), ct);
        return rows > 0;
    }

    public async Task DeleteAsync(ProjectId id, CancellationToken ct = default)
    {
        var pid = id.ToString();
        await using var db = await _factory.CreateDbContextAsync(ct);
        await db.Projects.Where(p => p.ProjectId == pid).ExecuteDeleteAsync(ct);
    }

    public async Task UpdatePickupSettingsAsync(
        ProjectId id, int maxReadyPerHeartbeat, bool autopilot, bool autoApproveTools, DateTimeOffset updatedAt, CancellationToken ct = default)
    {
        var pid = id.ToString();
        await using var db = await _factory.CreateDbContextAsync(ct);
        await db.Projects.Where(p => p.ProjectId == pid)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.MaxReadyPerHeartbeat, maxReadyPerHeartbeat)
                .SetProperty(p => p.PickupAutopilot, autopilot)
                .SetProperty(p => p.PickupAutoApproveTools, autoApproveTools)
                .SetProperty(p => p.UpdatedAt, updatedAt), ct);
    }

    public async Task UpdateDefaultWorkflowAsync(ProjectId id, string? workflowId, DateTimeOffset updatedAt, CancellationToken ct = default)
    {
        var pid = id.ToString();
        await using var db = await _factory.CreateDbContextAsync(ct);
        await db.Projects.Where(p => p.ProjectId == pid)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.DefaultWorkflowId, workflowId)
                .SetProperty(p => p.UpdatedAt, updatedAt), ct);
    }

    public async Task UpdateActiveReviewPolicyAsync(ProjectId id, string? policyName, DateTimeOffset updatedAt, CancellationToken ct = default)
    {
        var pid = id.ToString();
        await using var db = await _factory.CreateDbContextAsync(ct);
        await db.Projects.Where(p => p.ProjectId == pid)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.ActiveReviewPolicyName, policyName)
                .SetProperty(p => p.UpdatedAt, updatedAt), ct);
    }

    public async Task UpdateSandboxProfileAsync(ProjectId id, string? sandboxProfile, DateTimeOffset updatedAt, CancellationToken ct = default)
    {
        var pid = id.ToString();
        await using var db = await _factory.CreateDbContextAsync(ct);
        await db.Projects.Where(p => p.ProjectId == pid)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.SandboxProfile, sandboxProfile)
                .SetProperty(p => p.UpdatedAt, updatedAt), ct);
    }

    public async Task UpdateSourceBlueprintAsync(ProjectId id, string? blueprintId, string? blueprintType, DateTimeOffset updatedAt, CancellationToken ct = default)
    {
        var pid = id.ToString();
        await using var db = await _factory.CreateDbContextAsync(ct);
        await db.Projects.Where(p => p.ProjectId == pid)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.SourceBlueprintId, blueprintId)
                .SetProperty(p => p.SourceBlueprintType, blueprintType)
                .SetProperty(p => p.UpdatedAt, updatedAt), ct);
    }

    public async Task UpdateAllowedWorkflowIdsAsync(ProjectId id, IReadOnlyList<string>? allowedWorkflowIds, DateTimeOffset updatedAt, CancellationToken ct = default)
    {
        var pid = id.ToString();
        var json = allowedWorkflowIds is { Count: > 0 } ? JsonSerializer.Serialize(allowedWorkflowIds) : null;
        await using var db = await _factory.CreateDbContextAsync(ct);
        await db.Projects.Where(p => p.ProjectId == pid)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.AllowedWorkflowIds, json)
                .SetProperty(p => p.UpdatedAt, updatedAt), ct);
    }

    private static ProjectRecord ToRecord(Project p) => new()
    {
        ProjectId = p.Id.ToString(),
        Name = p.Name,
        OriginKind = p.Origin.ToApiString(),
        SourceRepository = p.Origin.SourceRepository,
        WorkingDirectory = p.WorkingDirectory,
        DefaultBranch = p.DefaultBranch,
        Owner = p.Owner,
        DefaultProvider = p.ProviderSettings.DefaultProvider.ToApiString(),
        DefaultModelCopilot = p.ProviderSettings.GitHubCopilotModel,
        DefaultModelFoundry = p.ProviderSettings.MicrosoftFoundryModel,
        State = p.State == ProjectState.Deleting ? "deleting" : "active",
        CreatedAt = p.CreatedAt,
        UpdatedAt = p.UpdatedAt,
        MaxReadyPerHeartbeat = p.MaxReadyPerHeartbeat,
        PickupAutopilot = p.PickupAutopilot,
        PickupAutoApproveTools = p.PickupAutoApproveTools,
        DefaultWorkflowId = p.DefaultWorkflowId,
        ActiveReviewPolicyName = p.ActiveReviewPolicyName,
        SandboxProfile = p.SandboxProfile,
        SourceBlueprintId = p.SourceBlueprintId,
        SourceBlueprintType = p.SourceBlueprintType,
        AllowedWorkflowIds = p.AllowedWorkflowIds is { Count: > 0 } ? JsonSerializer.Serialize(p.AllowedWorkflowIds) : null,
    };

    private Project FromRecord(ProjectRecord r)
    {
        var originKind = ProjectOrigin.KindFromApiString(r.OriginKind);
        var origin = originKind == ProjectOriginKind.FromGitHub
            ? ProjectOrigin.FromGitHub(r.SourceRepository!)
            : ProjectOrigin.Blank();

        IReadOnlyList<string>? allowedIds = null;
        if (!string.IsNullOrWhiteSpace(r.AllowedWorkflowIds))
        {
            try { allowedIds = JsonSerializer.Deserialize<List<string>>(r.AllowedWorkflowIds); }
            catch (JsonException ex) { _logger?.LogWarning(ex, "Invalid allowed_workflow_ids for project {ProjectId}", r.ProjectId); }
        }

        return new Project
        {
            Id = ProjectId.Parse(r.ProjectId),
            Name = r.Name,
            Origin = origin,
            WorkingDirectory = r.WorkingDirectory,
            DefaultBranch = r.DefaultBranch,
            Owner = r.Owner,
            ProviderSettings = new ProjectProviderSettings
            {
                DefaultProvider = ModelSourceExtensions.FromApiString(r.DefaultProvider),
                GitHubCopilotModel = r.DefaultModelCopilot,
                MicrosoftFoundryModel = r.DefaultModelFoundry,
            },
            State = r.State == "deleting" ? ProjectState.Deleting : ProjectState.Active,
            CreatedAt = r.CreatedAt,
            UpdatedAt = r.UpdatedAt,
            MaxReadyPerHeartbeat = r.MaxReadyPerHeartbeat,
            PickupAutopilot = r.PickupAutopilot,
            PickupAutoApproveTools = r.PickupAutoApproveTools,
            DefaultWorkflowId = r.DefaultWorkflowId,
            ActiveReviewPolicyName = r.ActiveReviewPolicyName,
            SandboxProfile = r.SandboxProfile,
            SourceBlueprintId = r.SourceBlueprintId,
            SourceBlueprintType = r.SourceBlueprintType,
            AllowedWorkflowIds = allowedIds is { Count: > 0 } ? allowedIds : null,
        };
    }
}
