using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator;
using Agentweaver.Orchestrator.Core;
using Agentweaver.SourceControl;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed partial class MafCheckpointStorePostgresTests
{
    [Fact]
    public async Task BinarySeedCommitsOnceAndRecoversFromItsOriginalCheckpointAfterRestart()
    {
        var (initial, state, intent) = await SeedCheckpointAsync();
        var saved = await _executionStore.AppendAsync(
            intent.CheckpointId, initial.Info, state, CancellationToken.None);
        var later = state with { Revision = 3 };
        await _executionStore.AppendAsync("seed-later", saved.Info, later, CancellationToken.None);
        var restarted = new MafExecutionCheckpointStore(
            new PostgresMafCheckpointStore(_fixture.DataSource, _schema, null), _binding);

        var latest = await restarted.ReadLatestAsync(CancellationToken.None);
        var restored = latest!.State.BuildTestIntents[intent.StepId];
        Assert.Null(restored.WorkspaceSeed);
        Assert.Equal(intent.WorkspaceSeedReference, restored.WorkspaceSeedReference);
        var hydrated = await restarted.HydrateBuildTestIntentAsync(restored, CancellationToken.None);
        Assert.Equal(intent.WorkspaceSeed!.Value.ToArray(), hydrated.WorkspaceSeed!.Value.ToArray());
        Assert.Equal(intent.ToAcceptedCommand().ImmutableHash, hydrated.ToAcceptedCommand().ImmutableHash);
        Assert.Equal(1, await SeedRowCountAsync());
        await CoordinationOwnerMigrator.VerifyAsync(_fixture.DataSource, _schema);
    }

    [Fact]
    public async Task BinarySeedRollsBackWithItsOriginAndIntent()
    {
        var (initial, state, intent) = await SeedCheckpointAsync();
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await _executionStore.AppendInTransactionAsync(
            connection, transaction, intent.CheckpointId, initial.Info, state, CancellationToken.None);
        await transaction.RollbackAsync();

        Assert.Equal(0, await SeedRowCountAsync());
        Assert.Empty((await _executionStore.ReadLatestAsync(CancellationToken.None))!.State.BuildTestIntents);
        var exception = await Assert.ThrowsAsync<CoordinationException>(() =>
            _executionStore.HydrateBuildTestIntentAsync(intent, CancellationToken.None));
        Assert.Equal("checkpoint_workspace_seed_missing", exception.Code);
    }

    [Fact]
    public async Task BinarySeedReplayMatchesBothMetadataAndActualBytesWithoutOverwrite()
    {
        var (initial, state, intent) = await SeedCheckpointAsync();
        var saved = await _executionStore.AppendAsync(
            intent.CheckpointId, initial.Info, state, CancellationToken.None);
        Assert.Equal(saved.Info, (await _executionStore.AppendAsync(
            intent.CheckpointId, initial.Info, state, CancellationToken.None)).Info);
        Assert.Equal(1, await SeedRowCountAsync());
    }

    [Fact]
    public async Task BinarySeedReplayRejectsCorruptedBytesFromAnInitialCheckpointFixture()
    {
        var (initial, state, intent) = await SeedCheckpointAsync();
        await InsertSeedFixtureAsync(initial, state, intent, new byte[intent.WorkspaceSeed!.Value.Length]);

        var conflict = await Assert.ThrowsAsync<CoordinationException>(() =>
            _executionStore.AppendAsync(intent.CheckpointId, initial.Info, state, CancellationToken.None));
        Assert.Equal("checkpoint_idempotency_conflict", conflict.Code);
        var altered = await Assert.ThrowsAsync<CoordinationException>(() =>
            _executionStore.HydrateBuildTestIntentAsync(intent, CancellationToken.None));
        Assert.Equal("checkpoint_workspace_seed_integrity_invalid", altered.Code);
    }

    [Fact]
    public async Task BinarySeedOriginCannotBePrunedWhileItsLaterCheckpointExists()
    {
        var (initial, state, intent) = await SeedCheckpointAsync();
        var saved = await _executionStore.AppendAsync(
            intent.CheckpointId, initial.Info, state, CancellationToken.None);
        await _executionStore.AppendAsync(
            "seed-later", saved.Info, state with { Revision = 3 }, CancellationToken.None);
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var delete = new NpgsqlCommand(
            $"DELETE FROM \"{_schema}\".maf_workflow_checkpoints WHERE checkpoint_id = @checkpoint",
            connection);
        delete.Parameters.AddWithValue("checkpoint", intent.CheckpointId);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
        Assert.Equal(intent.WorkspaceSeed!.Value.ToArray(),
            (await _executionStore.HydrateBuildTestIntentAsync(intent, CancellationToken.None))
                .WorkspaceSeed!.Value.ToArray());
    }

    [Fact]
    public async Task BinarySeedDatabaseLimitMatchesTheExactAdmittedMaximum()
    {
        var (initial, state, intent) = await SeedCheckpointAsync();
        await InsertSeedFixtureAsync(initial, state, intent,
            new byte[ProducedRunCaptureLimits.MaximumPackageBytes]);

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertSeedFixtureAsync(initial, state, intent,
                new byte[ProducedRunCaptureLimits.MaximumPackageBytes + 1], "seed-too-large"));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("ck_maf_workspace_seed_bytes", exception.ConstraintName);
        var invalid = await Assert.ThrowsAsync<CoordinationException>(() =>
            _executionStore.HydrateBuildTestIntentAsync(intent, CancellationToken.None));
        Assert.Equal("checkpoint_workspace_seed_integrity_invalid", invalid.Code);
    }

    [Fact]
    public async Task BinarySeedMissingOriginCannotExecuteFromItsValidJsonReference()
    {
        var (initial, state, intent) = await SeedCheckpointAsync();
        await InsertSeedFixtureAsync(initial, state, intent, bytes: null);

        var exception = await Assert.ThrowsAsync<CoordinationException>(() =>
            _executionStore.HydrateBuildTestIntentAsync(intent, CancellationToken.None));

        Assert.Equal("checkpoint_workspace_seed_missing", exception.Code);
    }

    [Theory]
    [InlineData("actor", "checkpoint_owner_actor_mismatch")]
    [InlineData("fence", "checkpoint_execution_fence_stale")]
    public async Task BinarySeedResolutionRequiresTheCurrentOwnerAndFence(string change, string code)
    {
        var (initial, state, intent) = await SeedCheckpointAsync();
        await _executionStore.AppendAsync(
            intent.CheckpointId, initial.Info, state, CancellationToken.None);
        if (change == "fence")
        {
            await using var connection = await _fixture.DataSource.OpenConnectionAsync();
            await using var advance = new NpgsqlCommand(
                $"UPDATE \"{_schema}\".accepted_runs SET execution_fence = execution_fence + 1", connection);
            await advance.ExecuteNonQueryAsync();
        }
        var binding = change == "actor"
            ? _binding with { Actor = new(_actor.Issuer, Guid.NewGuid().ToString("D")) }
            : _binding;
        var reader = new MafExecutionCheckpointStore(
            new PostgresMafCheckpointStore(_fixture.DataSource, _schema, null), binding);

        var exception = await Assert.ThrowsAsync<CoordinationException>(() =>
            reader.HydrateBuildTestIntentAsync(intent, CancellationToken.None));

        Assert.Equal(code, exception.Code);
    }

    private async Task<(MafExecutionCheckpointSnapshot Initial, MafExecutionCheckpoint State,
        MafExecutionBuildTestIntent Intent)> SeedCheckpointAsync()
    {
        var initial = await _executionStore.AppendAsync(
            "seed-before", null, new(1, "plan-seed", 1, MafExecutionProgress.Empty), CancellationToken.None);
        var directory = Path.Combine(Path.GetTempPath(), "agentweaver-maf-seed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "git-empty-config"), "");
            await GitAsync(directory, "init", "--initial-branch=seed-branch");
            var readme = Encoding.UTF8.GetBytes("baseline repository input\n");
            await File.WriteAllBytesAsync(Path.Combine(directory, "README.md"), readme);
            await GitAsync(directory, "add", "--", "README.md");
            await GitAsync(directory, "-c", "user.name=Seed Test", "-c", "user.email=seed@example.invalid",
                "commit", "-m", "source baseline");
            var baseSha = (await GitAsync(directory, "rev-parse", "HEAD")).Trim();
            var treeSha = (await GitAsync(directory, "rev-parse", "HEAD^{tree}")).Trim();
            var bundlePath = Path.Combine(directory, "seed.bundle");
            await GitAsync(directory, "bundle", "create", bundlePath, "HEAD");
            var capture = new GitWorkspaceCapture(
                "workspace-seed", _binding.Identity.RunId, "repository-seed", 1, Guid.NewGuid(),
                baseSha, treeSha, "",
                [new("README.md", "100644", GitWorkspaceCapturePackage.Hash(readme),
                    readme.Length, readme.ToImmutableArray())],
                "seed-branch");
            var baseline = GitWorkspaceCapturePackage.Create(capture);
            var bundle = await File.ReadAllBytesAsync(bundlePath);
            var bytes = GitWorkspaceBuildTestSeedPackage.Create(baseline, bundle);
            var seedSource = new SandboxBuildTestWorkspaceSource(
                2, _binding.Identity, _selection.Authorization.TenantId, _actor.Issuer, _actor.Subject,
                "source-pin-seed", SourceControlOwnerStore.HashSelection(_selection.Selection),
                new(ProviderSeam.SourceControl, "github", "repository-seed", 1),
                capture.WorkspaceId, capture.WorkspaceIncarnationId, capture.BranchName,
                capture.BaseSha, capture.OutputTreeSha, "seed-origin",
                GitWorkspaceCapturePackage.Hash(bytes), bytes.Length,
                GitWorkspaceCapturePackage.Hash(bundle), bundle.Length,
                baseline.ManifestSha256, baseline.ManifestBytes.Length);
            var sample = MafBuildTestCheckpointTests.Intent();
            var intent = sample with
            {
                Identity = _binding.Identity,
                CheckpointId = "seed-origin",
                CheckpointRevision = 2,
                WorkPlanId = "plan-seed",
                DecisionStateVersion = 1,
                ExecutionFence = _binding.ExecutionFence,
                AcceptedSelectionHash = seedSource.AcceptedSelectionHash,
                Command = sample.Command with { WorkspaceSource = seedSource },
                ExecutionOptions = sample.ExecutionOptions with
                {
                    CollectorMode = SandboxBuildTestLimits.WorkspaceCollectorMode
                },
                ExpectedBinding = sample.ExpectedBinding with
                {
                    Fence = new(new(_selection.Authorization.TenantId, _binding.Identity.ProjectId,
                        _binding.Identity.RunId, "environment-seed"), 1)
                },
                WorkspaceSeed = bytes.ToImmutableArray()
            };
            intent = intent with
            {
                WorkspaceSeedReference = new(
                    intent.CheckpointReference(), intent.OperationId, seedSource.SeedSha256, seedSource.SeedByteLength)
            };
            var state = initial.State with
            {
                Revision = 2,
                BuildTestIntents = initial.State.BuildTestIntents.Add(intent.StepId, intent),
                Progress = initial.State.Progress with
                {
                    NonModelSteps = initial.State.Progress.NonModelSteps.Add(intent.StepId, MafExecutionTaskStatus.Running)
                }
            };
            return (initial, state, intent);
        }
        finally
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(
                         directory, "*", SearchOption.AllDirectories))
                File.SetAttributes(entry, FileAttributes.Normal);
            Directory.Delete(directory, recursive: true);
        }
    }

    private async Task<int> SeedRowCountAsync()
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var count = new NpgsqlCommand(
            $"SELECT count(*)::integer FROM \"{_schema}\".maf_workflow_checkpoints WHERE workspace_seed IS NOT NULL",
            connection);
        return (int)(await count.ExecuteScalarAsync())!;
    }

    private async Task InsertSeedFixtureAsync(
        MafExecutionCheckpointSnapshot initial,
        MafExecutionCheckpoint state,
        MafExecutionBuildTestIntent intent,
        byte[]? bytes,
        string? checkpointId = null)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var insert = new NpgsqlCommand($"""
            INSERT INTO "{_schema}".maf_workflow_checkpoints
                (project_id, run_id, session_id, store_name, checkpoint_id, parent_checkpoint_id,
                 payload, cache_object_key, cache_sdk_version, pinned_model_reference,
                 accepted_selection_hash, execution_fence, created_at, workspace_seed)
            SELECT project_id, run_id, session_id, store_name, @checkpoint, checkpoint_id,
                   @payload, cache_object_key, cache_sdk_version, pinned_model_reference,
                   accepted_selection_hash, execution_fence, clock_timestamp(), @bytes
            FROM "{_schema}".maf_workflow_checkpoints
            WHERE project_id = @project AND run_id = @run AND session_id = @session
                AND checkpoint_id = @parent AND store_name = @store
            """, connection);
        insert.Parameters.AddWithValue("project", _binding.Identity.ProjectId);
        insert.Parameters.AddWithValue("run", _binding.Identity.RunId);
        insert.Parameters.AddWithValue("session", _binding.Identity.SessionId);
        insert.Parameters.AddWithValue("store", MafExecutionCheckpointContract.StoreName);
        insert.Parameters.AddWithValue("parent", initial.Info.CheckpointId);
        insert.Parameters.AddWithValue("checkpoint", checkpointId ?? intent.CheckpointId);
        insert.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, MafExecutionCheckpointContract.Serialize(state));
        insert.Parameters.AddWithValue("bytes", NpgsqlDbType.Bytea, (object?)bytes ?? DBNull.Value);
        Assert.Equal(1, await insert.ExecuteNonQueryAsync());
    }

    private static async Task<string> GitAsync(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(directory, "git-empty-config");
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await error);
        return await output;
    }
}
