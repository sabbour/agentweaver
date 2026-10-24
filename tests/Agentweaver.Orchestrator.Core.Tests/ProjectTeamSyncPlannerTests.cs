using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

/// <summary>Verifies that repository plans are deterministic projections of the Projects-owned cast.</summary>
public sealed class ProjectTeamSyncPlannerTests
{
    private const string CommitSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void PlanWritesCurrentCastAndPreservesUnrelatedAndAlumniFiles()
    {
        var projection = CreateProjection();
        var snapshot = new SourceControlTeamSyncSnapshot(
            "octo/team",
            "main",
            CommitSha,
            [
                File(".squad/team.md", "old team"),
                File(".squad/agents/lead/charter.md", "old charter"),
                File(".squad/agents/removed/charter.md", "stale charter"),
                File(".squad/agents/custom/history/charter.md", "nested charter"),
                File(".squad/agents/_alumni/old/charter.md", "alumni"),
                File(".squad/notes.md", "unrelated"),
            ]);

        var plan = ProjectTeamSyncPlanner.CreatePlan(projection, snapshot);

        Assert.Equal(
            [
                ".squad/agents/lead/charter.md",
                ".squad/agents/removed/charter.md",
                ".squad/agents/reviewer/charter.md",
                ".squad/team.md",
            ],
            plan.ChangeSummaries.Select(change => change.Path).ToArray());
        Assert.Equal(["modified", "removed", "added", "modified"],
            plan.ChangeSummaries.Select(change => change.Kind).ToArray());
        Assert.DoesNotContain(plan.Changes, change =>
            change.Path is ".squad/agents/custom/history/charter.md" or
                ".squad/agents/_alumni/old/charter.md" or ".squad/notes.md");
        Assert.Contains("Current project", System.Text.Encoding.UTF8.GetString(
            Assert.Single(plan.Changes, change => change.Path == ".squad/team.md").Content!));
        Assert.Equal(64, plan.ChangeSetHash.Length);

        // The same preview binds exact files, revision, repository ref, and branch head on every retry.
        var repeated = ProjectTeamSyncPlanner.CreatePlan(projection, snapshot);
        Assert.Equal(plan.ChangeSetHash, repeated.ChangeSetHash);
        Assert.NotEqual(plan.ChangeSetHash, ProjectTeamSyncPlanner.CreatePlan(
            projection with { ConfigurationRevision = projection.ConfigurationRevision + 1 },
            snapshot).ChangeSetHash);
        Assert.NotEqual(plan.ChangeSetHash, ProjectTeamSyncPlanner.CreatePlan(
            projection,
            snapshot with { CommitSha = "cccccccccccccccccccccccccccccccccccccccc" }).ChangeSetHash);
    }

    [Fact]
    public void ExactProjectionReturnsNoChangesAndNoCommitHash()
    {
        var projection = CreateProjection();
        var firstPlan = ProjectTeamSyncPlanner.CreatePlan(
            projection,
            new SourceControlTeamSyncSnapshot("octo/team", "main", CommitSha, []));
        var matchingSnapshot = new SourceControlTeamSyncSnapshot(
            "octo/team",
            "main",
            CommitSha,
            firstPlan.Changes.Select((change, index) => new SourceControlTeamSyncFile(
                change.Path,
                "blob-" + index,
                change.Content!,
                false)).ToImmutableArray());

        var plan = ProjectTeamSyncPlanner.CreatePlan(projection, matchingSnapshot);

        Assert.True(plan.NothingToSync);
        Assert.Empty(plan.Changes);
        Assert.Empty(plan.ChangeSummaries);
        Assert.Empty(plan.ChangeSetHash);
    }

    private static ProjectTeamSyncProjection CreateProjection() => new(
        "project",
        "Current project",
        "default",
        7,
        new IssueSyncProjectProof(
            "project",
            "tenant",
            "owner",
            "connection",
            new SourceControlRepositoryIdentity("octo", "team")),
        [
            new ProjectTeamSyncMember("lead", "Lead", "Engineer", "Builds the project.", 0),
            new ProjectTeamSyncMember("reviewer", "Reviewer", "Reviewer", "Reviews changes.", 1),
        ]);

    private static SourceControlTeamSyncFile File(string path, string content) =>
        new(path, "blob-" + path, System.Text.Encoding.UTF8.GetBytes(content), false);
}
