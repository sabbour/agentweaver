using System.Collections.Immutable;
using System.Security.Claims;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Agentweaver.Providers;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class ExecutableActionGuardTests
{
    private const string TrustedIssuer = "https://identity.test/";
    private const string AllowedAction = "coordinator.shell.execute";
    private const string AllowPolicy = """
        apiVersion: governance.toolkit/v1
        version: "1.0"
        name: platform-policy
        scope: global
        default_action: deny
        rules:
          - name: allow-shell
            condition: "action_id == 'coordinator.shell.execute'"
            action: allow
            priority: 100
        """;
    private const string DenyPolicy = """
        apiVersion: governance.toolkit/v1
        version: "1.0"
        name: platform-policy
        scope: global
        default_action: deny
        rules: []
        """;

    [Fact]
    public async Task MissingGrantOwnerLookupDeniesWithoutReadingJournalOrInvokingEffect()
    {
        var journal = new RecordingJournal();
        var guard = CreateGuard(journal, grantOwnerLookup: null);
        var effectInvoked = false;

        var result = await guard.ExecuteAsync(
            Invocation(),
            _ =>
            {
                effectInvoked = true;
                return Task.FromResult("unexpected");
            });

        Assert.Equal(PolicyEvaluationOutcome.Deny, result.Outcome);
        Assert.Equal(PolicyEvaluationReasonCode.NoEffectiveGrant, result.ReasonCode);
        Assert.False(result.EffectInvoked);
        Assert.False(effectInvoked);
        Assert.Equal(0, journal.AppendCalls);
    }

    [Fact]
    public async Task UnknownOrRevokedGrantDeniesWithoutCallingTheEffect()
    {
        foreach (var status in new[]
        {
            ExecutableActionGrantLookupStatus.Unknown,
            ExecutableActionGrantLookupStatus.Revoked,
            ExecutableActionGrantLookupStatus.Expired
        })
        {
            var journal = new RecordingJournal();
            var guard = CreateGuard(
                journal, new GrantLookup(new ExecutableActionGrantLookupResult(status)));
            var effectInvoked = false;

            var result = await guard.ExecuteAsync(
                Invocation(),
                _ =>
                {
                    effectInvoked = true;
                    return Task.FromResult("unexpected");
                });

            Assert.Equal(PolicyEvaluationOutcome.Deny, result.Outcome);
            Assert.Equal(PolicyEvaluationReasonCode.NoEffectiveGrant, result.ReasonCode);
            Assert.False(effectInvoked);
            Assert.Equal(0, journal.AppendCalls);
        }
    }

    [Fact]
    public async Task StaleFenceAndUngrantableCoordinatorActionsAreDeniedEvenWhenPolicyWouldAllow()
    {
        var expiredFence = Grant() with { Fence = 6 };
        var fenceJournal = new RecordingJournal();
        var fenceResult = await CreateGuard(
                fenceJournal, new GrantLookup(ExecutableActionGrantLookupResult.Current(expiredFence)))
            .ExecuteAsync(Invocation(), _ => Task.FromResult("unexpected"));

        Assert.Equal(PolicyEvaluationOutcome.Deny, fenceResult.Outcome);
        Assert.Equal(PolicyEvaluationReasonCode.StaleFence, fenceResult.ReasonCode);
        Assert.Equal(0, fenceJournal.AppendCalls);

        var restrictedGrant = Grant() with
        {
            ActionIds = ImmutableHashSet.Create(StringComparer.Ordinal, "api.read")
        };
        foreach (var actionId in new[]
        {
            "coordinator.shell.execute",
            "coordinator.code.execute",
            "coordinator.merge"
        })
        {
            var journal = new RecordingJournal();
            var ungrantedResult = await CreateGuard(
                    journal,
                    new GrantLookup(ExecutableActionGrantLookupResult.Current(restrictedGrant)))
                .ExecuteAsync(Invocation() with { ActionId = actionId }, _ => Task.FromResult("unexpected"));

            Assert.Equal(PolicyEvaluationOutcome.Deny, ungrantedResult.Outcome);
            Assert.Equal(PolicyEvaluationReasonCode.NoEffectiveGrant, ungrantedResult.ReasonCode);
            Assert.Equal(0, journal.AppendCalls);
        }
    }

    [Fact]
    public async Task ExpiredOrMismatchedCurrentDescriptorIsDenied()
    {
        var expired = Grant() with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        var wrongRun = Grant() with { RunId = "other-run" };
        var wrongRevision = Grant() with
        {
            Reference = new ExecutableActionGrantReference("grant-1", "revision-2")
        };

        foreach (var grant in new[] { expired, wrongRun, wrongRevision })
        {
            var journal = new RecordingJournal();
            var result = await CreateGuard(
                    journal, new GrantLookup(ExecutableActionGrantLookupResult.Current(grant)))
                .ExecuteAsync(Invocation(), _ => Task.FromResult("unexpected"));

            Assert.Equal(PolicyEvaluationOutcome.Deny, result.Outcome);
            Assert.Equal(PolicyEvaluationReasonCode.NoEffectiveGrant, result.ReasonCode);
            Assert.Equal(0, journal.AppendCalls);
        }
    }

    [Fact]
    public async Task MissingDuplicateOrMismatchedIssuerBindingDeniesWithoutAppendingOrInvokingEffect()
    {
        foreach (var (principal, expectedLookupCalls) in new[]
        {
            (RunPrincipal(subjectIssuer: "LOCAL AUTHORITY"), 0),
            (RunPrincipal(duplicateIssuer: "https://other.identity.test/"), 0),
            (RunPrincipal(subjectIssuer: "https://other.identity.test/"), 1),
            (RunPrincipal(projectIssuer: "https://other.identity.test/"), 0)
        })
        {
            var journal = new RecordingJournal();
            var lookup = new GrantLookup(ExecutableActionGrantLookupResult.Current(Grant()));
            var effectInvoked = false;

            var result = await CreateGuard(journal, lookup)
                .ExecuteAsync(Invocation() with { Caller = principal }, _ =>
                {
                    effectInvoked = true;
                    return Task.FromResult("unexpected");
                });

            Assert.Equal(PolicyEvaluationOutcome.Deny, result.Outcome);
            Assert.Equal(PolicyEvaluationReasonCode.NoEffectiveGrant, result.ReasonCode);
            Assert.False(result.EffectInvoked);
            Assert.False(effectInvoked);
            Assert.Equal(expectedLookupCalls, lookup.CallCount);
            Assert.Equal(0, journal.AppendCalls);
        }
    }

    [Fact]
    public async Task PolicyDenyDoesNotAppendOrInvokeTheEffect()
    {
        var journal = new RecordingJournal();
        var guard = CreateGuard(
            journal,
            new GrantLookup(ExecutableActionGrantLookupResult.Current(Grant())),
            DenyPolicy);
        var effectInvoked = false;

        var result = await guard.ExecuteAsync(
            Invocation(DenyPolicy),
            _ =>
            {
                effectInvoked = true;
                return Task.FromResult("unexpected");
            });

        Assert.Equal(PolicyEvaluationOutcome.Deny, result.Outcome);
        Assert.False(effectInvoked);
        Assert.Equal(0, journal.AppendCalls);
    }

    [Fact]
    public async Task PolicyAllowCannotBypassJournalWriterProvenanceFailure()
    {
        var journal = new RecordingJournal
        {
            AppendFailure = new SessionAccessDeniedException(
                "Policy evaluation events require trusted Orchestrator Core writer provenance.")
        };
        var lookup = new GrantLookup(ExecutableActionGrantLookupResult.Current(Grant()));
        var guard = CreateGuard(journal, lookup);
        var effectInvoked = false;

        var result = await guard.ExecuteAsync(
            Invocation(),
            _ =>
            {
                effectInvoked = true;
                return Task.FromResult("unexpected");
            });

        Assert.Equal(PolicyEvaluationOutcome.Error, result.Outcome);
        Assert.Equal(PolicyEvaluationReasonCode.EvaluationFailed, result.ReasonCode);
        Assert.False(result.EffectInvoked);
        Assert.False(effectInvoked);
        Assert.Equal(1, journal.AppendCalls);
        Assert.Equal(2, lookup.CallCount);
        Assert.Equal(new ExecutableActionGrantReference("grant-1", "revision-1"), lookup.LastReference);
        var payload = Assert.IsType<PolicyEvaluationSessionPayload>(journal.LastAppend!.Payload);
        Assert.Equal("tenant-1", payload.TenantId);
        Assert.Equal("step-1", payload.StepId);
        Assert.Equal("grant-1", payload.GrantId);
        Assert.Equal("revision-1", payload.GrantRevision);
        Assert.Equal(7, payload.Fence);
    }

    [Theory]
    [InlineData(ExecutableActionSourceReceiptWriteStatus.Duplicate, PolicyEvaluationReasonCode.EvaluationFailed)]
    [InlineData(ExecutableActionSourceReceiptWriteStatus.Rejected, PolicyEvaluationReasonCode.EvaluationFailed)]
    [InlineData(ExecutableActionSourceReceiptWriteStatus.Unavailable, PolicyEvaluationReasonCode.ProviderUnavailable)]
    public async Task SourceReceiptWriterMustStoreItsTypedReceiptBeforeTheEventsAppend(
        ExecutableActionSourceReceiptWriteStatus status,
        PolicyEvaluationReasonCode expectedReason)
    {
        var journal = new RecordingJournal();
        var writer = new SourceReceiptWriter(
            new ExecutableActionSourceReceiptWriteResult(status));
        var invocation = Invocation();
        var guard = CreateGuard(
            journal,
            new GrantLookup(ExecutableActionGrantLookupResult.Current(Grant())),
            sourceReceiptWriter: writer);
        var effectInvoked = false;

        var result = await guard.ExecuteAsync(invocation, _ =>
        {
            effectInvoked = true;
            return Task.FromResult("unexpected");
        });

        Assert.Equal(PolicyEvaluationOutcome.Error, result.Outcome);
        Assert.Equal(expectedReason, result.ReasonCode);
        Assert.False(result.EffectInvoked);
        Assert.False(effectInvoked);
        Assert.Equal(1, writer.CallCount);
        Assert.NotNull(writer.LastReceipt);
        Assert.Equal("session-1", writer.LastReceipt!.SessionId);
        Assert.Equal(invocation.GrantReference, writer.LastReceipt.Grant.Reference);
        Assert.Equal(invocation.Fence, writer.LastReceipt.Grant.Fence);
        Assert.Equal(invocation.EventId, writer.LastReceipt.ReceiptId);
        Assert.Equal(invocation.ActionId, writer.LastReceipt.ActionId);
        Assert.Equal(0, journal.AppendCalls);
    }

    [Fact]
    public async Task SourceReceiptWriterFailureDoesNotFallBackToOrBypassEventsAppend()
    {
        var journal = new RecordingJournal();
        var writer = new SourceReceiptWriter(exception: new InvalidOperationException("owner unavailable"));
        var guard = CreateGuard(
            journal,
            new GrantLookup(ExecutableActionGrantLookupResult.Current(Grant())),
            sourceReceiptWriter: writer);
        var effectInvoked = false;

        var result = await guard.ExecuteAsync(Invocation(), _ =>
        {
            effectInvoked = true;
            return Task.FromResult("unexpected");
        });

        Assert.Equal(PolicyEvaluationOutcome.Error, result.Outcome);
        Assert.Equal(PolicyEvaluationReasonCode.EvaluationFailed, result.ReasonCode);
        Assert.False(result.EffectInvoked);
        Assert.False(effectInvoked);
        Assert.Equal(1, writer.CallCount);
        Assert.Equal(0, journal.AppendCalls);
    }

    [Fact]
    public async Task GrantRevocationAfterSourceReceiptStoragePreventsEventsAppendAndEffect()
    {
        var journal = new RecordingJournal();
        var lookup = new GrantLookup(
            ExecutableActionGrantLookupResult.Current(Grant()),
            new(ExecutableActionGrantLookupStatus.Revoked));
        var writer = new SourceReceiptWriter();
        var effectInvoked = false;

        var result = await CreateGuard(journal, lookup, sourceReceiptWriter: writer)
            .ExecuteAsync(Invocation(), _ =>
            {
                effectInvoked = true;
                return Task.FromResult("unexpected");
            });

        Assert.Equal(PolicyEvaluationOutcome.Deny, result.Outcome);
        Assert.Equal(PolicyEvaluationReasonCode.NoEffectiveGrant, result.ReasonCode);
        Assert.False(result.EffectInvoked);
        Assert.False(effectInvoked);
        Assert.Equal(1, writer.CallCount);
        Assert.Equal(0, journal.AppendCalls);
        Assert.Equal(2, lookup.CallCount);
    }

    [Fact]
    public async Task GrantLookupErrorAndMissingPolicyOrJournalFailClosed()
    {
        var lookupError = await CreateGuard(
                new RecordingJournal(),
                new GrantLookup(new ExecutableActionGrantLookupResult(ExecutableActionGrantLookupStatus.Error)))
            .ExecuteAsync(Invocation(), _ => Task.FromResult("unexpected"));
        var missingPolicy = await new ExecutableActionGuard(
                policyProvider: null,
                policyOptions: Options(AllowPolicy),
                sessionsJournal: new RecordingJournal(),
                grantOwnerLookup: new GrantLookup(
                    ExecutableActionGrantLookupResult.Current(Grant())))
            .ExecuteAsync(Invocation(), _ => Task.FromResult("unexpected"));
        var missingJournal = await new ExecutableActionGuard(
                new AgtPolicyProvider(),
                Options(AllowPolicy),
                sessionsJournal: null,
                grantOwnerLookup: new GrantLookup(
                    ExecutableActionGrantLookupResult.Current(Grant())))
            .ExecuteAsync(Invocation(), _ => Task.FromResult("unexpected"));
        var missingPinJournal = new RecordingJournal();
        var missingPin = await CreateGuard(
                missingPinJournal,
                new GrantLookup(ExecutableActionGrantLookupResult.Current(Grant())))
            .ExecuteAsync(Invocation() with { PolicyBinding = null }, _ => Task.FromResult("unexpected"));
        var changedPinJournal = new RecordingJournal();
        var changedPin = await CreateGuard(
                changedPinJournal,
                new GrantLookup(ExecutableActionGrantLookupResult.Current(Grant())))
            .ExecuteAsync(Invocation(DenyPolicy), _ => Task.FromResult("unexpected"));

        Assert.Equal(PolicyEvaluationOutcome.Error, lookupError.Outcome);
        Assert.Equal(PolicyEvaluationReasonCode.ProviderUnavailable, lookupError.ReasonCode);
        Assert.Equal(PolicyEvaluationOutcome.Error, missingPolicy.Outcome);
        Assert.Equal(PolicyEvaluationOutcome.Error, missingJournal.Outcome);
        Assert.Equal(PolicyEvaluationOutcome.Error, missingPin.Outcome);
        Assert.Equal(PolicyEvaluationReasonCode.ProviderUnavailable, missingPin.ReasonCode);
        Assert.Equal(PolicyEvaluationOutcome.Error, changedPin.Outcome);
        Assert.Equal(PolicyEvaluationReasonCode.ProviderUnavailable, changedPin.ReasonCode);
        Assert.False(lookupError.EffectInvoked);
        Assert.False(missingPolicy.EffectInvoked);
        Assert.False(missingJournal.EffectInvoked);
        Assert.False(missingPin.EffectInvoked);
        Assert.False(changedPin.EffectInvoked);
        Assert.Equal(0, missingPinJournal.AppendCalls);
        Assert.Equal(0, changedPinJournal.AppendCalls);
    }

    private static ExecutableActionGuard CreateGuard(
        RecordingJournal journal,
        IExecutableActionGrantOwnerLookup? grantOwnerLookup,
        string policy = AllowPolicy,
        IExecutableActionSourceReceiptWriter? sourceReceiptWriter = null) =>
        new(
            new AgtPolicyProvider(),
            Options(policy),
            journal,
            grantOwnerLookup,
            sourceReceiptWriter: sourceReceiptWriter ?? new SourceReceiptWriter());

    private static AgtPolicyProviderOptions Options(string policy) =>
        new("agt-policy-resource", 3,
            policy == AllowPolicy ? "agt-policy-allow-v1" : "agt-policy-deny-v1", [policy]);

    private static ExecutableActionInvocation Invocation(string policy = AllowPolicy) =>
        new(
            RunPrincipal(),
            "session-1",
            "step-1",
            AllowedAction,
            "coordination.action",
            new ExecutableActionGrantReference("grant-1", "revision-1"),
            7,
            PolicyBinding(policy),
            Guid.NewGuid());

    private static ClaimsPrincipal RunPrincipal(
        string? subjectIssuer = TrustedIssuer,
        string? projectIssuer = null,
        string? runIssuer = null,
        string? duplicateIssuer = null)
    {
        var identities = new List<ClaimsIdentity>
        {
            RunIdentity(subjectIssuer, projectIssuer ?? subjectIssuer, runIssuer ?? subjectIssuer)
        };
        if (duplicateIssuer is not null)
            identities.Add(RunIdentity(duplicateIssuer, duplicateIssuer, duplicateIssuer));
        return new ClaimsPrincipal(identities);
    }

    private static ValidatedExecutableActionGrant Grant() =>
        new(
            new ExecutableActionGrantReference("grant-1", "revision-1"),
            ExecutableActionGrantState.Active,
            TrustedIssuer,
            "33333333-3333-3333-3333-333333333333",
            "tenant-1",
            "project-1",
            "run-1",
            "session-1",
            "step-1",
            ImmutableHashSet.Create(StringComparer.Ordinal, AllowedAction),
            "coordination.action",
            7,
            DateTimeOffset.UtcNow.AddMinutes(5));

    private static ClaimsIdentity RunIdentity(string? subjectIssuer, string? projectIssuer, string? runIssuer)
    {
        static Claim Claim(string type, string value, string? issuer) =>
            issuer is null
                ? new Claim(type, value)
                : new Claim(type, value, ClaimValueTypes.String, issuer);

        return new ClaimsIdentity(
            [
                Claim("sub", "33333333-3333-3333-3333-333333333333", subjectIssuer),
                Claim("project_id", "project-1", projectIssuer),
                Claim("run_id", "run-1", runIssuer)
            ],
            "validated-run-token");
    }

    private static PinnedProviderBinding PolicyBinding(string policy)
    {
        var provider = new AgtPolicyProvider();
        var options = Options(policy);
        var catalog = Assert.IsType<ProviderCatalog>(ProviderCatalog.Create(
            [provider.CreateRegistration(options)],
            [new ProviderSelection(ProviderSeam.Policy, AgtPolicyProvider.ProviderId)],
            []).Value);
        var result = provider.ResolveNegotiateAndPinAsync(
            new ProviderResolver(catalog), options, "run-1").GetAwaiter().GetResult();
        return Assert.IsType<PinnedProviderBinding>(result.Value);
    }

    private sealed class GrantLookup(params ExecutableActionGrantLookupResult[] results)
        : IExecutableActionGrantOwnerLookup
    {
        public int CallCount { get; private set; }
        public ExecutableActionGrantReference? LastReference { get; private set; }

        public Task<ExecutableActionGrantLookupResult> GetCurrentAsync(
            ExecutableActionGrantReference reference,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastReference = reference;
            return Task.FromResult(results[Math.Min(CallCount - 1, results.Length - 1)]);
        }
    }

    private sealed class SourceReceiptWriter(
        ExecutableActionSourceReceiptWriteResult? result = null,
        Exception? exception = null)
        : IExecutableActionSourceReceiptWriter
    {
        public int CallCount { get; private set; }
        public ExecutableActionSourceReceipt? LastReceipt { get; private set; }

        public Task<ExecutableActionSourceReceiptWriteResult> StoreAsync(
            ExecutableActionSourceReceipt receipt,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastReceipt = receipt;
            return exception is not null
                ? Task.FromException<ExecutableActionSourceReceiptWriteResult>(exception)
                : Task.FromResult(result ?? new(ExecutableActionSourceReceiptWriteStatus.Stored));
        }
    }

    private sealed class RecordingJournal : ISessionsJournal
    {
        public int AppendCalls { get; private set; }
        public AppendSessionEvent? LastAppend { get; private set; }
        public Exception? AppendFailure { get; init; }

        public Task<SessionRecord> CreateSessionAsync(
            ClaimsPrincipal principal,
            string sessionId,
            SessionProviderBinding binding,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SessionProviderBinding> GetProviderBindingAsync(
            ClaimsPrincipal principal,
            string sessionId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SessionProviderBinding> GetRunProviderBindingAsync(
            ClaimsPrincipal principal,
            string projectId,
            string runId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SessionAppendResult> AppendAsync(
            ClaimsPrincipal principal,
            string sessionId,
            AppendSessionEvent input,
            CancellationToken cancellationToken = default)
        {
            AppendCalls++;
            LastAppend = input;
            return Task.FromException<SessionAppendResult>(
                AppendFailure ?? new SessionAccessDeniedException("The writer is not trusted."));
        }

        public Task<SessionForkResult> ForkFromExplicitEventAsync(
            ClaimsPrincipal principal,
            string sourceSessionId,
            SessionForkRequest request,
            Func<CancellationToken, Task> validateAdmission,
            CancellationToken cancellationToken = default) =>
            throw new SessionForkUnsupportedException("The recording journal does not support session forks.");

        public Task<SessionEventPage> ReplayAsync(
            ClaimsPrincipal principal,
            SessionEventPageRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SessionEventPage> ReplayRunAsync(
            ClaimsPrincipal principal,
            SessionRunEventPageRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<SessionEventDelivery> SubscribeAsync(
            ClaimsPrincipal principal,
            SessionSubscriptionRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<SessionEventDelivery> SubscribeRunAsync(
            ClaimsPrincipal principal,
            SessionRunSubscriptionRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
