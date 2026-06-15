using System.Text.Json;
using Scaffolder.Api.Contracts;
using Scaffolder.Domain;
using Scaffolder.Squad.Analysis;
using Scaffolder.Squad.Catalog;
using Scaffolder.Squad.Model;
using Scaffolder.Squad.Naming;
using Scaffolder.Squad.Squad;
using Scaffolder.Squad.Sync;

namespace Scaffolder.Api.Casting;

/// <summary>
/// Application service for all scenario-based team casting and team read/edit operations.
/// All business logic lives here; endpoint handlers remain thin.
/// </summary>
public sealed class CastingService
{
    private readonly IProjectStore _projectStore;
    private readonly CatalogReader _catalog;
    private readonly CastProposalStore _proposalStore;
    private readonly IAgentRunner _agentRunner;
    private readonly ProjectSignalScanner _signalScanner;
    private readonly ILogger<CastingService> _logger;

    public CastingService(
        IProjectStore projectStore,
        CatalogReader catalog,
        CastProposalStore proposalStore,
        IAgentRunner agentRunner,
        ProjectSignalScanner signalScanner,
        ILogger<CastingService> logger)
    {
        _projectStore = projectStore;
        _catalog = catalog;
        _proposalStore = proposalStore;
        _agentRunner = agentRunner;
        _signalScanner = signalScanner;
        _logger = logger;
    }

    // -----------------------------------------------------------------------
    // Proposal helpers
    // -----------------------------------------------------------------------

    private async Task<(Project Project, string Owner)> LoadProjectAsync(
        string projectId, CancellationToken ct)
    {
        if (!ProjectId.TryParse(projectId, out var pid))
            throw new ArgumentException($"Invalid project id '{projectId}'.", nameof(projectId));

        var project = await _projectStore.GetAsync(pid, ct).ConfigureAwait(false);
        if (project is null)
            throw new ProjectNotFoundException(projectId);

        if (project.State == ProjectState.Deleting)
            throw new ProjectUnavailableException(projectId);

        return (project, project.Owner);
    }

    // -----------------------------------------------------------------------
    // Phase 1 — scenario casting
    // -----------------------------------------------------------------------

    /// <summary>Returns all role archetypes from the catalog, sorted by title.</summary>
    public IReadOnlyList<RoleDto> GetAllRoles()
    {
        return _catalog.LoadAllRoles()
            .OrderBy(r => r.Title)
            .Select(CastingMappings.ToDto)
            .ToList();
    }

    /// <summary>
    /// Builds a cast proposal from a catalog team template. Does not write any files.
    /// A new proposal supersedes any previous pending proposal for the same project.
    /// </summary>
    public async Task<(CastProposal Proposal, string ProjectOwner)> ProposeScenarioCastAsync(
        string projectId,
        string templateId,
        string? universeOverride,
        CancellationToken ct)
    {
        var (project, owner) = await LoadProjectAsync(projectId, ct).ConfigureAwait(false);

        var template = _catalog.LoadTemplate(templateId);
        if (template is null)
            throw new ArgumentException($"Unknown team template '{templateId}'.", nameof(templateId));

        var reader = new SquadReader(project.WorkingDirectory);

        SquadLayoutInfo layout;
        try
        {
            layout = reader.DetectLayout();
        }
        catch (LayoutConflictException ex)
        {
            throw new SquadLayoutConflictException(ex.Message);
        }

        if (layout.HasConflict)
            throw new SquadLayoutConflictException(
                layout.MigrationNote ?? "Conflicting canonical and legacy squad layouts detected.");

        bool existingTeamPresent = reader.TeamExists();

        var policy = reader.ReadPolicy();
        var registry = reader.ReadRegistry();
        var history = reader.ReadHistory();

        var reservedNames = new HashSet<string>(
            registry.Agents.Keys, StringComparer.OrdinalIgnoreCase);

        var allocator = new UniverseAllocator(policy);

        string universe;
        if (!string.IsNullOrWhiteSpace(universeOverride))
        {
            universe = allocator.IsValidUniverse(universeOverride)
                ? universeOverride
                : allocator.ProposeUniverse(history.UniverseUsageHistory, projectId);
        }
        else
        {
            universe = allocator.ProposeUniverse(history.UniverseUsageHistory, projectId);
        }

        var allocations = allocator.AllocateNames(universe, reservedNames, template.Roles.Count);

        var compiler = new CharterCompiler(_catalog);
        var proposedMembers = new List<ProposedMember>();

        for (var i = 0; i < template.Roles.Count; i++)
        {
            var role = template.Roles[i];
            var (name, isNamed) = allocations[i];
            string charter;
            try
            {
                charter = compiler.Compile(role.Id, name);
            }
            catch (InvalidOperationException)
            {
                charter = compiler.CompileCustom(name, role.Title, role.Summary,
                    role.Capabilities, role.Responsibilities, role.Boundaries);
            }

            proposedMembers.Add(new ProposedMember(
                ProposedName: name,
                Role: role,
                CharterMarkdown: charter,
                IsNamed: isNamed,
                DefaultModel: role.DefaultModel,
                Justification: null));
        }

        var proposal = new CastProposal(
            ProposalId: Guid.NewGuid().ToString("N"),
            Mode: CastMode.Scenario,
            Universe: universe,
            Members: proposedMembers,
            ExistingTeamPresent: existingTeamPresent,
            RunId: null,
            Warnings: [],
            Rationale: template.Description);

        _proposalStore.Store(projectId, proposal, owner);

        _logger.LogInformation(
            "Created scenario proposal {ProposalId} for project {ProjectId}, template {TemplateId}",
            proposal.ProposalId, projectId, templateId);

        return (proposal, owner);
    }

