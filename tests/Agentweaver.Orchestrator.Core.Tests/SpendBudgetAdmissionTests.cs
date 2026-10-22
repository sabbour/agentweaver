using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class SpendBudgetAdmissionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly UsageSpendPeriod September =
        new("2026-09", new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
    private static readonly UsageSpendPeriod October =
        new("2026-10", new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));

    private static SpendBudget Copilot(string id, string scope, decimal? soft, decimal? hard, decimal reserve) =>
        new(id, 1, scope, SdkMeterSources.CopilotNanoAiu, "AIC", soft, hard, reserve);

    private static SpendBudget Byok(string id, string scope, decimal? soft, decimal? hard, decimal reserve) =>
        new(id, 1, scope, SdkMeterSources.ByokTokens, "USD", soft, hard, reserve);

    [Fact]
    public void ContractRejectsInconsistentBudgets()
    {
        // Valid: a hard budget with a positive reserve, and a soft-only budget with no reserve.
        SpendBudgetContract.Validate(Copilot("run", SpendBudgetScopes.Run, 5m, 10m, 1m));
        SpendBudgetContract.Validate(Copilot("soft", SpendBudgetScopes.Run, 5m, null, 0m));
        // Each budget below breaks one rule; the id names the rule.
        foreach (var invalid in new[]
        {
            Copilot("soft-above-hard", SpendBudgetScopes.Run, 11m, 10m, 1m),
            Copilot("no-reserve", SpendBudgetScopes.Run, null, 10m, 0m),
            Copilot("no-limit", SpendBudgetScopes.Run, null, null, 1m),
            Copilot("negative", SpendBudgetScopes.Run, null, -1m, 1m),
            Copilot("project-without-period", SpendBudgetScopes.Project, null, 10m, 1m),
            Copilot("run-with-period", SpendBudgetScopes.Run, null, 10m, 1m) with { Period = September },
            Copilot("agent-without-id", SpendBudgetScopes.Agent, null, 10m, 1m),
            Copilot("run-with-agent", SpendBudgetScopes.Run, null, 10m, 1m) with { AgentId = "coder" },
            Copilot("bad-scope", "tenant", null, 10m, 1m),
            Copilot("zero-revision", SpendBudgetScopes.Run, null, 10m, 1m) with { Revision = 0 },
            // Period bounds must be in UTC; this start has a +02:00 offset.
            Copilot("local-period", SpendBudgetScopes.Project, null, 10m, 1m) with
            {
                Period = September with { StartsAt = new(2026, 9, 1, 0, 0, 0, TimeSpan.FromHours(2)) }
            }
        })
            Assert.Throws<ArgumentException>(() => SpendBudgetContract.Validate(invalid));
    }

    [Fact]
    public void ContractSetRejectsDuplicateIdsAndBucketsButKeepsUnitsApart()
    {
        // Same scope but different meter and unit: two separate buckets, so the set is valid.
        SpendBudgetContract.ValidateSet(
        [
            Copilot("copilot", SpendBudgetScopes.Run, null, 10m, 1m),
            Byok("azure", SpendBudgetScopes.Run, null, 10m, 1m)
        ]);
        // Two budgets can't share an id, even in different buckets.
        Assert.Throws<ArgumentException>(() => SpendBudgetContract.ValidateSet(
        [
            Copilot("same", SpendBudgetScopes.Run, null, 10m, 1m),
            Byok("same", SpendBudgetScopes.Run, null, 10m, 1m)
        ]));
        // Two budgets can't limit the same bucket (same scope, meter, and unit), even with different ids.
        Assert.Throws<ArgumentException>(() => SpendBudgetContract.ValidateSet(
        [
            Copilot("first", SpendBudgetScopes.Run, null, 10m, 1m),
            Copilot("second", SpendBudgetScopes.Run, null, 20m, 1m)
        ]));
    }

    [Fact]
    public void NarrowingKeepsTheBucketAndCannotRaiseOrRemoveLimits()
    {
        var ceiling = Copilot("run", SpendBudgetScopes.Run, 5m, 10m, 1m);
        // Lower limits and a larger reserve are stricter, so they are allowed.
        SpendBudgetContract.RequireNarrowing(ceiling, ceiling with { SoftLimit = 2m, HardLimit = 4m, ReservePerDispatch = 2m });
        // Raising the hard limit, removing the soft limit, shrinking the reserve, or changing the unit is rejected.
        Assert.Throws<ArgumentException>(() => SpendBudgetContract.RequireNarrowing(ceiling, ceiling with { HardLimit = 11m }));
        Assert.Throws<ArgumentException>(() => SpendBudgetContract.RequireNarrowing(ceiling, ceiling with { SoftLimit = null }));
        Assert.Throws<ArgumentException>(() => SpendBudgetContract.RequireNarrowing(ceiling, ceiling with { ReservePerDispatch = 0.5m }));
        Assert.Throws<ArgumentException>(() => SpendBudgetContract.RequireNarrowing(ceiling, ceiling with { Unit = "USD" }));
    }

    [Fact]
    public void SelectionAppliesRunBudgetsMatchingAgentsAndCurrentProjectPeriodsForTheMeter()
    {
        // Pinned run budgets: one run budget, two agent budgets, and one BYOK budget on another meter.
        // Project budgets at configuration revision 7: September (covers Now) and October (does not).
        var context = new SpendBudgetAdmissionContext(
        [
            Copilot("run", SpendBudgetScopes.Run, null, 10m, 1m),
            Copilot("coder", SpendBudgetScopes.Agent, null, 5m, 1m) with { AgentId = "coder" },
            Copilot("reviewer", SpendBudgetScopes.Agent, null, 5m, 1m) with { AgentId = "reviewer" },
            Byok("azure-run", SpendBudgetScopes.Run, null, 3m, 0.5m)
        ],
        new("project-1", 7, 2,
        [
            Copilot("sep", SpendBudgetScopes.Project, null, 100m, 1m) with { Period = September },
            Copilot("oct", SpendBudgetScopes.Project, null, 100m, 1m) with { Period = October }
        ]));

        // Select for agent "coder" on the Copilot meter, with the run pinned at configuration revision 3.
        var selected = SpendBudgetAdmissionMath.SelectApplicable(
            context, "run-1", "coder", SdkMeterSources.CopilotNanoAiu, 3, Now);

        // Selected: the run budget, the "coder" agent budget, and the September project budget.
        // Not selected: the "reviewer" agent budget, the BYOK budget, and the October period.
        Assert.Equal(["coder", "run", "sep"], selected.Select(item => item.Budget.BudgetId).Order());
        // Project budgets carry the current project revision; pinned budgets carry the run's pinned revision.
        Assert.Equal(7, selected.Single(item => item.Budget.BudgetId == "sep").ProjectConfigurationRevision);
        Assert.Equal(3, selected.Single(item => item.Budget.BudgetId == "run").ProjectConfigurationRevision);
        // Run buckets include the run ID; project buckets are shared by all runs.
        Assert.Contains("run-1", selected.Single(item => item.Budget.BudgetId == "run").BucketKey);
        Assert.DoesNotContain("run-1", selected.Single(item => item.Budget.BudgetId == "sep").BucketKey);
        // Results are sorted by bucket key, so concurrent admissions take bucket locks in the same order.
        Assert.Equal(selected.Select(item => item.BucketKey).Order(StringComparer.Ordinal),
            selected.Select(item => item.BucketKey));

        // On the BYOK meter only the BYOK run budget applies.
        var azure = SpendBudgetAdmissionMath.SelectApplicable(
            context, "run-1", "coder", SdkMeterSources.ByokTokens, 3, Now);
        Assert.Equal("azure-run", Assert.Single(azure).Budget.BudgetId);
    }

    [Fact]
    public void HardProjectBudgetWithoutACoveringPeriodBlocksWhileSoftOnlyDoesNot()
    {
        // Now is in September, but the only hard project budget covers October, so admission throws.
        var hard = new SpendBudgetAdmissionContext([],
            new("project-1", 1, 1, [Copilot("oct", SpendBudgetScopes.Project, null, 100m, 1m) with { Period = October }]));
        Assert.Equal(SpendBudgetAdmissionMath.PeriodUncovered,
            Assert.Throws<RuntimeAuthorizationException>(() => SpendBudgetAdmissionMath.SelectApplicable(
                hard, "run-1", "coder", SdkMeterSources.CopilotNanoAiu, 1, Now)).Code);

        // A soft-only budget for an uncovered period is skipped instead of blocking.
        var soft = new SpendBudgetAdmissionContext([],
            new("project-1", 1, 1, [Copilot("oct", SpendBudgetScopes.Project, 10m, null, 0m) with { Period = October }]));
        Assert.Empty(SpendBudgetAdmissionMath.SelectApplicable(
            soft, "run-1", "coder", SdkMeterSources.CopilotNanoAiu, 1, Now));
    }

    [Fact]
    public void AccountingLagCounterexampleStaysWithinTheHardLimitBecauseReservesCountAsExposure()
    {
        // Hard limit 10, reserve 4. Decide adds committed + outstanding + reserve and compares it to 10.
        var budget = Copilot("run", SpendBudgetScopes.Run, null, 10m, 4m);
        var nothingAccounted = new UsageSpendTotal(0m, 0, 0);

        // Without exposure, both of these would be admitted while Events still reports 0.
        // First dispatch: 0 + 0 + 4 = 4 <= 10. Second: 0 + 4 + 4 = 8 <= 10.
        Assert.True(SpendBudgetAdmissionMath.Decide(budget, nothingAccounted, 0m).Admitted);
        Assert.True(SpendBudgetAdmissionMath.Decide(budget, nothingAccounted, 4m).Admitted);
        // Third: 0 + 8 + 4 = 12 > 10, so the outstanding reserves alone deny it.
        Assert.Equal(SpendBudgetAdmissionMath.Exhausted,
            SpendBudgetAdmissionMath.Decide(budget, nothingAccounted, 8m).DenialCode);
        // Committed spend alone: 7 + 0 + 4 = 11 > 10 is denied; 6 + 0 + 4 = 10 is exactly at the limit and admitted.
        Assert.Equal(SpendBudgetAdmissionMath.Exhausted,
            SpendBudgetAdmissionMath.Decide(budget, new(7m, 2, 0), 0m).DenialCode);
        Assert.True(SpendBudgetAdmissionMath.Decide(budget, new(6m, 2, 0), 0m).Admitted);
    }

    [Fact]
    public void AzureByokBudgetUsesCurrencyAndBlocksUnpricedUsage()
    {
        // USD budget: soft limit 1, hard limit 2, reserve 0.25.
        var budget = Byok("azure", SpendBudgetScopes.Run, 1m, 2m, 0.25m);
        // 0.5 committed + 0.5 outstanding + 0.25 reserve = 1.25 <= 2; 0.5 committed is below the soft limit 1.
        var below = SpendBudgetAdmissionMath.Decide(budget, new(0.5m, 3, 0), 0.5m);
        Assert.True(below.Admitted);
        Assert.False(below.SoftLimitReached);

        // 1 committed reaches the soft limit 1 but still fits: 1 + 0 + 0.25 = 1.25 <= 2.
        var soft = SpendBudgetAdmissionMath.Decide(budget, new(1m, 3, 0), 0m);
        Assert.True(soft.Admitted);
        Assert.True(soft.SoftLimitReached);

        // One unpriced event makes the true spend unknown, so the hard budget denies.
        Assert.Equal(SpendBudgetAdmissionMath.PricingMissing,
            SpendBudgetAdmissionMath.Decide(budget, new(0m, 0, 1), 0m).DenialCode);
        // 1.5 committed + 0.5 outstanding + 0.25 reserve = 2.25 > 2.
        Assert.Equal(SpendBudgetAdmissionMath.Exhausted,
            SpendBudgetAdmissionMath.Decide(budget, new(1.5m, 3, 0), 0.5m).DenialCode);
    }

    [Fact]
    public void UnavailableOrStaleEvidenceBlocksHardBudgetsAndNeverReportsASoftLimit()
    {
        var hard = Copilot("hard", SpendBudgetScopes.Run, 1m, 10m, 1m);
        var softOnly = Copilot("soft", SpendBudgetScopes.Run, 1m, null, 0m);

        // A null committed total means Events evidence is missing or stale.
        // The hard budget denies with the matching code and never reports the soft limit.
        Assert.Equal(new(SpendBudgetAdmissionMath.EvidenceUnavailable, false),
            SpendBudgetAdmissionMath.Decide(hard, null, 0m));
        Assert.Equal(new(SpendBudgetAdmissionMath.EvidenceStale, false),
            SpendBudgetAdmissionMath.Decide(hard, null, 0m, staleEvidence: true));
        // The soft-only budget admits and reports nothing, because without evidence the soft limit is unknown.
        Assert.Equal(new(null, false), SpendBudgetAdmissionMath.Decide(softOnly, null, 0m));
        Assert.Equal(new(null, false), SpendBudgetAdmissionMath.Decide(softOnly, null, 0m, staleEvidence: true));
        // With evidence, 1 priced reaches the soft limit 1. Unpriced events don't block a soft-only budget.
        Assert.Equal(new(null, true), SpendBudgetAdmissionMath.Decide(softOnly, new(1m, 1, 3), 0m));
    }

    [Fact]
    public void CommittedTotalFollowsTheBudgetScope()
    {
        // Evidence with different totals per scope: run 2, project period 5, agent 1.
        var evidence = new UsageSpendEvidence(1, "tenant", "project", "run", SdkMeterSources.CopilotNanoAiu, "AIC",
            9, new(2m, 1, 0), new(5m, 3, 0), "2026-09", []) { Agent = new(1m, 1, 0) };
        // Each budget scope reads its own total.
        Assert.Equal(2m, SpendBudgetAdmissionMath.CommittedTotal(
            Copilot("run", SpendBudgetScopes.Run, null, 1m, 1m), evidence)!.PricedAmount);
        Assert.Equal(1m, SpendBudgetAdmissionMath.CommittedTotal(
            Copilot("agent", SpendBudgetScopes.Agent, null, 1m, 1m) with { AgentId = "coder" }, evidence)!.PricedAmount);
        Assert.Equal(5m, SpendBudgetAdmissionMath.CommittedTotal(
            Copilot("project", SpendBudgetScopes.Project, null, 1m, 1m) with { Period = September }, evidence)!.PricedAmount);
    }

    [Fact]
    public void EvidenceRequestCarriesOnlyTheScopeSpecificSelectors()
    {
        var dispatch = Guid.NewGuid().ToString("D");
        // A project budget sends its period and no agent ID, and the request passes contract validation.
        var project = SpendBudgetAdmissionMath.EvidenceRequest(
            Copilot("project", SpendBudgetScopes.Project, null, 1m, 1m) with { Period = September }, [dispatch]);
        Assert.Equal(September, project.ProjectPeriod);
        Assert.Null(project.AgentId);
        UsageSpendEvidenceContract.ValidateRequest(project);

        // An agent budget sends its agent ID and no period, in the budget's own unit.
        var agent = SpendBudgetAdmissionMath.EvidenceRequest(
            Byok("agent", SpendBudgetScopes.Agent, null, 1m, 1m) with { AgentId = "coder" }, []);
        Assert.Null(agent.ProjectPeriod);
        Assert.Equal("coder", agent.AgentId);
        Assert.Equal("USD", agent.Unit);
    }

    [Fact]
    public void PartialReceiptsNeverRetireExposure()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var scope = new UsageDispatchAccountingScope("tenant", "project", "run", SdkMeterSources.CopilotNanoAiu, "AIC", null);
        // Builds an Events witness that lists exactly the given receipt IDs.
        UsageDispatchAccountingWitness Witness(params Guid[] receipts)
        {
            var items = receipts.Select(id => new UsageDispatchSourceReceiptIdentity(id, Guid.NewGuid(), new string('a', 64)))
                .ToImmutableArray();
            var dispatch = Guid.NewGuid().ToString("D");
            return new(dispatch, scope, 1, items,
                UsageDispatchAccountingWitnessContract.ComputeReceiptDigest(dispatch, scope, items),
                0m, items.Length, 0, UsageDispatchAccountingStatus.Partial, null, null, null);
        }

        // The completed dispatch acknowledged two receipts.
        var required = ImmutableHashSet.Create(first, second);
        // Events witnesses only one of them: not accounted, so the reservation stays.
        Assert.False(SpendBudgetAdmissionMath.IsAccounted(required, Witness(first)));
        // Events witnesses both: accounted, so the reservation can retire.
        Assert.True(SpendBudgetAdmissionMath.IsAccounted(required, Witness(first, second)));
    }

    [Fact]
    public void AcceptedSelectionBudgetsAreValidatedWithTheOwnerErrorContract()
    {
        // Run limits without spendBudgets mean no budgets.
        Assert.Empty(CoordinatorWorkflowCatalog.ReadSpendBudgets(
            JsonSerializer.SerializeToElement(new { runLimits = new { } })));
        // A well-formed budget is read with all its fields, including the revision.
        var accepted = CoordinatorWorkflowCatalog.ReadSpendBudgets(JsonSerializer.SerializeToElement(new
        {
            runLimits = new
            {
                spendBudgets = new object[]
                {
                    new
                    {
                        budgetId = "run-aic", revision = 2, scope = "run", meterSource = SdkMeterSources.CopilotNanoAiu,
                        unit = "AIC", softLimit = 5m, hardLimit = 10m, reservePerDispatch = 1m
                    }
                }
            }
        }));
        Assert.Equal(Copilot("run-aic", SpendBudgetScopes.Run, 5m, 10m, 1m) with { Revision = 2 },
            Assert.Single(accepted));

        // Invalid shapes: an object instead of an array, a null entry, a hard budget with reserve 0,
        // and an unknown property. Each one is reported as an upstream contract error (502).
        foreach (var json in new[]
        {
            """{"runLimits":{"spendBudgets":{}}}""",
            """{"runLimits":{"spendBudgets":[null]}}""",
            """{"runLimits":{"spendBudgets":[{"budgetId":"x","revision":1,"scope":"run","meterSource":"m","unit":"AIC","hardLimit":1,"reservePerDispatch":0}]}}""",
            """{"runLimits":{"spendBudgets":[{"budgetId":"x","revision":1,"scope":"run","meterSource":"m","unit":"AIC","hardLimit":1,"reservePerDispatch":1,"extra":true}]}}"""
        })
        {
            using var document = JsonDocument.Parse(json);
            var failure = Assert.Throws<CoordinationException>(() =>
                CoordinatorWorkflowCatalog.ReadSpendBudgets(document.RootElement));
            Assert.Equal("projects_run_selection_contract_invalid", failure.Code);
            Assert.Equal(502, failure.StatusCode);
        }
    }

    [Fact]
    public void CurrentProjectBudgetsMustMatchTheProjectAndUseProjectScope()
    {
        var valid = new CurrentProjectSpendBudgets("project-1", 3, 1,
            [Copilot("sep", SpendBudgetScopes.Project, null, 10m, 1m) with { Period = September }]);
        ProjectsRunSelectionClient.ValidateCurrentProjectSpendBudgets(valid, "project-1");
        // Rejected: a different project, configuration revision 0, a run-scoped budget, and a hard budget with reserve 0.
        foreach (var invalid in new[]
        {
            valid with { ProjectId = "project-2" },
            valid with { ProjectConfigurationRevision = 0 },
            valid with { Budgets = [Copilot("run", SpendBudgetScopes.Run, null, 10m, 1m)] },
            valid with { Budgets = [Copilot("sep", SpendBudgetScopes.Project, null, 10m, 0m) with { Period = September }] }
        })
            Assert.Equal("projects_run_selection_contract_invalid",
                Assert.Throws<CoordinationException>(() =>
                    ProjectsRunSelectionClient.ValidateCurrentProjectSpendBudgets(invalid, "project-1")).Code);
    }
}
