using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator;
using Agentweaver.Orchestrator.Core;
using Agentweaver.Providers;
using Agentweaver.SourceControl;
using Microsoft.Agents.AI.Workflows;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class MafExecutionOutputWitnessTests
{
    [Fact]
    public void CompleteOutputSetUsesCanonicalOrderAndPreservesAcceptedPathCasing()
    {
        var (plan, context, fixedStep) = CreateFixedPlan();
        var root = new SessionIdentity("project-1", "run-1", "root");
        var association = CreateAssociation(plan, context, fixedStep, root);
        var checkpoint = new MafExecutionCheckpointSnapshot(
            new CheckpointInfo(root.SessionId, "checkpoint-1"),
            new MafExecutionCheckpoint(1, plan.Plan.Id, 3, MafExecutionProgress.Empty)
            {
                FixedWorkAssociations = ImmutableDictionary<string, MafExecutionFixedWorkAssociation>.Empty
                    .WithComparers(StringComparer.Ordinal)
                    .Add(association.AssociationId, association)
            });

        var (outputSet, missing) =
            MafExecutionOutputWitness.CreateCompletePlanOutputSet(plan, checkpoint, root);

        Assert.Empty(missing);
        Assert.Equal(
            [
                new MafBacklogOutputObligation(association.AssociationId, "Docs/Plan.md"),
                new MafBacklogOutputObligation("implementation", "Src/Result.cs")
            ],
            outputSet.Obligations.ToArray());
        var canonical = $"{{\"contractVersion\":1,\"workPlanId\":\"plan-1\",\"obligations\":[" +
            $"{{\"workItemId\":\"{association.AssociationId}\",\"outputPath\":\"Docs/Plan.md\"}}," +
            "{\"workItemId\":\"implementation\",\"outputPath\":\"Src/Result.cs\"}]}";
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))),
            outputSet.Digest);
    }

    [Fact]
    public void MissingFixedAssociationIsReportedAndItsDeclaredOutputsRemainInTheSet()
    {
        var (plan, _, _) = CreateFixedPlan();
        var root = new SessionIdentity("project-1", "run-1", "root");

        var (outputSet, missing) =
            MafExecutionOutputWitness.CreateCompletePlanOutputSet(plan, checkpoint: null, root);

        var missingAssociationId = MafExecutionPlanner.CreateFixedWorkAssociationId(
            root, plan.Plan.Id, "setup", activationRevision: 1);
        Assert.Equal([missingAssociationId], missing.ToArray());
        Assert.Contains(
            new MafBacklogOutputObligation(missingAssociationId, "Docs/Plan.md"),
            outputSet.Obligations);
    }

    [Fact]
    public void CheckpointFromDifferentRootOrPlanIsRejected()
    {
        var (plan, _, _) = CreateFixedPlan();
        var root = new SessionIdentity("project-1", "run-1", "root");
        var checkpoint = new MafExecutionCheckpointSnapshot(
            new CheckpointInfo("another-root", "checkpoint-1"),
            new MafExecutionCheckpoint(1, plan.Plan.Id, 3, MafExecutionProgress.Empty));

        var failure = Assert.Throws<CoordinationException>(() =>
            MafExecutionOutputWitness.CreateCompletePlanOutputSet(plan, checkpoint, root));

        Assert.Equal("maf_execution_output_set_invalid", failure.Code);
    }

    [Fact]
    public void OwnerEvidenceSerializerEmitsNullableMergeIntentIdAsRequiredJsonProperty()
    {
        const string outputPath = "src/result.cs";
        var (_, _, root) = CreateCompleteOutputFixture();
        var capture = CreateCapture(root, ["src/result.cs"]);

        var evidence = MafExecutionOwnerEvidenceContract.Serialize(
            root,
            "tenant-1",
            [capture],
            ImmutableArray<SourceControlMergeIntentSnapshot>.Empty);

        var output = evidence[0].GetProperty("outputs")[0];
        Assert.Equal("capture", evidence[0].GetProperty("kind").GetString());
        Assert.True(output.TryGetProperty("mergeIntentId", out var mergeIntentId));
        Assert.Equal(JsonValueKind.Null, mergeIntentId.ValueKind);
        Assert.Equal("implementation", output.GetProperty("workItemId").GetString());
        Assert.Equal(outputPath, output.GetProperty("path").GetString());
    }

    [Fact]
    public void OwnerEvidenceSerializerEmitsProducingCaptureAndMergedIntent()
    {
        var (plan, checkpoint, root) = CreateCompleteOutputFixture(includeMerge: true);
        var (approvedRoot, mergeIntent) = CreateMergedIntent(root, plan);
        var capture = CreateCapture(approvedRoot, ["src/result.cs"]);

        var evidence = MafExecutionOwnerEvidenceContract.Serialize(
            approvedRoot,
            "tenant-1",
            [capture],
            [mergeIntent]);

        Assert.Equal("capture", evidence[0].GetProperty("kind").GetString());
        Assert.Equal(
            "merge-intent-1",
            evidence[0].GetProperty("outputs")[0].GetProperty("mergeIntentId").GetString());
        Assert.Equal("merge", evidence[1].GetProperty("kind").GetString());
        Assert.Equal("merge-intent-1", evidence[1].GetProperty("intentId").GetString());
        Assert.Equal(mergeIntent.MergeSha, evidence[1].GetProperty("mergeSha").GetString());
        Assert.Equal(checkpoint.State.Revision, approvedRoot.Checkpoint.State.Revision);
    }

    [Fact]
    public void OwnerEvidenceSerializerRejectsCaptureFromDifferentRoot()
    {
        var (_, _, root) = CreateCompleteOutputFixture();
        var capture = CreateCapture(
            root,
            ["src/result.cs"],
            captureIdentity: new SessionIdentity(root.Identity.ProjectId, root.Identity.RunId, "other-root"));

        var failure = Assert.Throws<CoordinationException>(() =>
            MafExecutionOwnerEvidenceContract.Serialize(root, "tenant-1", [capture], []));

        Assert.Equal("maf_execution_output_witness_evidence_invalid", failure.Code);
    }

    [Fact]
    public void OwnerEvidenceSerializerRejectsMissingManifestOutput()
    {
        var (_, _, root) = CreateCompleteOutputFixture();
        var capture = CreateCapture(root, []);

        var failure = Assert.Throws<CoordinationException>(() =>
            MafExecutionOwnerEvidenceContract.Serialize(root, "tenant-1", [capture], []));

        Assert.Equal("maf_execution_output_witness_evidence_invalid", failure.Code);
    }

    [Fact]
    public void OwnerEvidenceSerializerRejectsExtraManifestOutput()
    {
        var (_, _, root) = CreateCompleteOutputFixture();
        var capture = CreateCapture(root, ["src/result.cs", "src/unexpected.cs"]);

        var failure = Assert.Throws<CoordinationException>(() =>
            MafExecutionOwnerEvidenceContract.Serialize(root, "tenant-1", [capture], []));

        Assert.Equal("maf_execution_output_witness_evidence_invalid", failure.Code);
    }

    [Fact]
    public void OwnerEvidenceSerializerRejectsAmbiguousManifestOutput()
    {
        var (_, _, root) = CreateCompleteOutputFixture();
        var capture = CreateCapture(root, ["src/result.cs"], duplicateManifestOutput: true);

        var failure = Assert.Throws<CoordinationException>(() =>
            MafExecutionOwnerEvidenceContract.Serialize(root, "tenant-1", [capture], []));

        Assert.Equal("maf_execution_output_witness_evidence_invalid", failure.Code);
    }

    [Fact]
    public void OwnerEvidenceSerializerEmitsExplicitNoOutputRowForCompleteEmptyPlan()
    {
        var (_, checkpoint, root) = CreateEmptyOutputFixture();

        var evidence = MafExecutionOwnerEvidenceContract.Serialize(
            root,
            "tenant-1",
            ImmutableArray<SourceControlOutputCaptureRecord>.Empty,
            ImmutableArray<SourceControlMergeIntentSnapshot>.Empty);

        Assert.Single(evidence.EnumerateArray());
        Assert.Equal("no-output", evidence[0].GetProperty("kind").GetString());
        Assert.Equal(
            checkpoint.State.Revision,
            evidence[0].GetProperty("checkpointRevision").GetInt64());
    }

    [Fact]
    public void OwnerEvidenceSerializerRejectsNonemptyAdmittedCaptureForNoOutputPlan()
    {
        var (_, _, root) = CreateEmptyOutputFixture();
        var capture = CreateCapture(root, ["src/unexpected.cs"]);
        Assert.Equal("admitted", capture.State);
        Assert.NotEmpty(GitWorkspaceCapturePackage.ParseManifest(capture.ManifestBytes).Files);

        var failure = Assert.Throws<CoordinationException>(() =>
            MafExecutionOwnerEvidenceContract.Serialize(root, "tenant-1", [capture], []));

        Assert.Equal("maf_execution_output_witness_evidence_invalid", failure.Code);
    }

    [Fact]
    public void CurrentNoOutputEvidenceAcceptsEmptyCaptureInventory()
    {
        var (_, _, root) = CreateEmptyOutputFixture();
        var witnessEvidence = MafExecutionOwnerEvidenceContract.Serialize(root, "tenant-1", [], []);

        Assert.True(MafBacklogPrerequisiteEvidenceReader.TryRebuildCurrentOwnerEvidence(
            root, "tenant-1", null, [], [], out var currentEvidence));
        Assert.True(JsonElement.DeepEquals(witnessEvidence, currentEvidence));
    }

    [Fact]
    public void CurrentSingleCaptureEvidenceAcceptsMatchingCapture()
    {
        var (_, _, root) = CreateCompleteOutputFixture();
        var capture = CreateCapture(root, ["src/result.cs"]);
        var witnessEvidence = MafExecutionOwnerEvidenceContract.Serialize(
            root, "tenant-1", [capture], []);

        Assert.True(MafBacklogPrerequisiteEvidenceReader.TryRebuildCurrentOwnerEvidence(
            root,
            "tenant-1",
            capture.Proof.CaptureId,
            [capture],
            [],
            out var currentEvidence));
        Assert.True(JsonElement.DeepEquals(witnessEvidence, currentEvidence));
    }

    [Fact]
    public void LateAdmittedCaptureInvalidatesNoOutputWitness()
    {
        var (_, _, root) = CreateEmptyOutputFixture();
        var witnessEvidence = MafExecutionOwnerEvidenceContract.Serialize(root, "tenant-1", [], []);
        Assert.Equal("no-output", witnessEvidence[0].GetProperty("kind").GetString());
        var lateCapture = CreateCapture(root, ["src/unexpected.cs"]);

        var failure = Assert.Throws<CoordinationException>(() =>
            MafBacklogPrerequisiteEvidenceReader.TryRebuildCurrentOwnerEvidence(
                root, "tenant-1", null, [lateCapture], [], out _));

        Assert.Equal("maf_execution_output_witness_evidence_invalid", failure.Code);
    }

    [Fact]
    public void SecondAdmittedCaptureInvalidatesSingleCaptureWitness()
    {
        var (_, _, root) = CreateCompleteOutputFixture();
        var witnessCapture = CreateCapture(root, ["src/result.cs"]);
        var secondCapture = CreateCapture(root, ["src/result.cs"], pinId: "pin-2");
        Assert.Equal("admitted", secondCapture.State);
        Assert.NotEqual(witnessCapture.Proof.CaptureId, secondCapture.Proof.CaptureId);
        var witnessEvidence = MafExecutionOwnerEvidenceContract.Serialize(
            root, "tenant-1", [witnessCapture], []);
        Assert.Equal("capture", witnessEvidence[0].GetProperty("kind").GetString());

        Assert.False(MafBacklogPrerequisiteEvidenceReader.TryRebuildCurrentOwnerEvidence(
            root,
            "tenant-1",
            witnessCapture.Proof.CaptureId,
            [witnessCapture, secondCapture],
            [],
            out _));
    }

    [Fact]
    public void OwnerEvidenceSerializerRejectsNoOutputWhenRequiredPlatformStepExists()
    {
        var identity = new SessionIdentity("project-1", "run-1", "root");
        var review = WorkflowTestData.Platform("review", 1, []);
        var definition = WorkflowTestData.Definition(
            WorkflowDefinitionOrigin.BuiltIn,
            WorkflowTestData.Open("optional", 0, [], minimum: 0),
            review);
        var planResult = WorkPlanValidator.ValidateAndSnapshot(
            WorkflowTestData.Snapshot(definition),
            WorkflowTestData.Plan(),
            WorkflowTestData.SelectionContext());
        Assert.True(planResult.IsValid, string.Join("; ", planResult.Issues.Select(issue => issue.Message)));
        var plan = planResult.Value!;
        var progress = MafExecutionProgress.Empty with
        {
            NonModelSteps = MafExecutionProgress.Empty.NonModelSteps.Add(
                review.Id, MafExecutionTaskStatus.Succeeded)
        };
        var checkpoint = new MafExecutionCheckpointSnapshot(
            new CheckpointInfo(identity.SessionId, "checkpoint-1"),
            new MafExecutionCheckpoint(1, plan.Plan.Id, 3, progress));
        var outputSet = MafExecutionOutputWitness.CreateCompletePlanOutputSet(
            plan, checkpoint, identity).OutputSet;
        var root = CreateRoot(identity, plan, checkpoint, outputSet);

        var failure = Assert.Throws<CoordinationException>(() =>
            MafExecutionOwnerEvidenceContract.Serialize(
                root,
                "tenant-1",
                ImmutableArray<SourceControlOutputCaptureRecord>.Empty,
                ImmutableArray<SourceControlMergeIntentSnapshot>.Empty));

        Assert.Equal("maf_execution_output_witness_evidence_invalid", failure.Code);
    }

    private static (
        WorkPlanSnapshot Plan,
        MafExecutionCheckpointSnapshot Checkpoint,
        MafExecutionOwnerEvidenceBinding Root) CreateCompleteOutputFixture(bool includeMerge = false)
    {
        var identity = new SessionIdentity("project-1", "run-1", "root");
        var definition = includeMerge
            ? WorkflowTestData.Definition(
                WorkflowDefinitionOrigin.BuiltIn,
                WorkflowTestData.Open("implement", 0, [], minimum: 1),
                WorkflowTestData.Platform("merge", 1, ["implement"], WorkflowPlatformGate.Merge))
            : WorkflowTestData.Definition(
                WorkflowDefinitionOrigin.BuiltIn,
                WorkflowTestData.Open("implement", 0, [], minimum: 1));
        var planResult = WorkPlanValidator.ValidateAndSnapshot(
            WorkflowTestData.Snapshot(definition),
            WorkflowTestData.Plan(WorkflowTestData.Item(
                "implementation", outputs: ["src/result.cs"])),
            WorkflowTestData.SelectionContext());
        Assert.True(planResult.IsValid, string.Join("; ", planResult.Issues.Select(issue => issue.Message)));
        var plan = planResult.Value!;
        var progress = MafExecutionProgress.Empty with
        {
            WorkItems = MafExecutionProgress.Empty.WorkItems.Add(
                "implementation", MafExecutionTaskStatus.Succeeded),
            NonModelSteps = includeMerge
                ? MafExecutionProgress.Empty.NonModelSteps.Add("merge", MafExecutionTaskStatus.Succeeded)
                : MafExecutionProgress.Empty.NonModelSteps
        };
        var checkpoint = new MafExecutionCheckpointSnapshot(
            new CheckpointInfo(identity.SessionId, "checkpoint-1"),
            new MafExecutionCheckpoint(1, plan.Plan.Id, 3, progress)
            {
                Results = ImmutableDictionary<string, string>.Empty
                    .WithComparers(StringComparer.Ordinal)
                    .Add("implementation", "completed")
            });
        var outputSet = MafExecutionOutputWitness.CreateCompletePlanOutputSet(
            plan, checkpoint, identity).OutputSet;
        return (plan, checkpoint, CreateRoot(identity, plan, checkpoint, outputSet));
    }

    private static (
        WorkPlanSnapshot Plan,
        MafExecutionCheckpointSnapshot Checkpoint,
        MafExecutionOwnerEvidenceBinding Root) CreateEmptyOutputFixture()
    {
        var identity = new SessionIdentity("project-1", "run-1", "root");
        var definition = WorkflowTestData.Definition(
            WorkflowDefinitionOrigin.BuiltIn,
            WorkflowTestData.Open("optional", 0, [], minimum: 0));
        var planResult = WorkPlanValidator.ValidateAndSnapshot(
            WorkflowTestData.Snapshot(definition),
            WorkflowTestData.Plan(),
            WorkflowTestData.SelectionContext());
        Assert.True(planResult.IsValid, string.Join("; ", planResult.Issues.Select(issue => issue.Message)));
        var plan = planResult.Value!;
        var checkpoint = new MafExecutionCheckpointSnapshot(
            new CheckpointInfo(identity.SessionId, "checkpoint-1"),
            new MafExecutionCheckpoint(1, plan.Plan.Id, 3, MafExecutionProgress.Empty));
        var outputSet = MafExecutionOutputWitness.CreateCompletePlanOutputSet(
            plan, checkpoint, identity).OutputSet;
        return (plan, checkpoint, CreateRoot(identity, plan, checkpoint, outputSet));
    }

    private static SourceControlOutputCaptureRecord CreateCapture(
        MafExecutionOwnerEvidenceBinding root,
        string[] outputPaths,
        SessionIdentity? captureIdentity = null,
        bool duplicateManifestOutput = false,
        string pinId = "pin-1",
        string repositoryId = "repo-1",
        long resourceGeneration = 1)
    {
        const string workspaceId = "workspace-1";
        const string branchName = "main";
        var incarnationId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var baseSha = new string('a', 40);
        var outputTreeSha = new string('b', 40);
        var files = outputPaths.Select(path =>
        {
            var bytes = Encoding.UTF8.GetBytes($"content:{path}");
            return new GitWorkspaceCapturedFile(
                path,
                "100644",
                GitWorkspaceCapturePackage.Hash(bytes),
                bytes.LongLength,
                ImmutableArray.CreateRange(bytes));
        }).ToImmutableArray();
        var document = GitWorkspaceCapturePackage.Create(new GitWorkspaceCapture(
            workspaceId,
            root.Identity.RunId,
            repositoryId,
            resourceGeneration,
            incarnationId,
            baseSha,
            outputTreeSha,
            string.Empty,
            files,
            branchName));
        var manifestBytes = document.ManifestBytes;
        if (duplicateManifestOutput)
        {
            Assert.Single(document.Manifest.Files);
            using var manifest = JsonDocument.Parse(manifestBytes);
            var fileJson = manifest.RootElement.GetProperty("files")[0].GetRawText();
            var json = Encoding.UTF8.GetString(manifestBytes);
            var closing = json.LastIndexOf("]}", StringComparison.Ordinal);
            manifestBytes = Encoding.UTF8.GetBytes(
                string.Concat(json.AsSpan(0, closing), ",", fileJson, json.AsSpan(closing)));
        }

        var proofIdentity = captureIdentity ?? root.Identity;
        var capture = ProducedRunCaptureContractValidation.CreateIdentity(
            proofIdentity,
            pinId,
            root.AcceptedSelectionHash,
            workspaceId,
            incarnationId,
            branchName,
            repositoryId,
            resourceGeneration,
            baseSha,
            outputTreeSha,
            GitWorkspaceCapturePackage.Hash(manifestBytes));
        var proof = new ProducedRunCaptureProof(
            ProducedRunCaptureLimits.ContractVersion,
            proofIdentity,
            capture.CaptureId,
            capture.EventId,
            root.Actor.Issuer,
            root.Actor.Subject,
            "tenant-1",
            pinId,
            root.AcceptedSelectionHash,
            workspaceId,
            incarnationId,
            branchName,
            repositoryId,
            resourceGeneration,
            baseSha,
            outputTreeSha,
            GitWorkspaceCapturePackage.Hash(manifestBytes),
            manifestBytes.LongLength,
            document.PatchSha256,
            document.PatchBytes.LongLength,
            document.PackageSha256,
            document.PackageBytes.LongLength,
            DateTimeOffset.UnixEpoch);
        return new SourceControlOutputCaptureRecord(
            proof,
            "admitted",
            ProducedRunCaptureContractValidation.CreatePackageReference(proof).Key.Value,
            1,
            DateTimeOffset.UnixEpoch,
            manifestBytes,
            document.PatchBytes,
            document.PackageBytes);
    }

    private static (
        MafExecutionOwnerEvidenceBinding Root,
        SourceControlMergeIntentSnapshot Intent) CreateMergedIntent(
        MafExecutionOwnerEvidenceBinding root,
        WorkPlanSnapshot plan)
    {
        const string intentId = "merge-intent-1";
        var approval = CoordinatorDecisionFlow.RequestApproval(
            root.CurrentDecision.State,
            "approval-request-1",
            intentId,
            root.Actor.Subject);
        Assert.NotNull(approval.State);
        var approved = approval.State!.ApplyGateAnswer(new CoordinatorGateAnswer(
            approval.Value!.RequestId,
            root.Actor.Subject,
            root.ExecutionFence,
            CoordinatorGateChoices.Approve,
            null));
        Assert.True(approved.IsAccepted);
        var receipt = Assert.IsType<CoordinatorGateDecisionReceipt>(approved.Receipt);
        var approvedRoot = root with
        {
            CurrentDecision = root.CurrentDecision with { State = approved.State }
        };
        var acceptedRun = new SourceControlAcceptedRunBinding(
            root.Actor.Issuer,
            root.Actor.Subject,
            "tenant-1",
            root.Identity.ProjectId,
            root.Identity.RunId,
            root.Identity.SessionId,
            root.AcceptedSelectionHash,
            1,
            1,
            1,
            "context-1",
            root.ExecutionFence);
        var pin = CreateSourceControlPin(acceptedRun);
        var mergeStep = plan.Workflow.Definition.Steps.Single(step =>
            step.Mode == WorkflowStepMode.Platform && step.PlatformGate == WorkflowPlatformGate.Merge);
        var intent = new SourceControlMergeIntentSnapshot(
            intentId,
            acceptedRun,
            pin,
            1,
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            "source-request-1",
            "idempotency-1",
            plan.Workflow.Definition.Id,
            plan.Workflow.Definition.Revision,
            plan.Plan.Id,
            "assembly-request-1",
            mergeStep.Id,
            receipt.RequestId,
            12,
            "feature",
            new string('c', 40),
            "main",
            new string('d', 40),
            SourceControlMergeMethod.Rebase,
            "merged",
            Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            approvedRoot.CurrentDecision.StateVersion,
            receipt,
            new string('e', 40),
            null,
            DateTimeOffset.UnixEpoch,
            null);
        return (approvedRoot, intent);
    }

    private static SourceControlRepositoryPin CreateSourceControlPin(
        SourceControlAcceptedRunBinding acceptedRun)
    {
        var capabilities = ImmutableHashSet.Create(
            StringComparer.Ordinal,
            SourceControlCapabilities.RepositoryRead,
            SourceControlCapabilities.Merge);
        var catalogResult = ProviderCatalog.Create(
            [
                new ProviderRegistration(
                    new ProviderDescriptor(
                        ProviderSeam.SourceControl,
                        SourceControlProviderIds.GitHub,
                        new Version(1, 0, 0),
                        1,
                        ProviderHostingPattern.InProcess,
                        capabilities),
                    true,
                    "source-control-v1",
                    1)
            ],
            [new ProviderSelection(ProviderSeam.SourceControl, SourceControlProviderIds.GitHub)],
            []);
        Assert.True(catalogResult.IsSuccess);
        var resolver = new ProviderResolver(catalogResult.Value!);
        var resolution = resolver.Resolve(new ProviderResolutionRequest(
            ProviderSeam.SourceControl,
            null,
            new Version(1, 0, 0),
            1,
            ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal)));
        Assert.True(resolution.IsSuccess);
        var resource = new ProviderResourceRef(
            ProviderSeam.SourceControl, SourceControlProviderIds.GitHub, "repo-1", 1);
        var binding = resolver.Pin(
            acceptedRun.RunId,
            resolution.Value!.Candidate!,
            resource.ResourceId,
            new ResourceNegotiation(resource, capabilities));
        Assert.True(binding.IsSuccess);
        return new SourceControlRepositoryPin(
            "pin-1",
            acceptedRun,
            binding.Value!,
            new SourceControlRepositoryIdentity("octo", "repo"),
            new SourceControlCredentialReference(
                new SecretRef("github-api-v1", "version-1"),
                SourceControlSecretPurposes.Api),
            checkoutCredential: null,
            webhookCredential: null,
            123,
            "main",
            isPrivate: false,
            DateTimeOffset.UnixEpoch);
    }

    private static MafExecutionOwnerEvidenceBinding CreateRoot(
        SessionIdentity identity,
        WorkPlanSnapshot plan,
        MafExecutionCheckpointSnapshot checkpoint,
        MafBacklogOutputSetSnapshot outputSet)
    {
        const long fence = 2;
        const long decisionVersion = 3;
        const string selectionHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var state = CoordinatorDecisionState.Create(fence);
        return new MafExecutionOwnerEvidenceBinding(
            new CoordinationActor("https://issuer.example/", "actor-1"),
            identity,
            selectionHash,
            fence,
            new CoordinatorDecisionCurrentState(state, decisionVersion, selectionHash),
            checkpoint,
            plan,
            outputSet);
    }

    private static (WorkPlanSnapshot Plan, WorkPlanRunSelectionContext Context,
        WorkflowStepDefinition FixedStep) CreateFixedPlan()
    {
        var definition = WorkflowTestData.Definition(
            WorkflowDefinitionOrigin.BuiltIn,
            WorkflowTestData.Fixed("setup", 0, [], ["Docs/Plan.md"]),
            WorkflowTestData.Open("implement", 1, ["setup"], minimum: 0));
        var context = new WorkPlanRunSelectionContext(
            [
                new RoleRunSelection("planner", ["planner-agent"], ["model:provider/alpha"], ["worktree"]),
                new RoleRunSelection("engineer", ["agent-1"], ["model:provider/alpha"], ["worktree"])
            ],
            WorkflowTestData.PinnedSandboxBinding());
        var result = WorkPlanValidator.ValidateAndSnapshot(
            WorkflowTestData.Snapshot(definition),
            WorkflowTestData.Plan(WorkflowTestData.Item("implementation", outputs: ["Src/Result.cs"])),
            context);
        Assert.True(result.IsValid, string.Join("; ", result.Issues.Select(issue => issue.Message)));
        return (
            result.Value!,
            context,
            result.Value!.Workflow.Definition.Steps.Single(step => step.Id == "setup"));
    }

    private static MafExecutionFixedWorkAssociation CreateAssociation(
        WorkPlanSnapshot plan,
        WorkPlanRunSelectionContext context,
        WorkflowStepDefinition step,
        SessionIdentity root)
    {
        const string selectionHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const long fence = 2;
        const long decisionVersion = 3;
        const long activationRevision = 1;
        var associationId = MafExecutionPlanner.CreateFixedWorkAssociationId(
            root, plan.Plan.Id, step.Id, activationRevision);
        return MafExecutionPlanner.CreateFixedWorkAssociation(
            plan,
            context,
            root,
            step,
            selectionHash,
            fence,
            decisionVersion,
            activationRevision,
            MafExecutionPlanner.CreateChildSessionId(root, plan.Plan.Id, associationId));
    }
}
