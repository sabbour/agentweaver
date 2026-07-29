using System.Text;
using LibGit2Sharp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Agentweaver.Api.Git;
using Agentweaver.Domain;

namespace Agentweaver.Api.Squad;

/// <summary>
/// Project-level background service that externalises Squad's canonical decision ledger from the
/// per-run branch-merge path (issue #621).
///
/// <para><b>Why this exists.</b> In a Squad-bootstrapped project, every coordinator run gets its own
/// throwaway <c>agentweaver/{runId}</c> branch and, historically, each run's embedded "Scribe" pass
/// wrote directly to the SAME canonical bookkeeping files (<c>.squad/decisions.md</c>,
/// <c>.squad/agents/*/history.md</c>, <c>.squad/identity/now.md</c>) on its own branch. When many
/// runs raced on the same lines and their branches merged back via the plumbing-level
/// <see cref="LibGit2Sharp.ObjectDatabase.MergeCommits"/> (which does NOT honour <c>.gitattributes</c>
/// <c>merge=union</c> drivers), the result was a genuine, human-resolution-required 3-way conflict —
/// a terminal <c>MergeFailed</c> state that stuck the run.</para>
///
/// <para><b>What this does.</b> Squad's own documented "drop-box" convention already has agents record
/// decisions as brand-new, uniquely-named files under <c>.squad/decisions/inbox/{agent}-{slug}.md</c>
/// (never colliding across branches) and lets a single Scribe pass merge them into the canonical
/// <c>.squad/decisions.md</c>. This service makes that consolidation a project-level concern decoupled
/// from any individual run's worktree/branch/merge lifecycle: on each tick, for every active project,
/// it reads the inbox entries that have landed on the project's real default branch and idempotently
/// appends them into <c>.squad/decisions.md</c> via a single focused commit on that default branch —
/// NOT via <see cref="WorktreeManager"/>'s per-run merge machinery. It is the sole writer of the
/// canonical ledger; per-run merges are correspondingly taught to resolve those ledger paths
/// path-level "ours" (<see cref="WorktreeManager.IsSquadConsolidatedStatePath"/>), so a run's copy can
/// neither conflict nor clobber consolidated content.</para>
///
/// <para><b>Concurrency.</b> Each project's consolidation commit is guarded by the same
/// <see cref="RepositoryMergeLock"/> that serialises ordinary run-branch merges against that
/// repository, so this service never races a landing merge on the same default branch. If the lock is
/// busy this tick is a no-op for that project and simply retried next tick.</para>
///
/// <para><b>Idempotency.</b> Each consolidated entry carries a content-addressed marker
/// (<c>&lt;!-- squad-consolidated: {blobSha} --&gt;</c>); a repeated tick over the same entry never
/// re-appends it, and processed inbox files are deleted in the same commit, so a re-tick is a no-op —
/// the same fire-at-most-once discipline <c>WorkflowScheduleTriggerService</c> uses via its synthetic
/// source-path idempotency key.</para>
///
/// <para><b>Config.</b> Master enable flag <c>Squad:StateConsolidationEnabled</c> (default true) and
/// <c>Squad:StateConsolidationIntervalSeconds</c> (default 60), mirroring the
/// <c>Workflows:ScheduleTriggerEnabled</c>/<c>...IntervalSeconds</c> convention so hermetic tests can
/// disable it deterministically.</para>
/// </summary>
public sealed class SquadStateConsolidationService : BackgroundService
{
    private const string InboxDirPath = ".squad/decisions/inbox";
    private const string DecisionsPath = ".squad/decisions.md";
    private const string DefaultDecisionsHeader = "# Squad Decisions\n";
    private const string MarkerPrefix = "<!-- squad-consolidated: ";

    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RepositoryMergeLock _mergeLock;
    private readonly ILogger<SquadStateConsolidationService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Signature _signature;
    private readonly bool _enabled;
    private readonly TimeSpan _interval;

