using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator;
using Agentweaver.Orchestrator.Core;
using Agentweaver.Providers;
using Agentweaver.SourceControl;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class MafBuildTestSourceInputTests
{
    [Fact]
    public void VerifiedSourceOwnerInputDoesNotEnrichTheConfirmedCommand()
    {
        var fixture = Fixture();
        var source = Produce(fixture);
        var intent = fixture.Intent.AttachWorkspaceInput(source, fixture.Seed.Bytes.ToImmutableArray());

        Assert.Same(fixture.Intent.Command, intent.Command);
        Assert.Null(intent.Command.WorkspaceSource);
        Assert.Equal(source, intent.WorkspaceInput);
        Assert.Equal(fixture.Run.Pin.PinId, source.SourceControlPinId);
        Assert.Equal(fixture.Run.Pin.AcceptedRun.TenantId, source.TenantId);
        Assert.Equal(fixture.Run.Pin.ProviderBinding.Resource, source.SourceControlResource);
        var accepted = intent.ToAcceptedCommand();
        Assert.Equal(2, accepted.ContractVersion);
        Assert.Equal(source, accepted.WorkspaceSource);
        Assert.Equal(accepted.ImmutableHash, accepted.ComputeImmutableHash());
        Assert.Equal(intent.CheckpointReference(), intent.WorkspaceSeedReference!.Origin);
        Assert.Equal(intent.OperationId, intent.WorkspaceSeedReference.OperationId);
    }

    [Fact]
    public void CheckpointRecoveryReplaysTheSameInputWithoutReplacingItsSeed()
    {
        var fixture = Fixture();
        var source = Produce(fixture);
        var intent = fixture.Intent.AttachWorkspaceInput(source, fixture.Seed.Bytes.ToImmutableArray());
        var initial = new MafExecutionCheckpointSnapshot(new("root", "initial"),
            new(1, intent.WorkPlanId, intent.DecisionStateVersion, MafExecutionProgress.Empty));
        var running = CoordinationEndpoints.PrepareBuildTestCheckpoint(initial, intent)!;
        var payload = MafExecutionCheckpointContract.Serialize(running);
        var recovered = MafExecutionCheckpointContract.Deserialize(payload);
        var restored = recovered.BuildTestIntents[intent.StepId];

        Assert.Contains("\"workspaceInput\":", payload.GetRawText());
        Assert.DoesNotContain("\"workspaceSeed\":", payload.GetRawText());
        Assert.Null(restored.Command.WorkspaceSource);
        Assert.Null(restored.WorkspaceSeed);
        Assert.Equal(intent.WorkspaceSeedReference, restored.WorkspaceSeedReference);
        Assert.Equal(intent.ToAcceptedCommand().ImmutableHash, restored.ToAcceptedCommand(false).ImmutableHash);
        SourceControlEndpoints.RequireSameBuildTestWorkspace(restored.EffectiveWorkspaceSource, fixture.Locator);
        Assert.Null(CoordinationEndpoints.PrepareBuildTestCheckpoint(
            new(new("root", intent.CheckpointId), recovered), intent));
        Assert.Equal(fixture.Seed.Bytes,
            (restored with { WorkspaceSeed = intent.WorkspaceSeed }).ToAcceptedCommand().WorkspaceSeed!.Value.ToArray());
        Assert.Throws<CoordinationException>(() =>
            restored.AttachWorkspaceInput(source, fixture.Seed.Bytes.ToImmutableArray()));
    }

    [Theory]
    [InlineData("workspace")]
    [InlineData("incarnation")]
    [InlineData("branch")]
    [InlineData("base")]
    public void ConflictingLocatorCannotReplaceTheAcceptedOperation(string change)
    {
        var fixture = Fixture();
        var source = Produce(fixture);
        var locator = change switch
        {
            "workspace" => fixture.Locator with { WorkspaceId = "another-workspace" },
            "incarnation" => fixture.Locator with { WorkspaceIncarnationId = Guid.NewGuid() },
            "branch" => fixture.Locator with { BranchName = "another-branch" },
            "base" => fixture.Locator with { BaseSha = new string('b', 40) },
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };

        var failure = Assert.Throws<CoordinationException>(() =>
            SourceControlEndpoints.RequireSameBuildTestWorkspace(source, locator));

        Assert.Equal("maf_execution_build_test_intent_conflict", failure.Code);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("tenant")]
    [InlineData("project")]
    [InlineData("run")]
    [InlineData("root")]
    [InlineData("selection")]
    [InlineData("pin")]
    [InlineData("resource")]
    [InlineData("fence")]
    public void SourceInputIsRejectedWhenTheCurrentOwnerBindingChanges(string change)
    {
        var fixture = Fixture();
        var source = Produce(fixture);
        var actor = fixture.Run.Actor;
        var identity = fixture.Run.Identity;
        var tenant = fixture.Run.Selection.Authorization.TenantId;
        var selection = fixture.Intent.AcceptedSelectionHash;
        var fence = fixture.Intent.ExecutionFence;
        switch (change)
        {
            case "actor": actor = actor with { Subject = "other-actor" }; break;
            case "tenant": tenant = "other-tenant"; break;
            case "project": identity = new("other-project", identity.RunId, identity.SessionId); break;
            case "run": identity = new(identity.ProjectId, "other-run", identity.SessionId); break;
            case "root": identity = new(identity.ProjectId, identity.RunId, "other-root"); break;
            case "selection": selection = new string('b', 64); break;
            case "pin": source = source with { SourceControlPinId = "other-pin" }; break;
            case "resource": source = source with
            {
                SourceControlResource = source.SourceControlResource with { Generation = 2 }
            }; break;
            case "fence": fence++; break;
        }

        var failure = Assert.Throws<CoordinationException>(() =>
            SourceControlEndpoints.ValidateBuildTestSourceOwner(
                source, actor, identity, tenant, selection, fixture.Run.Pin, fence));

        Assert.Equal("source_control_run_binding_changed", failure.Code);
    }

    [Fact]
    public void ASeedWithAnotherWorkspaceIncarnationCannotCreateTrustedInput()
    {
        var fixture = Fixture();
        var locator = fixture.Locator with { WorkspaceIncarnationId = Guid.NewGuid() };

        Assert.Throws<CoordinationException>(() =>
            SourceControlEndpoints.CreateBuildTestWorkspaceSource(fixture.Run, locator, fixture.Seed, "seed"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("changed")]
    public void MissingOrChangedSeedCannotBeAttachedOrExecuted(string change)
    {
        var fixture = Fixture();
        var source = Produce(fixture);
        var bytes = fixture.Seed.Bytes.ToArray();
        if (change == "missing")
            bytes = [];
        else
            bytes[^1] ^= 1;

        Assert.Throws<ArgumentException>(() =>
            fixture.Intent.AttachWorkspaceInput(source, bytes.ToImmutableArray()));
        var accepted = fixture.Intent.AttachWorkspaceInput(source, fixture.Seed.Bytes.ToImmutableArray());
        Assert.Throws<ArgumentException>(() => (accepted with { WorkspaceSeed = null }).ToAcceptedCommand());
    }

    [Fact]
    public void AConfirmedExplicitSourceMustRemainExact()
    {
        var fixture = Fixture();
        var source = Produce(fixture);
        var confirmed = fixture.Intent with { Command = fixture.Intent.Command with { WorkspaceSource = source } };
        var accepted = confirmed.AttachWorkspaceInput(source, fixture.Seed.Bytes.ToImmutableArray());

        Assert.Same(confirmed.Command, accepted.Command);
        Assert.Null(accepted.WorkspaceInput);
        Assert.Equal(source, accepted.ToAcceptedCommand().WorkspaceSource);
        var failure = Assert.Throws<CoordinationException>(() => confirmed.AttachWorkspaceInput(
            source with { SeedReference = "another-seed" }, fixture.Seed.Bytes.ToImmutableArray()));
        Assert.Equal("maf_execution_build_test_intent_conflict", failure.Code);
    }

    [Fact]
    public void SourceInputCannotSilentlyUpgradeTheAcceptedExecutionProfile()
    {
        var fixture = Fixture();
        var intent = fixture.Intent with { ExecutionOptions = MafBuildTestCheckpointTests.ExecutionOptions() };

        Assert.Throws<ArgumentException>(() =>
            intent.AttachWorkspaceInput(Produce(fixture), fixture.Seed.Bytes.ToImmutableArray()));
    }

    [Fact]
    public void ACheckpointCannotReplacePreviouslySavedSourceMetadata()
    {
        var fixture = Fixture();
        var intent = fixture.Intent.AttachWorkspaceInput(Produce(fixture), fixture.Seed.Bytes.ToImmutableArray());
        var initial = new MafExecutionCheckpointSnapshot(new("root", "initial"),
            new(1, intent.WorkPlanId, intent.DecisionStateVersion, MafExecutionProgress.Empty));
        var saved = CoordinationEndpoints.PrepareBuildTestCheckpoint(initial, intent)!;
        var changed = intent with
        {
            WorkspaceInput = intent.WorkspaceInput! with { SeedReference = "replacement" }
        };

        Assert.Throws<ArgumentException>(() => MafExecutionCheckpointContract.ValidateTransition(saved, saved with
        {
            Revision = saved.Revision + 1,
            BuildTestIntents = saved.BuildTestIntents.SetItem(intent.StepId, changed)
        }));
    }

    [Fact]
    public void LegacyIntentAndDispatchRequestDoNotGainInputFields()
    {
        var legacy = MafBuildTestCheckpointTests.Intent();
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var intentJson = JsonSerializer.Serialize(legacy, options);
        var requestJson = JsonSerializer.Serialize(new MafExecutionDispatchRequest(3, "plan-1"), options);

        Assert.DoesNotContain("workspaceInput", intentJson);
        Assert.DoesNotContain("workspaceSource", intentJson);
        Assert.DoesNotContain("workspaceSeed", intentJson);
        Assert.DoesNotContain("buildTestWorkspaces", requestJson);
        Assert.Null(legacy.EffectiveWorkspaceSource);
        Assert.Equal(1, legacy.ToAcceptedCommand().ContractVersion);
        Assert.Equal(legacy.ToAcceptedCommand().ComputeImmutableHash(), legacy.ToAcceptedCommand().ImmutableHash);
    }

    private static SandboxBuildTestWorkspaceSource Produce(InputFixture fixture) =>
        SourceControlEndpoints.CreateBuildTestWorkspaceSource(
            fixture.Run, fixture.Locator, fixture.Seed, "buildtest-" + fixture.Intent.OperationId.ToString("N"));

    private static InputFixture Fixture()
    {
        var intent = MafBuildTestCheckpointTests.Intent();
        var actor = new CoordinationActor("issuer", "actor");
        using var configuration = JsonDocument.Parse("""{"source":"projects-config"}""");
        var selection = new AuthorizedRunSelection(
            new(intent.Identity.ProjectId, intent.Identity.RunId, 1, 1, 1, "context", configuration.RootElement.Clone()),
            new(1, actor.Issuer, actor.Subject, "tenant-1", 1, intent.Identity.ProjectId, intent.Identity.RunId, []));
        var selectionHash = SourceControlOwnerStore.HashSelection(selection.Selection);
        intent = intent with
        {
            AcceptedSelectionHash = selectionHash,
            ExecutionOptions = intent.ExecutionOptions with { CollectorMode = SandboxBuildTestLimits.WorkspaceCollectorMode }
        };
        var capabilities = ImmutableHashSet.Create(StringComparer.Ordinal,
            SourceControlCapabilities.RepositoryRead, SourceControlCapabilities.RepositoryCheckout);
        var catalog = ProviderCatalog.Create(
            [new ProviderRegistration(new ProviderDescriptor(ProviderSeam.SourceControl, "github", new(1, 0, 0),
                1, ProviderHostingPattern.InProcess, capabilities), true, "source-v1", 1)],
            [new ProviderSelection(ProviderSeam.SourceControl, "github")], []).Value!;
        var resolver = new ProviderResolver(catalog);
        var candidate = resolver.Resolve(new ProviderResolutionRequest(
            ProviderSeam.SourceControl, null, new(1, 0, 0), 1,
            ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal))).Value!.Candidate!;
        var resource = new ProviderResourceRef(ProviderSeam.SourceControl, "github", "repository", 1);
        var binding = resolver.Pin(intent.Identity.RunId, candidate, resource.ResourceId,
            new ResourceNegotiation(resource, capabilities)).Value!;
        var acceptedRun = new SourceControlAcceptedRunBinding(
            actor.Issuer, actor.Subject, "tenant-1", intent.Identity.ProjectId, intent.Identity.RunId,
            intent.Identity.SessionId, selectionHash, selection.Selection.ProjectRevision,
            selection.Selection.ProjectConfigurationRevision, selection.Selection.PlatformRuntimeRevision,
            selection.Selection.ContextRevision, intent.ExecutionFence);
        var pin = new SourceControlRepositoryPin("source-pin", acceptedRun, binding, new("octo", "repo"),
            new(new SecretRef("api", "1"), SourceControlSecretPurposes.Api),
            new(new SecretRef("checkout", "1"), SourceControlSecretPurposes.Checkout),
            null, 123, "main", false, DateTimeOffset.UnixEpoch);
        var locator = new SourceControlBuildTestWorkspaceLocator("workspace",
            Guid.Parse("5956a121-5556-4c16-848c-0d4a2937966d"), new string('a', 40), "branch");
        var content = Encoding.UTF8.GetBytes("input");
        var file = new GitWorkspaceCapturedFile(
            "README.md", "100644", GitWorkspaceCapturePackage.Hash(content), content.Length, content.ToImmutableArray());
        var baseline = GitWorkspaceCapturePackage.Create(new("workspace", intent.Identity.RunId, "repository", 1,
            locator.WorkspaceIncarnationId, locator.BaseSha, new string('c', 40), string.Empty, [file], locator.BranchName));
        var bundle = Encoding.ASCII.GetBytes("# v2 git bundle\naccepted-test-base\n");
        var bytes = GitWorkspaceBuildTestSeedPackage.Create(baseline, bundle);
        var seed = new GitWorkspaceBuildTestSeedDocument(baseline, bundle, bytes, GitWorkspaceCapturePackage.Hash(bytes));
        return new(intent, new(actor, intent.Identity, selection, pin, intent.DecisionStateVersion), locator, seed);
    }

    private sealed record InputFixture(
        MafExecutionBuildTestIntent Intent,
        SourceControlRunContext Run,
        SourceControlBuildTestWorkspaceLocator Locator,
        GitWorkspaceBuildTestSeedDocument Seed);
}
