extern alias agenthost;

using System.Diagnostics;
using FluentAssertions;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Skills;
using Agentweaver.Domain;
using Agentweaver.Domain.Skills;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

using AgentHostOptions = agenthost::Agentweaver.AgentHost.AgentHostOptions;
using PodLocalWorkspaceManager = agenthost::Agentweaver.AgentHost.PodLocalWorkspaceManager;
using PodLocalWorkspaceSpec = agenthost::Agentweaver.AgentHost.PodLocalWorkspaceSpec;

namespace Agentweaver.Tests.Skills;

/// <summary>
/// Verifies issue #336 end-to-end for the pod-per-run / LocalWritable execution topology used by
/// coordinator-dispatched implementation children (<see cref="Agentweaver.Api.Sandbox.IRunAgentHostContextResolver"/>
/// resolves <c>ExecutionWorkspaceMode.LocalWritable</c> for these runs — see
/// <c>apps\Agentweaver.Api\Sandbox\IRunAgentHostContextResolver.cs:93</c>).
///
/// <para>
/// <see cref="SkillPromptComposer"/> (the #336 fix, <c>SkillPromptComposer.cs:72-116</c>) only inlines
/// a skill's full instructions when no writable worktree is found AT COMPOSE TIME
/// (<c>Directory.Exists(worktreePath)</c> from the API process' point of view). But
/// <see cref="Agentweaver.Api.Runs.RunOrchestrator"/> always composes against <c>run.WorktreePath</c> —
/// the API-local git worktree created by <c>WorktreeManager</c> — which DOES exist on the API's own
/// disk even for LocalWritable/pod-per-run runs. So the composer takes the "materialize + pointer"
/// branch and never inlines.
/// </para>
///
/// <para>
/// The problem: for LocalWritable runs, the agent does NOT execute against that API-local worktree at
/// all. <c>PodLocalWorkspaceManager.PrepareAsync</c> (<c>apps\Agentweaver.AgentHost\PodLocalWorkspaceManager.cs:44-160</c>)
/// builds a completely independent workspace on the AgentHost pod via <c>git init</c> +
/// <c>fetch --depth=1</c> + <c>checkout --detach</c> of a specific commit SHA — it never copies the
/// API's working-tree files, and the composer's materialized <c>.agentweaver/skills/</c> directory is
/// deliberately added to the git exclude list (<c>SkillPromptComposer.TryEnsureGitExclude</c>) so it is
/// never committed and could not be fetched even if it were. This test proves the <c>SKILL.md</c>
/// pointer the composer emits for a LocalWritable run is DANGLING from the actual executing agent's
/// point of view: the file is not in the composed prompt (inlined) NOR reachable inside the pod-local
/// workspace the agent actually runs in — reproducing #336 for this specific topology.
/// </para>
/// </summary>
public sealed class SkillPodPerRunDeliveryTests : IDisposable
{
    private readonly string _dir;
    private readonly string _sourceRepoPath;
    private readonly string _apiWorktreePath;
    private readonly string _scratchRoot;
    private readonly SqliteDb _db;
    private const string BranchName = "agentweaver/child-run-1";

    public SkillPodPerRunDeliveryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "aw-pod-skill-" + Guid.NewGuid().ToString("N"));
        _sourceRepoPath = Path.Combine(_dir, "source-repo");
        _apiWorktreePath = Path.Combine(_dir, "api-worktree");
        _scratchRoot = Path.Combine(_dir, "pod-scratch");
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(_sourceRepoPath);
        Directory.CreateDirectory(_scratchRoot);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"] = Path.Combine(_dir, "agentweaver.db"),
            })
            .Build();
        _db = new SqliteDb(config);
        _db.EnsureCreatedAsync().GetAwaiter().GetResult();

        // Real "shared" source repository (what run.RepositoryPath points at) with an authoritative
        // child branch, exactly like WorktreeManager/RunAgentHostContextResolver expect for an
        // implementation child (BranchNameFor(runId) == "agentweaver/<runId>").
        RunGit(_sourceRepoPath, "init", "-b", "main");
        RunGit(_sourceRepoPath, "config", "user.email", "test@example.com");
        RunGit(_sourceRepoPath, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(_sourceRepoPath, "README.md"), "hello\n");
        RunGit(_sourceRepoPath, "add", ".");
        RunGit(_sourceRepoPath, "commit", "-m", "initial");
        RunGit(_sourceRepoPath, "branch", BranchName);

        // The API-local worktree: what RunOrchestrator/SkillPromptComposer materialize skills into —
        // this exists ONLY on the API pod's disk, mirroring WorktreeManager.AddWorktree.
        RunGit(_sourceRepoPath, "worktree", "add", _apiWorktreePath, BranchName);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { RunGit(_sourceRepoPath, "worktree", "remove", "--force", _apiWorktreePath); } catch { /* best effort */ }
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static void RunGit(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stdout}\n{stderr}");
    }

    private static string RunGitCapture(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed");
        return stdout.Trim();
    }

    [Fact]
    public async Task ImplementationTurn_PodLocalWorkspace_SkillContentMustReachExecutingAgent()
    {
        var store = new SqliteSkillStore(_db);
        var project = ProjectId.New();
        const string token = "SKILL-ACTIVE-PODLOCAL-VERIFY";
        var skill = new Skill
        {
            Id = SkillId.New(),
            ProjectId = project,
            Name = "pod-local-verify-skill",
            Description = "Verifies delivery to pod-per-run implementation children.",
            Instructions = $"IMPORTANT: begin every response with '{token}'.",
            Provenance = SkillProvenance.Manual,
            ContentHash = SkillParser.ComputeContentHash(
                "pod-local-verify-skill", "desc", $"IMPORTANT: begin every response with '{token}'.",
                Array.Empty<SkillResource>()),
            Status = SkillStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await store.InsertAsync(skill);
        await store.AssignAsync(project, skill.Id, "Rogers", DateTimeOffset.UtcNow);

        // 1) API-side composition: exactly what RunOrchestrator.AppendAssignedSkillsAsync does for a
        //    coordinator-dispatched implementation child (run.WorktreePath == the API-local worktree).
        var composer = new SkillPromptComposer(store, NullLogger<SkillPromptComposer>.Instance);
        var block = await composer.ComposeAsync(
            project,
            "Rogers",
            _apiWorktreePath,
            CancellationToken.None,
            ExecutionWorkspaceMode.LocalWritable);
        block.Should().NotBeNullOrEmpty();

        // 2) Pod-side workspace preparation: exactly what the AgentHost pod does for an
        //    ImplementationTurn/LocalWritable run — an independent git fetch+checkout by commit SHA,
        //    with NO access to the API's worktree filesystem.
        var commitSha = RunGitCapture(_sourceRepoPath, "rev-parse", BranchName);
        var treeSha = RunGitCapture(_sourceRepoPath, "rev-parse", $"{BranchName}^{{tree}}");
        var options = Options.Create(new AgentHostOptions { ExecutionScratchRoot = _scratchRoot });
        var manager = new PodLocalWorkspaceManager(options, NullLogger<agenthost::Agentweaver.AgentHost.PodLocalWorkspaceManager>.Instance);
        var spec = new PodLocalWorkspaceSpec(
            RunId: "child-run-1",
            SourceRepositoryPath: _sourceRepoPath,
            SourceRef: BranchName,
            BaseCommitSha: commitSha,
            ExpectedTreeHash: treeSha,
            Mode: ExecutionWorkspaceMode.LocalWritable,
            ScratchRoot: _scratchRoot,
            CommitAuthorName: "Agentweaver",
            CommitAuthorEmail: "agentweaver@localhost");
        var prepared = await manager.PrepareAsync(spec, CancellationToken.None);

        // ACCEPTANCE CRITERION for #336 fully fixed in pod-per-run: the skill's content must actually
        // reach the executing agent — either inlined verbatim in the composed block, or present as a
        // real file inside the pod-local workspace the agent will read from.
        var podLocalSkillFile = Directory.Exists(prepared.WorkspacePath)
            ? Directory.EnumerateFiles(prepared.WorkspacePath, "SKILL.md", SearchOption.AllDirectories).FirstOrDefault()
            : null;

        var inlinedInPrompt = block!.Contains(token, StringComparison.Ordinal);
        var reachableOnPod = podLocalSkillFile is not null && File.ReadAllText(podLocalSkillFile).Contains(token, StringComparison.Ordinal);

        (inlinedInPrompt || reachableOnPod).Should().BeTrue(
            "the assigned skill's instructions must reach the agent that actually executes the turn; " +
            "a SKILL.md pointer materialized only into the API-local worktree is dangling for a " +
            "LocalWritable pod-per-run child whose workspace is an independent git checkout");
    }
}