    public SquadStateConsolidationService(
        IServiceScopeFactory scopeFactory,
        RepositoryMergeLock mergeLock,
        IConfiguration configuration,
        ILogger<SquadStateConsolidationService> logger,
        TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory;
        _mergeLock = mergeLock;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;

        var authorName = configuration["Git:Author:Name"];
        var authorEmail = configuration["Git:Author:Email"];
        _signature = new Signature(
            string.IsNullOrWhiteSpace(authorName) ? "Agentweaver" : authorName,
            string.IsNullOrWhiteSpace(authorEmail) ? "agentweaver@localhost" : authorEmail,
            _timeProvider.GetUtcNow());

        // Master enable flag (default true), mirroring Workflows:ScheduleTriggerEnabled so hermetic
        // web tests can disable it to stay deterministic.
        _enabled = configuration.GetValue("Squad:StateConsolidationEnabled", true);

        var seconds = configuration.GetValue("Squad:StateConsolidationIntervalSeconds", 60);
        _interval = TimeSpan.FromSeconds(Math.Max(1, seconds));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation(
                "Squad state consolidation disabled (Squad:StateConsolidationEnabled=false)");
            return;
        }

        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await RunTickAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs one consolidation pass over every active project. The test seam that keeps the whole
    /// service unit-testable without any wall-clock dependency or sleeping.
    /// </summary>
    public async Task RunTickAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var projectStore = scope.ServiceProvider.GetRequiredService<IProjectStore>();

        IReadOnlyList<Project> projects = await projectStore.ListAsync(ct).ConfigureAwait(false);
        foreach (var project in projects)
        {
            ct.ThrowIfCancellationRequested();
            if (project.State != ProjectState.Active)
                continue;

            try
            {
                await ConsolidateProjectAsync(project, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;   // shutdown — stop the service cleanly
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Squad state consolidation: project {ProjectId} tick failed", project.Id);
                // Isolated; next project still processed.
            }
        }
    }

