using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed partial class RuntimeNumericLimitsPostgresTests
{
    [Fact]
    public async Task ConfirmedDispatchRootContextPreservesOwnerAuthorityAndActionDenial()
    {
        var (registration, _, _) = await PrepareConfirmedRootRubberduckAsync();
        ((System.Security.Claims.ClaimsIdentity)_context.User.Identity!).AddClaim(
            new System.Security.Claims.Claim("aud", _options.Audience));
        var owner = await CoordinationEndpoints.ReadRuntimeOwnerContextCoreAsync(
            _root.ProjectId, _root.RunId, _root.SessionId, _context, _options, ProjectsClient(),
            new CoordinationOwnerStore(fixture.DataSource, _schema), _decisions, _runSelectionContexts,
            new PostgresMafCheckpointStore(fixture.DataSource, _schema, null), default,
            allowCoordinatorRoot: true);

        Assert.Equal(_actor.Issuer, owner.ActorIssuer);
        Assert.Equal(_actor.Subject, owner.ActorId);
        Assert.Equal(registration.Binding.ExecutionFence, owner.ExecutionFence);
        Assert.Equal(registration.Binding.AcceptedSelectionHash, owner.AcceptedSelectionHash);
        Assert.Equal(RuntimeActionContract.CoordinatorRootStepId, owner.AgentId);
        Assert.Null(owner.WorkflowStepId);

        var actorFailure = await Assert.ThrowsAsync<CoordinationException>(() =>
            new CoordinationOwnerStore(fixture.DataSource, _schema).ReadRuntimeOwnerStateForRegistrationAsync(
                new CoordinationActor(_actor.Issuer, "other-actor"), _root, owner.TenantId,
                owner.AcceptedSelectionHash, default));
        Assert.Equal("session_writer_mismatch", actorFailure.Code);
        var operation = Guid.NewGuid();
        var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            GrantStore().AdmitCoordinatorRootModelTurnAsync(
                _context, registration, operation, new string('a', 64), owner.DecisionStateVersion, default));
        Assert.Equal("runtime_action_owner_unavailable", failure.Code);
        foreach (var actionId in new[] { "tool.read", "tool.write" })
        {
            var request = new RuntimeActionRequest(
                1, registration.RuntimeInstanceId, registration.Revision, owner.ExecutionFence,
                Guid.NewGuid(), actionId, new string('a', 64))
            {
                IsToolInvocation = true
            };
            failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                GrantStore().IssueRuntimeActionGrantAsync(
                    _context, registration, request, default, reserveToolInvocation: true));
            Assert.Equal("runtime_action_owner_unavailable", failure.Code);
        }
        Assert.Equal(0, await CountRuntimeGrantsAsync());
    }
}
