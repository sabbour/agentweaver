using FluentAssertions;
using System.Security.Cryptography;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs;
using Agentweaver.Api.Workflows;
using Agentweaver.Domain;
using Run = Agentweaver.Domain.Run;
using RunStatus = Agentweaver.Domain.RunStatus;

namespace Agentweaver.Tests.Runs;

/// <summary>
/// Replica-safety tests for the EF-backed <see cref="PendingRequestStore"/>.
///
/// The HITL review/confirm gate is armed by the background watch loop on one pod and consumed by a
/// later HTTP request that may land on a DIFFERENT pod. These tests use a shared in-memory SQLite
/// database and give EACH store instance its OWN root service provider (and therefore its OWN
/// <see cref="MemoryDbContext"/> connections via its scope factory) to simulate distinct API
/// replicas:
///   • a gate armed on one "replica" is visible and consumable on a DIFFERENT "replica";
///   • consume is atomic and single-use — a second consume of the same run returns null;
///   • re-arm (upsert) overwrites the previous gate for the same run id.
/// </summary>
public sealed class PendingRequestStoreTests : IDisposable
{
    private readonly SqliteConnection _keepAlive;
    private readonly string _connectionString;
    private readonly List<ServiceProvider> _providers = [];

    public PendingRequestStoreTests()
    {
        _connectionString = $"DataSource=file:pending-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();

        // Materialize the schema once on the shared database.
        var sp = NewReplicaServiceProvider();
        using var scope = sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<MemoryDbContext>().Database.EnsureCreated();
    }