    /// <summary>
    /// Consolidates the decisions inbox on a single project's default branch. Returns the number of
    /// inbox entries newly appended to the canonical ledger (0 when nothing to do or the repository
    /// merge lock was busy), primarily for tests and diagnostics.
    /// </summary>
    public async Task<int> ConsolidateProjectAsync(Project project, CancellationToken ct)
    {
        var repoPath = project.WorkingDirectory;
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath) || !Repository.IsValid(repoPath))
            return 0;

        var lockHandle = await _mergeLock
            .TryAcquireAsync(Path.GetFullPath(repoPath), LockTimeout, ct)
            .ConfigureAwait(false);
        if (lockHandle is null)
        {
            // A run merge (or another consolidation) is in flight for this repository; retry next tick.
            _logger.LogDebug(
                "Squad state consolidation: repository busy for project {ProjectId}; skipping this tick",
                project.Id);
            return 0;
        }

        try
        {
            using var repo = new Repository(repoPath);
            var branch = repo.Branches[project.DefaultBranch];
            var tip = branch?.Tip;
            if (branch is null || tip is null)
                return 0;

            var inboxTreeEntry = tip.Tree[InboxDirPath];
            if (inboxTreeEntry is not { TargetType: TreeEntryTargetType.Tree, Target: Tree inbox })
                return 0;

            var inboxEntries = inbox
                .Where(e => e.TargetType == TreeEntryTargetType.Blob &&
                            e.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.Name, StringComparer.Ordinal)
                .ToList();
            if (inboxEntries.Count == 0)
                return 0;

            var existingDecisions = tip.Tree[DecisionsPath] is { Target: Blob decisionsBlob }
                ? ReadBlobText(decisionsBlob)
                : DefaultDecisionsHeader;

            var builder = new StringBuilder(existingDecisions);
            var processedPaths = new List<string>(inboxEntries.Count);
            var appended = 0;

            foreach (var entry in inboxEntries)
            {
                var relPath = $"{InboxDirPath}/{entry.Name}";
                processedPaths.Add(relPath);   // always cleared, even if already consolidated

                var blobSha = entry.Target.Sha;
                var marker = $"{MarkerPrefix}{blobSha} -->";
                if (existingDecisions.Contains(marker, StringComparison.Ordinal))
                    continue;   // idempotent: this exact content is already in the ledger

                var body = ReadBlobText((Blob)entry.Target).Trim();
                var stem = entry.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                    ? entry.Name[..^3]
                    : entry.Name;

                // Ensure exactly one blank line separates the previous content from this entry.
                if (builder.Length > 0 && builder[^1] != '\n') builder.Append('\n');
                builder.Append('\n');
                builder.Append("## ")
                       .Append(_timeProvider.GetUtcNow().ToString("yyyy-MM-ddTHH:mm:ssZ"))
                       .Append(" — Consolidated decision: ")
                       .Append(stem)
                       .Append("\n\n")
                       .Append(body)
                       .Append("\n\n")
                       .Append(marker)
                       .Append(" source: ")
                       .Append(relPath)
                       .Append("\n\n---\n");
                appended++;
            }

            var newDecisions = builder.ToString();
            var contentChanged = !string.Equals(newDecisions, existingDecisions, StringComparison.Ordinal);

            var definition = TreeDefinition.From(tip.Tree);
            if (contentChanged)
            {
                var newBlob = CreateBlob(repo, newDecisions);
                definition.Add(DecisionsPath, newBlob, Mode.NonExecutableFile);
            }
            foreach (var relPath in processedPaths)
                definition.Remove(relPath);

            var newTree = repo.ObjectDatabase.CreateTree(definition);
            if (string.Equals(newTree.Sha, tip.Tree.Sha, StringComparison.Ordinal))
                return 0;   // nothing actually changed

            var signature = WithTimestamp();
            var message = appended > 0
                ? $"chore(squad): consolidate {appended} decision inbox " +
                  $"{(appended == 1 ? "entry" : "entries")} into decisions.md (issue #621)"
                : "chore(squad): clear already-consolidated decision inbox entries (issue #621)";
            var commit = repo.ObjectDatabase.CreateCommit(
                signature, signature, message, newTree, new[] { tip }, prettifyMessage: true);

            repo.Refs.UpdateTarget(repo.Refs[branch.CanonicalName], commit.Id);

            SyncWorkingTreeIfCheckedOut(repo, project.DefaultBranch, contentChanged, newDecisions, processedPaths);

            _logger.LogInformation(
                "Squad state consolidation: project {ProjectId} appended {Appended} decision inbox " +
                "entries and cleared {Cleared} inbox files on {Branch} (commit {Commit})",
                project.Id, appended, processedPaths.Count, project.DefaultBranch, commit.Sha);

            return appended;
        }
        finally
        {
            lockHandle.Dispose();
        }
    }

    /// <summary>
    /// When the default branch is checked out in the main working tree, advancing the ref alone
    /// desynchronises the index/working tree from the new HEAD (the #348 hazard). Since this commit
    /// only ever touches the consolidation-owned ledger and the (now removed) inbox files, reconcile
    /// exactly those paths on disk so <c>git status</c> stays clean, without touching anything else.
    /// Best-effort: the commit is authoritative, so a working-tree sync failure is logged and left for
    /// the next tick (and a subsequent run merge neutralises the ledger path anyway).
    /// </summary>
    private void SyncWorkingTreeIfCheckedOut(
        Repository repo,
        string defaultBranch,
        bool decisionsChanged,
        string newDecisions,
        IReadOnlyList<string> processedPaths)
    {
        var headComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var checkedOut = !repo.Info.IsBare
            && !repo.Info.IsHeadDetached
            && string.Equals(repo.Head.FriendlyName, defaultBranch, headComparison);
        if (!checkedOut)
            return;

        var workdir = repo.Info.WorkingDirectory;
        try
        {
            foreach (var relPath in processedPaths)
            {
                var full = Path.Combine(workdir, relPath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(full)) File.Delete(full);
                Commands.Stage(repo, relPath);
            }

            if (decisionsChanged)
            {
                var full = Path.Combine(workdir, DecisionsPath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, newDecisions);
                Commands.Stage(repo, DecisionsPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Squad state consolidation: committed the ledger update but failed to reconcile the " +
                "checked-out working tree; it will self-heal on the next tick");
        }
    }

    private static Blob CreateBlob(Repository repo, string content)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        return repo.ObjectDatabase.CreateBlob(stream);
    }

    private static string ReadBlobText(Blob blob)
    {
        using var stream = blob.GetContentStream();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private Signature WithTimestamp() =>
        new(_signature.Name, _signature.Email, _timeProvider.GetUtcNow());
}