    // -----------------------------------------------------------------------
    // Phase 2b — manual casting (no AI, explicit role IDs)
    // -----------------------------------------------------------------------

    public async Task<(CastProposal Proposal, string ProjectOwner)> ProposeManualCastAsync(
        string projectId,
        IReadOnlyList<string> roleIds,
        string? universeOverride,
        CancellationToken ct)
    {
        var (project, owner) = await LoadProjectAsync(projectId, ct).ConfigureAwait(false);

        var reader = new SquadReader(project.WorkingDirectory);

        bool existingTeamPresent = reader.TeamExists();

        var policy = reader.ReadPolicy();
        var registry = reader.ReadRegistry();
        var history = reader.ReadHistory();

        var reservedNames = new HashSet<string>(
            registry.Agents.Keys, StringComparer.OrdinalIgnoreCase);

        var allocator = new UniverseAllocator(policy);

        string universe;
        if (!string.IsNullOrWhiteSpace(universeOverride))
        {
            universe = allocator.IsValidUniverse(universeOverride)
                ? universeOverride
                : allocator.ProposeUniverse(history.UniverseUsageHistory, projectId);
        }
        else
        {
            universe = allocator.ProposeUniverse(history.UniverseUsageHistory, projectId);
        }

        var allocations = allocator.AllocateNames(universe, reservedNames, roleIds.Count);

        var compiler = new CharterCompiler(_catalog);
        var proposedMembers = new List<ProposedMember>();

        for (var i = 0; i < roleIds.Count; i++)
        {
            var roleId = roleIds[i];
            var role = _catalog.LoadRole(roleId);
            if (role is null)
            {
                _logger.LogWarning("Manual cast: unknown role id '{RoleId}'; skipping", roleId);
                continue;
            }

            var (name, isNamed) = allocations[i];
            string charter;
            try
            {
                charter = compiler.Compile(role.Id, name);
            }
            catch (InvalidOperationException)
            {
                charter = compiler.CompileCustom(name, role.Title, role.Summary,
                    role.Capabilities, role.Responsibilities, role.Boundaries);
            }

            proposedMembers.Add(new ProposedMember(
                ProposedName: name,
                Role: role,
                CharterMarkdown: charter,
                IsNamed: isNamed,
                DefaultModel: role.DefaultModel,
                Justification: $"Manually selected role: {role.Title}"));
        }

        var proposal = new CastProposal(
            ProposalId: Guid.NewGuid().ToString("N"),
            Mode: CastMode.Manual,
            Universe: universe,
            Members: proposedMembers,
            ExistingTeamPresent: existingTeamPresent,
            RunId: null,
            Warnings: [],
            Rationale: $"Team manually configured with {proposedMembers.Count} role{(proposedMembers.Count != 1 ? "s" : "")}.");

        _proposalStore.Store(projectId, proposal, owner);

        _logger.LogInformation(
            "Created manual proposal {ProposalId} for project {ProjectId} with {Count} members",
            proposal.ProposalId, projectId, proposedMembers.Count);

        return (proposal, owner);
    }

    // -----------------------------------------------------------------------
    // Phase 3 / 4 — model-assisted casting (read-only proposal-generation run)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Model-assisted free-text casting. Runs in the read-only proposal-generation run mode.
    /// Provider is always GitHub Copilot (fixed); model_id overrides the role/agent default.
    /// Returns the proposal held in memory — no .squad/ files are written until confirm.
    /// </summary>
    public Task<(CastProposal Proposal, string ProjectOwner)> ProposeFreetextCastAsync(
        string projectId,
        string goal,
        string? universeOverride,
        string? modelId,
        CancellationToken ct,
        int? teamSize = null)
    {
        if (string.IsNullOrWhiteSpace(goal))
            throw new ArgumentException("Goal must not be empty.", nameof(goal));

        if (goal.Length > 2000)
            throw new ArgumentException("Goal must be 2000 characters or fewer.", nameof(goal));

        var effectiveGoal = goal;

        return BuildModelAssistedProposalAsync(
            projectId,
            CastMode.FreeText,
            universeOverride,
            modelId,
            buildPrompt: roles => CastingPrompts.FreeText(effectiveGoal, roles, teamSize),
            extraWarnings: [],
            ct);
    }

    /// <summary>
    /// Model-assisted analysis-based casting. Scans the project for signals, then runs in the
    /// read-only proposal-generation run mode. Provider is always GitHub Copilot (fixed);
    /// model_id overrides the role/agent default. No .squad/ files are written until confirm.
    /// </summary>
    public Task<(CastProposal Proposal, string ProjectOwner)> ProposeAnalysisCastAsync(
        string projectId,
        string? universeOverride,
        string? modelId,
        CancellationToken ct,
        int? teamSize = null)
    {
        return BuildModelAssistedProposalAsync(
            projectId,
            CastMode.Analysis,
            universeOverride,
            modelId,
            buildPrompt: null, // built inside, needs the scanned signals
            extraWarnings: null,
            ct,
            analysisMode: true,
            teamSize: teamSize);
    }