    // Each service provider simulates a distinct replica: its own scope factory over the shared DB.
    private ServiceProvider NewReplicaServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddDbContext<MemoryDbContext>(o => o.UseSqlite(_connectionString));
        var sp = services.BuildServiceProvider();
        _providers.Add(sp);
        return sp;
    }

    private PendingRequestStore NewStoreOnSeparateReplica() =>
        new(NewReplicaServiceProvider().GetRequiredService<IServiceScopeFactory>());

    private static ExternalRequest NewRequest(string requestId)
    {
        var portInfo = new RequestPortInfo(
            new TypeId("Test.Asm", "Test.RequestType"),
            new TypeId("Test.Asm", "Test.ResponseType"),
            "review-port");
        return new ExternalRequest(portInfo, requestId, new PortableValue(requestId));
    }

    [Fact]
    public async Task TypedReview_RoundTripsAcrossReplicas_OnlyWhileCurrentAndUndecided()
    {
        var run = new Run
        {
            Id = RunId.New(), RepositoryPath = "unused", OriginatingBranch = "dev",
            ModelSource = ModelSource.GitHubCopilot, Task = "review",
            SubmittingUser = "octocat", Status = RunStatus.AwaitingReview,
            StartedAt = DateTimeOffset.UtcNow, TreeHash = "tree-1",
            CurrentOutputRevisionId = "rev-1"
        };
        var id = run.Id.ToString();
        var request = new ExternalRequest(
            new RequestPortInfo(new TypeId("Test", "Review"),
                new TypeId("Test", "Decision"), "review"),
            "req-review",
            new PortableValue(new WorkflowReviewRequest(id, "tree-1", "diff", 1,
                LifecycleGeneration: run.LifecycleGeneration)));
        var revision = new RunOutputRevision("rev-1", RunOutputRevision.CurrentSchemaVersion,
            run.Id, run.LifecycleGeneration, null, true, "tree-1",
            Convert.ToHexString(SHA256.HashData([])), null, [], DateTimeOffset.UtcNow);
        await NewStoreOnSeparateReplica().SetAsync(id, request, "octocat",
            lifecycleGeneration: run.LifecycleGeneration, reviewOutputRevisionId: "rev-1");
        var reader = NewStoreOnSeparateReplica();
        (await reader.GetActionableRequestKindAsync(run, revision)).Should().Be(PendingRequestDeliveryKinds.WorkflowReview);
        (await reader.GetActionableRequestKindAsync(run, null)).Should().BeNull();
        (await reader.GetActionableRequestKindAsync(run with { CurrentOutputRevisionId = "rev-2" }, revision)).Should().BeNull();
        (await reader.GetActionableRequestKindAsync(run with { TreeHash = "other" }, revision)).Should().BeNull();
        (await reader.GetActionableRequestKindAsync(run with { LifecycleGeneration = 2 }, revision)).Should().BeNull();
        (await reader.GetActionableRequestKindAsync(run with { Id = RunId.New() }, revision)).Should().BeNull();
        await reader.SetAsync(id, request, "octocat", lifecycleGeneration: run.LifecycleGeneration);
        (await reader.GetActionableRequestKindAsync(run, revision)).Should().BeNull(
            "a typed request without a producer-pinned review revision is not actionable");
        await reader.SetAsync(id, request, "octocat",
            lifecycleGeneration: run.LifecycleGeneration, reviewOutputRevisionId: "rev-1");
        using (var scope = NewReplicaServiceProvider().CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var row = await db.PendingRequests.SingleAsync(p => p.RunId == id);
            row.RequestId = "other";
            await db.SaveChangesAsync();
        }
        (await reader.GetActionableRequestKindAsync(run, revision)).Should().BeNull();
        using (var scope = NewReplicaServiceProvider().CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var row = await db.PendingRequests.SingleAsync(p => p.RunId == id);
            row.RequestId = request.RequestId;
            await db.SaveChangesAsync();
        }
        var decision = new WorkflowReviewDecision(true, ReviewedBy: "octocat");
        (await reader.TryQueueDeliveryAsync(id, PendingRequestDeliveryKinds.WorkflowReview,
            PendingRequestStore.CreateDecisionIdentity(request.RequestId, decision), decision, "octocat"))
            .Should().BeTrue();
        (await reader.GetActionableRequestKindAsync(run, revision)).Should().BeNull();
        (await reader.GetRequestKindAsync(id)).Should().Be(PendingRequestDeliveryKinds.WorkflowReview);
        (await reader.TryClaimDeliveryAsync(id, "owner", TimeSpan.Zero)).Should().NotBeNull();
        (await reader.GetActionableRequestKindAsync(run, revision)).Should().BeNull();
    }

    [Fact]
    public async Task LatestParentPlan_ControlsChildPauseAndDoesNotLetOlderWaitMaskFreshReview()
    {
        var run = new Run
        {
            Id = RunId.New(), RepositoryPath = "unused", OriginatingBranch = "dev",
            ModelSource = ModelSource.GitHubCopilot, Task = "fan out",
            SubmittingUser = "octocat", Status = RunStatus.AwaitingReview,
            StartedAt = DateTimeOffset.UtcNow, TreeHash = "tree",
            CurrentOutputRevisionId = "rev"
        };
        var id = run.Id.ToString();
        var review = new ExternalRequest(
            new RequestPortInfo(new TypeId("Test", "Review"), new TypeId("Test", "Decision"), "review"),
            "req-review", new PortableValue(new WorkflowReviewRequest(id, "tree", "diff", 1,
                LifecycleGeneration: run.LifecycleGeneration)));
        var revision = new RunOutputRevision("rev", RunOutputRevision.CurrentSchemaVersion,
            run.Id, run.LifecycleGeneration, null, true, "tree",
            Convert.ToHexString(SHA256.HashData([])), null, [], DateTimeOffset.UtcNow);
        var store = NewStoreOnSeparateReplica();
        await store.SetAsync(id, review, "octocat",
            lifecycleGeneration: run.LifecycleGeneration, reviewOutputRevisionId: "rev");
        (await store.GetActionableRequestKindAsync(run, revision)).Should().Be(PendingRequestDeliveryKinds.WorkflowReview);

        async Task<WorkPlan> AddPlanAsync(string state, string node)
        {
            using var scope = NewReplicaServiceProvider().CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var now = DateTimeOffset.UtcNow;
            var spec = new OutcomeSpec
            {
                ProjectId = "project", CoordinatorRunId = id, Goal = "fan",
                DesiredOutcome = "done", Scope = "workflow", Assumptions = "none",
                Status = "confirmed", CreatedAt = now, UpdatedAt = now
            };
            db.OutcomeSpecs.Add(spec);
            await db.SaveChangesAsync();
            var plan = new WorkPlan
            {
                OutcomeSpecId = spec.Id, ProjectId = "project", CoordinatorRunId = id,
                ParentRunId = id, ParentWorkflowNodeId = node,
                ParentResumeState = state, Status = "dispatching", CreatedAt = now, UpdatedAt = now
            };
            db.WorkPlans.Add(plan);
            await db.SaveChangesAsync();
            return plan;
        }

        await AddPlanAsync("waiting", "fan-old");
        (await store.GetActionableRequestKindAsync(run, revision)).Should().BeNull();
        var newPlan = await AddPlanAsync("delivered", "fan-new");
        (await store.GetActionableRequestKindAsync(run, revision)).Should().Be(PendingRequestDeliveryKinds.WorkflowReview,
            "only the latest parent-node plan can suppress a newer human review");
        var child = new ExternalRequest(
            new RequestPortInfo(new TypeId("Test", "Child"), new TypeId("Test", "Result"), "fan"),
            "req-child",
            new PortableValue(new WorkflowChildWorkPauseRequest(
                newPlan.Id, id, "fan-new", "join", RunId.New().ToString())));
        await store.SetAsync(id, child, "octocat", lifecycleGeneration: run.LifecycleGeneration);
        (await store.GetActionableRequestKindAsync(run, revision)).Should().BeNull(
            "the new plan has not yet bound its continuation request");
        using (var scope = NewReplicaServiceProvider().CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var plan = await db.WorkPlans.SingleAsync(p => p.Id == newPlan.Id);
            plan.ParentResumeState = "waiting";
            plan.ParentResumeRequestId = child.RequestId;
            await db.SaveChangesAsync();
        }
        (await NewStoreOnSeparateReplica().GetActionableRequestKindAsync(run, revision))
            .Should().Be(PendingRequestDeliveryKinds.WorkflowChildWork);
        (await store.GetRequestKindAsync(id)).Should().Be(PendingRequestDeliveryKinds.WorkflowChildWork,
            "POST must reject a typed waiting child pause as well as a queued child delivery");
        var decision = new WorkflowReviewDecision(true, ReviewedBy: "octocat");
        (await store.TryQueueDeliveryAsync(id, PendingRequestDeliveryKinds.WorkflowChildWork,
            PendingRequestStore.CreateDecisionIdentity(child.RequestId, decision), decision, "octocat"))
            .Should().BeTrue();
        (await store.GetActionableRequestKindAsync(run, revision)).Should().BeNull();
        (await store.GetRequestKindAsync(id)).Should().Be(PendingRequestDeliveryKinds.WorkflowChildWork);
    }

    [Fact]
    public async Task GateArmedOnOneReplica_IsConsumableOnAnother_ThenSingleUse()
    {
        const string runId = "run-cross-replica";

        // Replica A arms the gate (persisted to the shared DB).
        await NewStoreOnSeparateReplica().SetAsync(runId, NewRequest("req-A"), "octocat");

        // Replica B (a SEPARATE store/scope factory) reads it without consuming.
        var peek = await NewStoreOnSeparateReplica().GetAsync(runId);
        peek.Should().NotBeNull("the gate must be visible from any replica");
        peek!.OwnerUser.Should().Be("octocat");
        peek.Request.RequestId.Should().Be("req-A");
        peek.Request.PortInfo.PortId.Should().Be("review-port");

        // Replica B consumes it atomically.
        var consumed = await NewStoreOnSeparateReplica()
            .TryAbandonWaitingGateForHumanRevisionAsync(runId);
        consumed.Should().NotBeNull("the armed gate must be consumable cross-replica");
        consumed!.Request.RequestId.Should().Be("req-A");

        // Replica C tries to consume the same run again → already consumed (at-most-once).
        var second = await NewStoreOnSeparateReplica()
            .TryAbandonWaitingGateForHumanRevisionAsync(runId);
        second.Should().BeNull("a gate can be consumed at most once across all replicas");

        // And a plain read now sees nothing.
        (await NewStoreOnSeparateReplica().GetAsync(runId)).Should().BeNull();
    }

    [Fact]
    public async Task ConcurrentConsume_AcrossReplicas_ExactlyOneSucceeds()
    {
        const string runId = "run-concurrent";
        await NewStoreOnSeparateReplica().SetAsync(runId, NewRequest("req-X"), "octocat");

        var tasks = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => NewStoreOnSeparateReplica()
                .TryAbandonWaitingGateForHumanRevisionAsync(runId)))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        results.Count(r => r is not null).Should().Be(1,
            "the pending gate is single-consume even when many replicas race");
    }

    [Fact]
    public async Task SetAsync_ReArmsSameRun_OverwritesPreviousGate()
    {
        const string runId = "run-rearm";
        await NewStoreOnSeparateReplica().SetAsync(runId, NewRequest("req-old"), "octocat");
        await NewStoreOnSeparateReplica().SetAsync(runId, NewRequest("req-new"), "hubot");

        var peek = await NewStoreOnSeparateReplica().GetAsync(runId);
        peek.Should().NotBeNull();
        peek!.Request.RequestId.Should().Be("req-new", "re-arming must upsert in place by run id");
        peek.OwnerUser.Should().Be("hubot");

        // Still exactly one row (no duplicate gate for the run).
        using var scope = NewReplicaServiceProvider().CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        (await db.PendingRequests.CountAsync(p => p.RunId == runId)).Should().Be(1);
    }

    [Fact]
    public async Task DeferredDecisionClaim_CrashBeforeSend_KeepsDecisionRetryable()
    {
        const string runId = "run-deferred-crash-before-send";
        var decision = new WorkflowReviewDecision(
            Approved: true,
            RequestChanges: false,
            Feedback: "ship it",
            ReviewedBy: "octocat");

        await NewStoreOnSeparateReplica().SetAsync(runId, NewRequest("req-deferred"), "octocat");

        var queued = await NewStoreOnSeparateReplica().TryQueueDeliveryAsync(
            runId,
            PendingRequestDeliveryKinds.WorkflowReview,
            PendingRequestStore.CreateDecisionIdentity("req-deferred", decision),
            decision,
            "octocat");
        queued.Should().BeTrue("the deferred decision must be persisted before the owner replica sends it");
        using (var scope = NewReplicaServiceProvider().CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var row = await db.PendingRequests.AsNoTracking().SingleAsync(p => p.RunId == runId);
            row.DeliveryState.Should().Be(PendingRequestDeliveryStates.Ready);
            row.DecisionIdentity.Should().NotBeNull();
            row.ResponseJson.Should().NotBeNull();
        }

        var firstClaim = await NewStoreOnSeparateReplica().TryClaimDeliveryAsync(
            runId,
            "owner-a",
            staleAfter: TimeSpan.FromHours(1));
        firstClaim.Should().NotBeNull("the owner replica should be able to claim the queued decision");
        firstClaim!.Request.RequestId.Should().Be("req-deferred");
        firstClaim.GetResponse<WorkflowReviewDecision>().Should().BeEquivalentTo(decision);

        // Fault injection: owner-a dies after claiming the durable decision but before SendResponseAsync.
        // A recovery scanner must be able to reclaim the exact same gate/decision identity; the old
        // read-then-delete consume path loses both rows and cannot satisfy this assertion.
        var retryClaim = await NewStoreOnSeparateReplica().TryClaimDeliveryAsync(
            runId,
            "owner-b",
            staleAfter: TimeSpan.Zero);
        retryClaim.Should().NotBeNull("a crash before send must not destroy the resume handoff");
        retryClaim!.Request.RequestId.Should().Be("req-deferred");
        retryClaim.DecisionIdentity.Should().Be(firstClaim.DecisionIdentity);
        retryClaim.GetResponse<WorkflowReviewDecision>().Should().BeEquivalentTo(decision);
    }

    [Fact]
    public async Task HumanRevisionAbandon_DoesNotDeleteQueuedDelivery()
    {
        const string runId = "run-human-revision-after-queue";
        var decision = new WorkflowReviewDecision(
            Approved: false,
            RequestChanges: true,
            Feedback: "revise",
            ReviewedBy: "octocat");
        var identity = PendingRequestStore.CreateDecisionIdentity("req-human-revision", decision);

        var store = NewStoreOnSeparateReplica();
        await store.SetAsync(runId, NewRequest("req-human-revision"), "octocat");
        (await store.TryQueueDeliveryAsync(
            runId,
            PendingRequestDeliveryKinds.WorkflowReview,
            identity,
            decision,
            "octocat")).Should().BeTrue();

        (await NewStoreOnSeparateReplica().TryAbandonWaitingGateForHumanRevisionAsync(runId))
            .Should().BeNull("abandonment must not erase a decision already queued for delivery");

        var claim = await NewStoreOnSeparateReplica().TryClaimDeliveryAsync(
            runId,
            "owner-after-abandon-race",
            TimeSpan.Zero);
        claim.Should().NotBeNull("the queued resume handoff must survive a racing human-revision cleanup");
        claim!.DecisionIdentity.Should().Be(identity);
    }

    [Fact]
    public async Task MatchingDelivery_RejectsConflictingDecision()
    {
        const string runId = "run-conflicting-decision";
        var confirm = new WorkflowReviewDecision(
            Approved: true,
            RequestChanges: false,
            Feedback: null,
            ReviewedBy: "octocat");
        var revise = confirm with
        {
            Approved = false,
            RequestChanges = true,
            Feedback = "revise",
        };

        var store = NewStoreOnSeparateReplica();
        await store.SetAsync(runId, NewRequest("req-conflict"), "octocat");
        (await store.TryQueueDeliveryAsync(
            runId,
            PendingRequestDeliveryKinds.WorkflowReview,
            PendingRequestStore.CreateDecisionIdentity("req-conflict", confirm),
            confirm,
            "octocat")).Should().BeTrue();

        (await NewStoreOnSeparateReplica().MatchesUndeliveredDeliveryAsync(
            runId,
            PendingRequestDeliveryKinds.WorkflowReview,
            confirm)).Should().BeTrue();
        (await NewStoreOnSeparateReplica().MatchesUndeliveredDeliveryAsync(
            runId,
            PendingRequestDeliveryKinds.WorkflowReview,
            revise)).Should().BeFalse("a conflicting human decision is not an idempotent retry");
    }

    [Fact]
    public async Task DeliveredDecision_IsNotClaimedAgain()
    {
        const string runId = "run-deferred-delivered";
        var decision = new WorkflowReviewDecision(
            Approved: true,
            RequestChanges: false,
            Feedback: null,
            ReviewedBy: "octocat");
        var identity = PendingRequestStore.CreateDecisionIdentity("req-delivered", decision);

        var store = NewStoreOnSeparateReplica();
        await store.SetAsync(runId, NewRequest("req-delivered"), "octocat");
        (await store.TryQueueDeliveryAsync(
            runId,
            PendingRequestDeliveryKinds.WorkflowReview,
            identity,
            decision,
            "octocat")).Should().BeTrue();

        var claim = await store.TryClaimDeliveryAsync(runId, "owner-a", TimeSpan.Zero);
        claim.Should().NotBeNull();
        (await store.MarkDeliveredAsync(runId, identity, claim!.ClaimOwner, claim.ClaimedAt)).Should().BeTrue();

        (await NewStoreOnSeparateReplica().TryClaimDeliveryAsync(runId, "owner-b", TimeSpan.Zero))
            .Should().BeNull("delivered gates must no-op during recovery instead of advancing twice");
        (await NewStoreOnSeparateReplica().GetAsync(runId))
            .Should().BeNull("a delivered handoff is no longer a human-pending gate");
    }

    [Fact]
    public async Task RearmingSameRequest_DoesNotReopenDeliveredDecision()
    {
        const string runId = "run-rearm-delivered";
        var request = NewRequest("req-delivered-rearm");
        var decision = new WorkflowReviewDecision(
            Approved: true,
            RequestChanges: false,
            Feedback: null,
            ReviewedBy: "octocat");
        var identity = PendingRequestStore.CreateDecisionIdentity(request.RequestId, decision);

        var store = NewStoreOnSeparateReplica();
        await store.SetAsync(runId, request, "octocat");
        (await store.TryQueueDeliveryAsync(
            runId,
            PendingRequestDeliveryKinds.WorkflowReview,
            identity,
            decision,
            "octocat")).Should().BeTrue();
        var claim = await store.TryClaimDeliveryAsync(runId, "owner-a", TimeSpan.Zero);
        claim.Should().NotBeNull();
        (await store.MarkDeliveredAsync(runId, identity, claim!.ClaimOwner, claim.ClaimedAt)).Should().BeTrue();

        await NewStoreOnSeparateReplica().SetAsync(runId, request, "octocat");

        (await NewStoreOnSeparateReplica().GetAsync(runId))
            .Should().BeNull("same-request restart replay must not reopen a delivered gate");
        (await NewStoreOnSeparateReplica().TryClaimDeliveryAsync(runId, "owner-b", TimeSpan.Zero))
            .Should().BeNull("same-request restart replay must not make a delivered decision retryable");
    }

    [Fact]
    public async Task RearmingSameRequest_DoesNotEraseQueuedDecision()
    {
        const string runId = "run-rearm-queued";
        var decision = new WorkflowReviewDecision(
            Approved: true,
            RequestChanges: false,
            Feedback: "ship it",
            ReviewedBy: "octocat");
        var request = NewRequest("req-rearm");
        var identity = PendingRequestStore.CreateDecisionIdentity(request.RequestId, decision);

        var store = NewStoreOnSeparateReplica();
        await store.SetAsync(runId, request, "octocat");
        (await store.TryQueueDeliveryAsync(
            runId,
            PendingRequestDeliveryKinds.WorkflowReview,
            identity,
            decision,
            "octocat")).Should().BeTrue();

        await NewStoreOnSeparateReplica().SetAsync(runId, request, "octocat");

        var retryClaim = await NewStoreOnSeparateReplica().TryClaimDeliveryAsync(runId, "owner-after-rearm", TimeSpan.Zero);
        retryClaim.Should().NotBeNull("restart re-arm for the same request must not erase a queued decision");
        retryClaim!.DecisionIdentity.Should().Be(identity);
        retryClaim.GetResponse<WorkflowReviewDecision>().Should().BeEquivalentTo(decision);
    }

    [Fact]
    public async Task QueueDelivery_ToleratesPreMigrationRowsWithoutRequestIdColumnValue()
    {
        const string runId = "run-premigration-null-request-id";
        var request = NewRequest("req-premigration");
        var decision = new WorkflowReviewDecision(
            Approved: true,
            RequestChanges: false,
            Feedback: null,
            ReviewedBy: "octocat");
        var identity = PendingRequestStore.CreateDecisionIdentity(request.RequestId, decision);

        var store = NewStoreOnSeparateReplica();
        await store.SetAsync(runId, request, "octocat");
        using (var scope = NewReplicaServiceProvider().CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var row = await db.PendingRequests.SingleAsync(p => p.RunId == runId);
            row.RequestId = null;
            await db.SaveChangesAsync();
        }

        (await store.TryQueueDeliveryAsync(
            runId,
            PendingRequestDeliveryKinds.WorkflowReview,
            identity,
            decision,
            "octocat")).Should().BeTrue("rows created before the RequestId column existed still carry it in RequestJson");

        using var assertScope = NewReplicaServiceProvider().CreateScope();
        var assertDb = assertScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var updated = await assertDb.PendingRequests.AsNoTracking().SingleAsync(p => p.RunId == runId);
        updated.RequestId.Should().Be(request.RequestId);
        updated.DecisionIdentity.Should().Be(identity);
    }

    public void Dispose()
    {
        foreach (var sp in _providers)
            sp.Dispose();
        _keepAlive.Dispose();
    }
}