    private async Task<(CastProposal Proposal, string ProjectOwner)> BuildModelAssistedProposalAsync(
        string projectId,
        CastMode mode,
        string? universeOverride,
        string? modelId,
        Func<IReadOnlyList<Role>, string>? buildPrompt,
        IReadOnlyList<string>? extraWarnings,
        CancellationToken ct,
        bool analysisMode = false,
        int? teamSize = null)
    {
        var (project, owner) = await LoadProjectAsync(projectId, ct).ConfigureAwait(false);

        var reader = new SquadReader(project.WorkingDirectory);

        SquadLayoutInfo layout;
        try
        {
            layout = reader.DetectLayout();
        }
        catch (LayoutConflictException ex)
        {
            throw new SquadLayoutConflictException(ex.Message);
        }

        if (layout.HasConflict)
            throw new SquadLayoutConflictException(
                layout.MigrationNote ?? "Conflicting canonical and legacy squad layouts detected.");

        bool existingTeamPresent = reader.TeamExists();

        var policy = reader.ReadPolicy();
        var registry = reader.ReadRegistry();
        var history = reader.ReadHistory();

        var reservedNames = new HashSet<string>(
            registry.Agents.Keys, StringComparer.OrdinalIgnoreCase);

        var allocator = new UniverseAllocator(policy);

        string universe;
        if (!string.IsNullOrWhiteSpace(universeOverride))
        {
            universe = allocator.IsValidUniverse(universeOverride)
                ? universeOverride
                : allocator.ProposeUniverse(history.UniverseUsageHistory, projectId);
        }
        else
        {
            universe = allocator.ProposeUniverse(history.UniverseUsageHistory, projectId);
        }

        var availableRoles = _catalog.LoadTemplates()
            .SelectMany(g => g.Roles)
            .DistinctBy(r => r.Id)
            .ToList();

        var warnings = new List<string>(extraWarnings ?? []);

        string prompt;
        if (analysisMode)
        {
            var signals = _signalScanner.Scan(project.WorkingDirectory);
            prompt = signals.HasNoSignals
                ? CastingPrompts.AnalysisFallback(availableRoles, teamSize)
                : CastingPrompts.Analysis(_signalScanner.Summarize(signals), availableRoles, teamSize);

            if (signals.HasNoSignals)
                warnings.Add("No project signals detected — using general-purpose defaults.");
        }
        else
        {
            prompt = buildPrompt!(availableRoles);
        }

        var runId = Guid.NewGuid().ToString("N");

        string result;
        try
        {
            result = await _agentRunner.ExecuteAsync(
                task: prompt,
                workingDirectory: project.WorkingDirectory,
                repositoryPath: project.WorkingDirectory,
                modelSource: ModelSource.GitHubCopilot,
                runId: runId,
                modelId: modelId,
                stream: null,
                ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Model-assisted casting run {RunId} failed for project {ProjectId}", runId, projectId);
            throw new ModelRunFailedException("The casting model run failed to complete.");
        }

        var (rationale, selections) = ParseRoleSelections(result);
        if (selections.Count == 0)
        {
            _logger.LogWarning(
                "Model-assisted casting run {RunId} produced no parseable role selections for project {ProjectId}",
                runId, projectId);
            throw new ModelRunFailedException("The casting model did not return a valid role selection.");
        }

        var resolved = new List<(Role Role, string? Reason)>();
        foreach (var sel in selections)
        {
            var role = _catalog.LoadRole(sel.RoleId);
            if (role is null)
            {
                _logger.LogWarning(
                    "Model selected unrecognized role id '{RoleId}' for run {RunId}; skipping",
                    sel.RoleId, runId);
                continue;
            }
            resolved.Add((role, sel.Reason));
        }

        if (resolved.Count == 0)
            throw new ModelRunFailedException("The casting model selected no recognized role ids.");

        var allocations = allocator.AllocateNames(universe, reservedNames, resolved.Count);

        var compiler = new CharterCompiler(_catalog);
        var proposedMembers = new List<ProposedMember>();

        for (var i = 0; i < resolved.Count; i++)
        {
            var (role, reason) = resolved[i];
            var (name, isNamed) = allocations[i];

            string charter;
            try
            {
                charter = compiler.Compile(role.Id, name);
            }
            catch (InvalidOperationException)
            {
                charter = compiler.CompileCustom(name, role.Title, role.Summary,
                    role.Capabilities, role.Responsibilities, role.Boundaries);
            }

            proposedMembers.Add(new ProposedMember(
                ProposedName: name,
                Role: role,
                CharterMarkdown: charter,
                IsNamed: isNamed,
                DefaultModel: role.DefaultModel,
                Justification: string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()));
        }

        var proposal = new CastProposal(
            ProposalId: Guid.NewGuid().ToString("N"),
            Mode: mode,
            Universe: universe,
            Members: proposedMembers,
            ExistingTeamPresent: existingTeamPresent,
            RunId: runId,
            Warnings: warnings,
            Rationale: rationale);

        _proposalStore.Store(projectId, proposal, owner);

        _logger.LogInformation(
            "Created {Mode} proposal {ProposalId} for project {ProjectId} from run {RunId} with {Count} members",
            mode, proposal.ProposalId, projectId, runId, proposedMembers.Count);

        return (proposal, owner);
    }

    private sealed record RoleSelection(string RoleId, string? Reason);

    private static (string? Rationale, List<RoleSelection> Selections) ParseRoleSelections(string modelOutput)
    {
        if (string.IsNullOrWhiteSpace(modelOutput))
            return (null, []);

        // Try new object format first: {"rationale": "...", "roles": [...]}
        var jsonObj = ExtractJsonObject(modelOutput);
        if (jsonObj is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonObj);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    string? rationale = null;
                    if (doc.RootElement.TryGetProperty("rationale", out var rProp) &&
                        rProp.ValueKind == JsonValueKind.String)
                        rationale = rProp.GetString()?.Trim();

                    List<RoleSelection> selections = [];
                    if (doc.RootElement.TryGetProperty("roles", out var rolesProp) &&
                        rolesProp.ValueKind == JsonValueKind.Array)
                        selections = ParseRoleArray(rolesProp);

                    if (selections.Count > 0)
                        return (rationale, selections);
                }
            }
            catch (JsonException) { }
        }

        // Fall back to old bare-array format for backward compat
        var jsonArr = ExtractJsonArray(modelOutput);
        if (jsonArr is null)
            return (null, []);

        try
        {
            using var doc = JsonDocument.Parse(jsonArr);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return (null, []);
            return (null, ParseRoleArray(doc.RootElement));
        }
        catch (JsonException)
        {
            return (null, []);
        }
    }

    private static List<RoleSelection> ParseRoleArray(JsonElement arrayElement)
    {
        var result = new List<RoleSelection>();
        foreach (var element in arrayElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
                continue;

            string? roleId = null;
            if (element.TryGetProperty("role_id", out var idProp) &&
                idProp.ValueKind == JsonValueKind.String)
                roleId = idProp.GetString();

            if (string.IsNullOrWhiteSpace(roleId))
                continue;

            string? reason = null;
            if (element.TryGetProperty("reason", out var reasonProp) &&
                reasonProp.ValueKind == JsonValueKind.String)
                reason = reasonProp.GetString();

            result.Add(new RoleSelection(roleId.Trim(), reason));
        }
        return result;
    }

    private static string? ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;
        return text[start..(end + 1)];
    }

    private static string? ExtractJsonArray(string text)
    {
        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        if (start < 0 || end < 0 || end <= start)
            return null;
        return text.Substring(start, end - start + 1);
    }

    /// <summary>
    /// Updates members or universe on a pending proposal. Does not write any files.
    /// </summary>
    public CastProposal AmendProposal(
        string projectId,
        string proposalId,
        IReadOnlyList<ProposedMember>? members,
        string? universe)
    {
        var (existing, owner) = _proposalStore.Get(projectId, proposalId);
        if (existing is null)
            throw new ProposalNotFoundException(proposalId);

        var updated = existing with
        {
            Members = members ?? existing.Members,
            Universe = universe ?? existing.Universe,
        };

        _proposalStore.Store(projectId, updated, owner!);
        return updated;
    }

    /// <summary>
    /// Discards the pending proposal for a project.
    /// </summary>
    public void RejectProposal(string projectId, string proposalId)
    {
        var removed = _proposalStore.Remove(projectId, proposalId);
        if (!removed)
            throw new ProposalNotFoundException(proposalId);
    }

    /// <summary>
    /// Validates and commits a pending proposal: writes .squad/ files, appends events.
    /// </summary>
    public async Task<Team> ConfirmProposalAsync(
        string projectId,
        string proposalId,
        string? intent,
        CancellationToken ct)
    {
        var (proposal, _) = _proposalStore.Get(projectId, proposalId);
        if (proposal is null)
            throw new ProposalNotFoundException(proposalId);

        var (project, owner) = await LoadProjectAsync(projectId, ct).ConfigureAwait(false);

        if (proposal.ExistingTeamPresent && intent is null)
            throw new RequiresChoiceException(
                "Existing team detected. Provide intent: new, augment, or recast.");

        var resolvedIntent = intent?.ToLowerInvariant() switch
        {
            "new" => CastIntent.New,
            "augment" => CastIntent.Augment,
            "recast" => CastIntent.Recast,
            null => CastIntent.New,
            _ => throw new ArgumentException($"Invalid intent '{intent}'. Must be new, augment, or recast.", nameof(intent))
        };

        var reader = new SquadReader(project.WorkingDirectory);
        var writer = new SquadWriter(project.WorkingDirectory);

        Team? existingTeam = proposal.ExistingTeamPresent ? reader.ReadTeam() : null;

        List<CastMember> finalMembers;
        List<string> addedNames;
        List<string> retiredNames;

        var proposedByName = proposal.Members
            .ToDictionary(m => m.ProposedName, StringComparer.OrdinalIgnoreCase);

        if (resolvedIntent == CastIntent.Augment && existingTeam is not null)
        {
            var existingNames = new HashSet<string>(
                existingTeam.Members.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);

            finalMembers = [.. existingTeam.Members];
            addedNames = [];

            foreach (var pm in proposal.Members)
            {
                if (!existingNames.Contains(pm.ProposedName))
                {
                    var member = new CastMember(
                        Name: pm.ProposedName,
                        Role: pm.Role,
                        CharterPath: CharterPath(pm.ProposedName),
                        Status: CastMemberStatus.Active,
                        IsNamed: pm.IsNamed);
                    finalMembers.Add(member);
                    addedNames.Add(pm.ProposedName);
                }
            }
            retiredNames = [];
        }
        else if (resolvedIntent == CastIntent.Recast && existingTeam is not null)
        {
            var existingByName = existingTeam.Members
                .ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);

            finalMembers = [];
            addedNames = [];
            retiredNames = [];

            foreach (var pm in proposal.Members)
            {
                var member = new CastMember(
                    Name: pm.ProposedName,
                    Role: pm.Role,
                    CharterPath: CharterPath(pm.ProposedName),
                    Status: CastMemberStatus.Active,
                    IsNamed: pm.IsNamed);
                finalMembers.Add(member);

                if (!existingByName.ContainsKey(pm.ProposedName))
                    addedNames.Add(pm.ProposedName);
            }

            foreach (var existing in existingTeam.Members)
            {
                if (!proposedByName.ContainsKey(existing.Name))
                    retiredNames.Add(existing.Name);
            }
        }
        else
        {
            finalMembers = proposal.Members.Select(pm => new CastMember(
                Name: pm.ProposedName,
                Role: pm.Role,
                CharterPath: CharterPath(pm.ProposedName),
                Status: CastMemberStatus.Active,
                IsNamed: pm.IsNamed)).ToList();

            addedNames = finalMembers.Select(m => m.Name).ToList();
            retiredNames = existingTeam?.Members.Select(m => m.Name).ToList() ?? [];
        }

        // Built-in MAF agents (Scribe, Ralph, Rai) are always part of every team.
        // They are never proposed by the user — the framework provisions them automatically.
        var builtinRoles = new (string Name, string RoleTitle, string RoleId)[]
        {
            ("Scribe", "Scribe", "scribe"),
            ("Ralph", "Work Monitor", "work-monitor"),
            ("Rai", "RAI Reviewer", "rai-reviewer"),
        };

        foreach (var (name, title, roleId) in builtinRoles)
        {
            if (finalMembers.Any(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)))
                continue;

            finalMembers.Add(new CastMember(
                Name: name,
                Role: new Role(roleId, title, $"Built-in {title} agent.", "claude-sonnet-4.6", [], [], []),
                CharterPath: CharterPath(name.ToLower()),
                Status: CastMemberStatus.Active,
                IsNamed: false));
        }

        var team = new Team(
            ProjectName: project.Name,
            Universe: proposal.Universe,
            Members: finalMembers);

        var now = DateTimeOffset.UtcNow;

        writer.WriteTeam(team, owner, now, proposal.Rationale);
        writer.WriteRouting(BuildRoutingMd(team));

        writer.EnsureSquadDirectories();

        if (!writer.ConfigExists())
            writer.WriteConfig("{\n  \"version\": 1\n}\n");

        writer.EnsureIdentityFiles(project.Name, now);
        writer.EnsureFirstRunMarker(now);

        // Provision RAI policy and audit trail (idempotent)
        if (!writer.RaiPolicyExists())
        {
            var raiPolicy = _catalog.LoadRaiPolicyTemplate();
            writer.WriteRaiPolicy(raiPolicy ?? "# RAI Policy\n\nSee .squad/rai/policy.md for RAI check configuration.\n");
        }
        writer.EnsureRaiAuditTrail();

        if (!writer.DecisionsExist())
        {
            writer.WriteDecisions(
                "# Squad Decisions\n\n" +
                "## Active Decisions\n\n" +
                "No decisions recorded yet.\n\n" +
                "## Governance\n\n" +
                "- All meaningful changes require team consensus\n" +
                "- Document architectural decisions here\n" +
                "- Keep history focused on work, decisions focused on direction\n");
        }

        var chartersByName = proposal.Members
            .ToDictionary(m => m.ProposedName, m => m.CharterMarkdown, StringComparer.OrdinalIgnoreCase);

        foreach (var member in finalMembers.Where(m => m.Status == CastMemberStatus.Active))
        {
            if (chartersByName.TryGetValue(member.Name, out var charter))
                writer.WriteCharter(member.Name.ToLower(), charter);
        }

        foreach (var retiredName in retiredNames)
            writer.ArchiveMemberCharter(retiredName);

        // Seed history.md for each new member (idempotent — skip if already exists)
        foreach (var member in finalMembers.Where(m => addedNames.Contains(m.Name) && m.Status == CastMemberStatus.Active))
        {
            if (writer.HistoryExists(member.Name.ToLower())) continue;

            var historyContent =
                $"# Project Context\n\n" +
                $"- **Owner:** {owner}\n" +
                $"- **Project:** {project.Name}\n" +
                (string.IsNullOrWhiteSpace(proposal.Rationale) ? "" : $"- **Description:** {proposal.Rationale}\n") +
                $"- **Created:** {now:yyyy-MM-dd}\n\n" +
                $"## Core Context\n\n" +
                $"Agent {member.Name} initialized and ready for work on {project.Name}.\n\n" +
                $"## Recent Updates\n\n" +
                $"Team initialized on {now:yyyy-MM-dd}.\n\n" +
                $"## Learnings\n\n" +
                "Initial setup complete.\n";

            writer.WriteAgentHistory(member.Name.ToLower(), historyContent);
        }

        // Provision the built-in MAF agents (Scribe, Ralph, Rai). They are never cast by the
        // user — the framework creates them automatically so every project has ready-to-use
        // session-logging, work-monitoring, and responsible-AI agents. Idempotent on augment/recast.
        ProvisionBuiltinAgents(writer, team, owner, project.Name);

        writer.EnsureGitAttributes();
        writer.EnsureGitIgnoreEntries();

        var builtinNames = new HashSet<string>(
            builtinRoles.Select(b => b.Name), StringComparer.OrdinalIgnoreCase);

        foreach (var member in finalMembers.Where(m => m.Status == CastMemberStatus.Active && !builtinNames.Contains(m.Name)))
        {
            var registryMember = new RegistryMember(
                Name: member.Name,
                PersistentName: member.Name.ToLower(),
                Universe: team.Universe,
                DefaultModel: member.Role.DefaultModel,
                Status: CastMemberStatus.Active,
                CreatedAt: now,
                PreviousName: null,
                SucceededBy: null,
                RetiredAt: null,
                CharterPath: member.CharterPath);
            writer.AppendRegistryEvent(registryMember);
        }

        // Register the built-in agents so they appear in the registry alongside cast members.
        foreach (var (name, title, roleId) in builtinRoles)
        {
            var builtinRegistryMember = new RegistryMember(
                Name: name,
                PersistentName: name.ToLower(),
                Universe: team.Universe,
                DefaultModel: "claude-sonnet-4.6",
                Status: CastMemberStatus.Active,
                CreatedAt: now,
                PreviousName: null,
                SucceededBy: null,
                RetiredAt: null,
                CharterPath: CharterPath(name.ToLower()));
            writer.AppendRegistryEvent(builtinRegistryMember);
        }

        foreach (var retiredName in retiredNames)
        {
            var retiredMember = new RegistryMember(
                Name: retiredName,
                PersistentName: retiredName.ToLower(),
                Universe: team.Universe,
                DefaultModel: string.Empty,
                Status: CastMemberStatus.Retired,
                CreatedAt: now,
                PreviousName: null,
                SucceededBy: null,
                RetiredAt: now,
                CharterPath: null);
            writer.AppendRegistryEvent(retiredMember);
        }

        var snapshot = new CastSnapshot(
            SnapshotId: Guid.NewGuid().ToString("N"),
            Universe: team.Universe,
            Mode: proposal.Mode,
            Intent: resolvedIntent,
            Members: finalMembers.Select(m => m.Name).ToList(),
            AddedMembers: addedNames,
            RetiredMembers: retiredNames,
            CreatedAt: now);

        writer.AppendHistoryEvent(snapshot);
        writer.RegenerateCanonicalJson();

        _proposalStore.Remove(projectId, proposalId);

        _logger.LogInformation(
            "Confirmed proposal {ProposalId} for project {ProjectId}, intent {Intent}, {Count} members",
            proposalId, projectId, resolvedIntent, finalMembers.Count);

        return team;
    }

    // -----------------------------------------------------------------------
    // Built-in agent provisioning
    // -----------------------------------------------------------------------

    /// <summary>
    /// Provisions the built-in MAF agents (Scribe, Ralph, Rai). These are system-level agents
    /// that every project gets automatically — they are never part of a cast proposal. For each
    /// built-in, writes both the <c>.squad/agents/{name}/charter.md</c> and the first-class
    /// <c>.github/agents/{name}.agent.md</c> MAF definition. All writes are idempotent.
    /// </summary>
    private void ProvisionBuiltinAgents(SquadWriter writer, Team team, string owner, string projectName)
    {
        var builtins = new[] { ("scribe", "Scribe"), ("ralph", "Ralph"), ("rai", "Rai") };

        foreach (var (agentId, displayName) in builtins)
        {
            // Write .squad/agents/{name}/charter.md (existing pattern)
            if (!writer.CharterExists(agentId))
            {
                try
                {
                    var compiler = new CharterCompiler(_catalog);
                    string charter;
                    try
                    {
                        charter = compiler.Compile(agentId, displayName);
                    }
                    catch (InvalidOperationException)
                    {
                        // No catalog charter template — use MAF content as charter fallback
                        var mafContent = _catalog.LoadMafAgentTemplate(agentId);
                        charter = mafContent ?? $"# {displayName}\n\nBuilt-in Squad agent.\n";
                    }
                    writer.WriteCharter(agentId, charter);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to provision built-in {Agent} charter.", displayName);
                }
            }

            // Write .github/agents/{name}.agent.md (MAF agent definition)
            if (!writer.MafAgentExists(agentId))
            {
                try
                {
                    var mafContent = _catalog.LoadMafAgentTemplate(agentId);
                    if (mafContent is not null)
                        writer.WriteMafAgent(agentId, mafContent);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to provision MAF agent file for {Agent}.", displayName);
                }
            }
        }
    }

    // -----------------------------------------------------------------------
    // Routing helpers
    // -----------------------------------------------------------------------

    private static string BuildRoutingMd(Team team)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("# Routing\n\n");
        sb.Append("## Work Assignment\n\n");
        sb.Append("| Signal | Agent |\n");
        sb.Append("|--------|-------|\n");

        foreach (var member in team.Members.Where(m => m.Status == CastMemberStatus.Active))
        {
            var signal = member.Role.Title switch
            {
                var t when t.Contains("Frontend", StringComparison.OrdinalIgnoreCase) => "UI / frontend work",
                var t when t.Contains("Backend", StringComparison.OrdinalIgnoreCase) => "API / backend / server work",
                var t when t.Contains("Architect", StringComparison.OrdinalIgnoreCase) => "Architecture decisions, system design",
                var t when t.Contains("QA", StringComparison.OrdinalIgnoreCase) || t.Contains("Quality", StringComparison.OrdinalIgnoreCase) => "Testing, quality, bug fixes",
                var t when t.Contains("PM", StringComparison.OrdinalIgnoreCase) || t.Contains("Product", StringComparison.OrdinalIgnoreCase) => "Product decisions, scope, prioritization",
                var t when t.Contains("Docs", StringComparison.OrdinalIgnoreCase) || t.Contains("Writer", StringComparison.OrdinalIgnoreCase) => "Documentation, content, guides",
                var t when t.Contains("Research", StringComparison.OrdinalIgnoreCase) => "Research, investigation, analysis",
                var t when t.Contains("Designer", StringComparison.OrdinalIgnoreCase) => "Design, prototyping, UX",
                var t when t.Contains("Agent", StringComparison.OrdinalIgnoreCase) || t.Contains("Prompt", StringComparison.OrdinalIgnoreCase) => "AI agent design, prompt engineering",
                var t when t.Contains("Security", StringComparison.OrdinalIgnoreCase) || t.Contains("Safety", StringComparison.OrdinalIgnoreCase) => "Security, safety, compliance",
                var t when t.Contains("Incident", StringComparison.OrdinalIgnoreCase) => "Incidents, escalations, on-call",
                var t when t.Contains("Triage", StringComparison.OrdinalIgnoreCase) => "Issue triage, backlog prioritization",
                var t when t.Contains("Library", StringComparison.OrdinalIgnoreCase) || t.Contains("Maintainer", StringComparison.OrdinalIgnoreCase) => "Library direction, contribution review",
                _ => member.Role.Title + " work",
            };
            sb.Append($"| {signal} | {member.Name} |\n");
        }

        sb.Append("\n## Built-in Agents\n\n");
        sb.Append("| Signal | Agent |\n");
        sb.Append("|--------|-------|\n");
        sb.Append("| Session logging, decision recording, memory | Scribe |\n");
        sb.Append("| Work queue monitoring, backlog, keep-alive | Ralph |\n");
        sb.Append("| Safety review, content compliance | Rai |\n");

        return sb.ToString();
    }

    // -----------------------------------------------------------------------
    // Phase 2 — team read/edit
    // -----------------------------------------------------------------------

    public async Task<Team?> GetTeamAsync(string projectId, CancellationToken ct)
    {
        var (project, _) = await LoadProjectAsync(projectId, ct).ConfigureAwait(false);
        var reader = new SquadReader(project.WorkingDirectory);

        if (reader.DetectLayout().HasConflict)
            throw new SquadLayoutConflictException("Conflicting squad layouts detected.");

        return reader.ReadTeam();
    }

    public async Task<string?> GetCharterAsync(string projectId, string memberName, CancellationToken ct)
    {
        var (project, _) = await LoadProjectAsync(projectId, ct).ConfigureAwait(false);
        var reader = new SquadReader(project.WorkingDirectory);
        return reader.ReadCharter(memberName);
    }

    public async Task<string?> GetHistoryAsync(string projectId, string memberName, CancellationToken ct)
    {
        var (project, _) = await LoadProjectAsync(projectId, ct).ConfigureAwait(false);
        var reader = new SquadReader(project.WorkingDirectory);
        return reader.ReadHistory(memberName);
    }

    public async Task UpdateCharterAsync(
        string projectId, string memberName, string content, CancellationToken ct)
    {
        var (project, _) = await LoadProjectAsync(projectId, ct).ConfigureAwait(false);
        var writer = new SquadWriter(project.WorkingDirectory);
        writer.WriteCharter(memberName.ToLower(), content);
    }

    public async Task<CastMember> AddMemberAsync(
        string projectId, string roleId, string? customRoleTitle, string? modelId, CancellationToken ct)
    {
        var (project, owner) = await LoadProjectAsync(projectId, ct).ConfigureAwait(false);

        var reader = new SquadReader(project.WorkingDirectory);

        if (reader.DetectLayout().HasConflict)
            throw new SquadLayoutConflictException("Conflicting squad layouts detected.");

        var existingTeam = reader.ReadTeam();
        if (existingTeam is null)
            throw new InvalidOperationException("No team exists. Create a team first via casting.");

        var role = _catalog.LoadRole(roleId);
        if (role is null)
        {
            if (string.IsNullOrWhiteSpace(customRoleTitle))
                throw new ArgumentException($"Unknown role '{roleId}' and no custom_role_title provided.", nameof(roleId));

            role = new Role(
                Id: roleId,
                Title: customRoleTitle,
                Summary: string.Empty,
                DefaultModel: modelId ?? string.Empty,
                Capabilities: [],
                Responsibilities: [],
                Boundaries: []);
        }
        else if (!string.IsNullOrWhiteSpace(modelId))
        {
            role = role with { DefaultModel = modelId };
        }

        var policy = reader.ReadPolicy();
        var registry = reader.ReadRegistry();

        var reservedNames = new HashSet<string>(
            registry.Agents.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var m in existingTeam.Members)
            reservedNames.Add(m.Name);

        var allocator = new UniverseAllocator(policy);
        var (name, isNamed) = allocator.AllocateOne(existingTeam.Universe, reservedNames);

        var compiler = new CharterCompiler(_catalog);
        string charter;
        try
        {
            charter = compiler.Compile(role.Id, name);
        }
        catch (InvalidOperationException)
        {
            charter = compiler.CompileCustom(name, role.Title, role.Summary,
                role.Capabilities, role.Responsibilities, role.Boundaries);
        }

        var charterPath = CharterPath(name);
        var newMember = new CastMember(name, role, charterPath, CastMemberStatus.Active, isNamed);

        var updatedMembers = existingTeam.Members.Append(newMember).ToList();
        var updatedTeam = existingTeam with { Members = updatedMembers };

        var now = DateTimeOffset.UtcNow;
        var writer = new SquadWriter(project.WorkingDirectory);
        writer.WriteTeam(updatedTeam, owner, now);
        writer.WriteCharter(name.ToLower(), charter);

        var registryMember = new RegistryMember(
            Name: name,
            PersistentName: name.ToLower(),
            Universe: updatedTeam.Universe,
            DefaultModel: role.DefaultModel,
            Status: CastMemberStatus.Active,
            CreatedAt: now,
            PreviousName: null,
            SucceededBy: null,
            RetiredAt: null,
            CharterPath: charterPath);
        writer.AppendRegistryEvent(registryMember);
        writer.RegenerateCanonicalJson();

        return newMember;
    }

    public async Task RetireMemberAsync(string projectId, string memberName, CancellationToken ct)
    {
        var (project, owner) = await LoadProjectAsync(projectId, ct).ConfigureAwait(false);
        var reader = new SquadReader(project.WorkingDirectory);

        if (reader.DetectLayout().HasConflict)
            throw new SquadLayoutConflictException("Conflicting squad layouts detected.");

        var existingTeam = reader.ReadTeam();
        if (existingTeam is null)
            throw new InvalidOperationException("No team exists.");

        var target = existingTeam.Members.FirstOrDefault(
            m => string.Equals(m.Name, memberName, StringComparison.OrdinalIgnoreCase));
        if (target is null)
            throw new MemberNotFoundException(memberName);

        var retiredMember = target with { Status = CastMemberStatus.Retired };
        var updatedMembers = existingTeam.Members
            .Select(m => string.Equals(m.Name, memberName, StringComparison.OrdinalIgnoreCase) ? retiredMember : m)
            .ToList();
        var updatedTeam = existingTeam with { Members = updatedMembers };

        var now = DateTimeOffset.UtcNow;
        var writer = new SquadWriter(project.WorkingDirectory);
        writer.WriteTeam(updatedTeam, owner, now);
        writer.ArchiveMemberCharter(memberName);

        var registryEvent = new RegistryMember(
            Name: target.Name,
            PersistentName: target.Name.ToLower(),
            Universe: existingTeam.Universe,
            DefaultModel: target.Role.DefaultModel,
            Status: CastMemberStatus.Retired,
            CreatedAt: now,
            PreviousName: null,
            SucceededBy: null,
            RetiredAt: now,
            CharterPath: null);
        writer.AppendRegistryEvent(registryEvent);
        writer.RegenerateCanonicalJson();
    }

    public async Task<CastMember> ReroleMemberAsync(
        string projectId, string memberName, string newRoleId, string? customRoleTitle, CancellationToken ct)
    {
        var (project, owner) = await LoadProjectAsync(projectId, ct).ConfigureAwait(false);
        var reader = new SquadReader(project.WorkingDirectory);

        if (reader.DetectLayout().HasConflict)
            throw new SquadLayoutConflictException("Conflicting squad layouts detected.");

        var existingTeam = reader.ReadTeam();
        if (existingTeam is null)
            throw new InvalidOperationException("No team exists.");

        var target = existingTeam.Members.FirstOrDefault(
            m => string.Equals(m.Name, memberName, StringComparison.OrdinalIgnoreCase));
        if (target is null)
            throw new MemberNotFoundException(memberName);

        var role = _catalog.LoadRole(newRoleId);
        if (role is null)
        {
            if (string.IsNullOrWhiteSpace(customRoleTitle))
                throw new ArgumentException(
                    $"Unknown role '{newRoleId}' and no custom_role_title provided.", nameof(newRoleId));

            role = new Role(
                Id: newRoleId,
                Title: customRoleTitle,
                Summary: string.Empty,
                DefaultModel: string.Empty,
                Capabilities: [],
                Responsibilities: [],
                Boundaries: []);
        }

        var compiler = new CharterCompiler(_catalog);
        string charter;
        try
        {
            charter = compiler.Compile(role.Id, target.Name);
        }
        catch (InvalidOperationException)
        {
            charter = compiler.CompileCustom(target.Name, role.Title, role.Summary,
                role.Capabilities, role.Responsibilities, role.Boundaries);
        }

        var reroledMember = target with { Role = role };
        var updatedMembers = existingTeam.Members
            .Select(m => string.Equals(m.Name, memberName, StringComparison.OrdinalIgnoreCase) ? reroledMember : m)
            .ToList();
        var updatedTeam = existingTeam with { Members = updatedMembers };

        var now = DateTimeOffset.UtcNow;
        var writer = new SquadWriter(project.WorkingDirectory);
        writer.WriteTeam(updatedTeam, owner, now);
        writer.WriteCharter(target.Name.ToLower(), charter);

        var registryEvent = new RegistryMember(
            Name: target.Name,
            PersistentName: target.Name.ToLower(),
            Universe: existingTeam.Universe,
            DefaultModel: role.DefaultModel,
            Status: CastMemberStatus.Active,
            CreatedAt: now,
            PreviousName: null,
            SucceededBy: null,
            RetiredAt: null,
            CharterPath: reroledMember.CharterPath);
        writer.AppendRegistryEvent(registryEvent);
        writer.RegenerateCanonicalJson();

        return reroledMember;
    }

    private static string CharterPath(string memberName)
    {
        var slug = memberName.Trim().ToLowerInvariant().Replace(' ', '-');
        return $".squad/agents/{slug}/charter.md";
    }

    // -----------------------------------------------------------------------
    // Phase 5 — Git Sync
    // -----------------------------------------------------------------------

    private static SquadGitScribe CreateScribe(Project project)
        => new SquadGitScribe(project.WorkingDirectory);

    public async Task<SyncStatus> GetSyncStatusAsync(string projectId, CancellationToken ct)
    {
        var (project, _) = await LoadProjectAsync(projectId, ct).ConfigureAwait(false);
        return CreateScribe(project).GetStatus();
    }

    public async Task<string> CommitSyncAsync(
        string projectId,
        string expectedHash,
        string? message,
        CancellationToken ct)
    {
        var (project, _) = await LoadProjectAsync(projectId, ct).ConfigureAwait(false);
        var authorName = "Agentweaver";
        var authorEmail = "agentweaver@localhost";
        try
        {
            return CreateScribe(project).Commit(expectedHash, message, authorName, authorEmail);
        }
        catch (SyncStateChangedException ex)
        {
            throw new SyncStateChangedException(ex.Message);
        }
    }
}

// -----------------------------------------------------------------------
// Domain exceptions used by CastingService
// -----------------------------------------------------------------------

public sealed class ProjectNotFoundException(string projectId)
    : Exception($"Project '{projectId}' not found.");

public sealed class ProjectUnavailableException(string projectId)
    : Exception($"Project '{projectId}' is unavailable (being deleted).");

public sealed class SquadLayoutConflictException(string message)
    : Exception(message);

public sealed class ProposalNotFoundException(string proposalId)
    : Exception($"Proposal '{proposalId}' not found or has expired.");

public sealed class RequiresChoiceException(string message)
    : Exception(message);

public sealed class MemberNotFoundException(string name)
    : Exception($"Member '{name}' not found in the team.");
