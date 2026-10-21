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
        SpendBudgetContract.Validate(Copilot("run", SpendBudgetScopes.Run, 5m, 10m, 1m));
        SpendBudgetContract.Validate(Copilot("soft", SpendBudgetScopes.Run, 5m, null, 0m));
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
        SpendBudgetContract.ValidateSet(
        [
            Copilot("copilot", SpendBudgetScopes.Run, null, 10m, 1m),
            Byok("azure", SpendBudgetScopes.Run, null, 10m, 1m)
        ]);
        Assert.Throws<ArgumentException>(() => SpendBudgetContract.ValidateSet(
        [
            Copilot("same", SpendBudgetScopes.Run, null, 10m, 1m),
            Byok("same", SpendBudgetScopes.Run, null, 10m, 1m)
        ]));
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
        SpendBudgetContract.RequireNarrowing(ceiling, ceiling with { SoftLimit = 2m, HardLimit = 4m, ReservePerDispatch = 2m });
        Assert.Throws<ArgumentException>(() => SpendBudgetContract.RequireNarrowing(ceiling, ceiling with { HardLimit = 11m }));
        Assert.Throws<ArgumentException>(() => SpendBudgetContract.RequireNarrowing(ceiling, ceiling with { SoftLimit = null }));
        Assert.Throws<ArgumentException>(() => SpendBudgetContract.RequireNarrowing(ceiling, ceiling with { ReservePerDispatch = 0.5m }));
        Assert.Throws<ArgumentException>(() => SpendBudgetContract.RequireNarrowing(ceiling, ceiling with { Unit = "USD" }));
    }

    [Fact]
    public void SelectionAppliesRunBudgetsMatchingAgentsAndCurrentProjectPeriodsForTheMeter()
    {
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

        var selected = SpendBudgetAdmissionMath.SelectApplicable(
            context, "run-1", "coder", SdkMeterSources.CopilotNanoAiu, 3, Now);

        Assert.Equal(["coder", "run", "sep"], selected.Select(item => item.Budget.BudgetId).Order());
        Assert.Equal(7, selected.Single(item => item.Budget.BudgetId == "sep").ProjectConfigurationRevision);
        Assert.Equal(3, selected.Single(item => item.Budget.BudgetId == "run").ProjectConfigurationRevision);
        Assert.Contains("run-1", selected.Single(item => item.Budget.BudgetId == "run").BucketKey);
        Assert.DoesNotContain("run-1", selected.Single(item => item.Budget.BudgetId == "sep").BucketKey);
        Assert.Equal(selected.Select(item => item.BucketKey).Order(StringComparer.Ordinal),
            selected.Select(item => item.BucketKey));

        var azure = SpendBudgetAdmissionMath.SelectApplicable(
            context, "run-1", "coder", SdkMeterSources.ByokTokens, 3, Now);
        Assert.Equal("azure-run", Assert.Single(azure).Budget.BudgetId);
    }

    [Fact]
    public void HardProjectBudgetWithoutACoveringPeriodBlocksWhileSoftOnlyDoesNot()
    {
        var hard = new SpendBudgetAdmissionContext([],
            new("project-1", 1, 1, [Copilot("oct", SpendBudgetScopes.Project, null, 100m, 1m) with { Period = October }]));
        Assert.Equal(SpendBudgetAdmissionMath.PeriodUncovered,
            Assert.Throws<RuntimeAuthorizationException>(() => SpendBudgetAdmissionMath.SelectApplicable(
                hard, "run-1", "coder", SdkMeterSources.CopilotNanoAiu, 1, Now)).Code);

        var soft = new SpendBudgetAdmissionContext([],
            new("project-1", 1, 1, [Copilot("oct", SpendBudgetScopes.Project, 10m, null, 0m) with { Period = October }]));
        Assert.Empty(SpendBudgetAdmissionMath.SelectApplicable(
            soft, "run-1", "coder", SdkMeterSources.CopilotNanoAiu, 1, Now));
    }

    [Fact]
    public void AccountingLagCounterexampleStaysWithinTheHardLimitBecauseReservesCountAsExposure()
    {
        var budget = Copilot("run", SpendBudgetScopes.Run, null, 10m, 4m);
        var nothingAccounted = new UsageSpendTotal(0m, 0, 0);

        // Without exposure, both of these would be admitted while Events still reports 0.
        Assert.True(SpendBudgetAdmissionMath.Decide(budget, nothingAccounted, 0m).Admitted);
        Assert.True(SpendBudgetAdmissionMath.Decide(budget, nothingAccounted, 4m).Admitted);
        Assert.Equal(SpendBudgetAdmissionMath.Exhausted,
            SpendBudgetAdmissionMath.Decide(budget, nothingAccounted, 8m).DenialCode);
        Assert.Equal(SpendBudgetAdmissionMath.Exhausted,
            SpendBudgetAdmissionMath.Decide(budget, new(7m, 2, 0), 0m).DenialCode);
        Assert.True(SpendBudgetAdmissionMath.Decide(budget, new(6m, 2, 0), 0m).Admitted);
    }

    [Fact]
    public void AzureByokBudgetUsesCurrencyAndBlocksUnpricedUsage()
    {
        var budget = Byok("azure", SpendBudgetScopes.Run, 1m, 2m, 0.25m);
        var below = SpendBudgetAdmissionMath.Decide(budget, new(0.5m, 3, 0), 0.5m);
        Assert.True(below.Admitted);
        Assert.False(below.SoftLimitReached);

        var soft = SpendBudgetAdmissionMath.Decide(budget, new(1m, 3, 0), 0m);
        Assert.True(soft.Admitted);
        Assert.True(soft.SoftLimitReached);

        Assert.Equal(SpendBudgetAdmissionMath.PricingMissing,
            SpendBudgetAdmissionMath.Decide(budget, new(0m, 0, 1), 0m).DenialCode);
        Assert.Equal(SpendBudgetAdmissionMath.Exhausted,
            SpendBudgetAdmissionMath.Decide(budget, new(1.5m, 3, 0), 0.5m).DenialCode);
    }

    [Fact]
    public void UnavailableOrStaleEvidenceBlocksHardBudgetsAndNeverReportsASoftLimit()
    {
        var hard = Copilot("hard", SpendBudgetScopes.Run, 1m, 10m, 1m);
        var softOnly = Copilot("soft", SpendBudgetScopes.Run, 1m, null, 0m);

        Assert.Equal(new(SpendBudgetAdmissionMath.EvidenceUnavailable, false),
            SpendBudgetAdmissionMath.Decide(hard, null, 0m));
        Assert.Equal(new(SpendBudgetAdmissionMath.EvidenceStale, false),
            SpendBudgetAdmissionMath.Decide(hard, null, 0m, staleEvidence: true));
        Assert.Equal(new(null, false), SpendBudgetAdmissionMath.Decide(softOnly, null, 0m));
        Assert.Equal(new(null, false), SpendBudgetAdmissionMath.Decide(softOnly, null, 0m, staleEvidence: true));
        Assert.Equal(new(null, true), SpendBudgetAdmissionMath.Decide(softOnly, new(1m, 1, 3), 0m));
    }

    [Fact]
    public void CommittedTotalFollowsTheBudgetScope()
    {
        var evidence = new UsageSpendEvidence(1, "tenant", "project", "run", SdkMeterSources.CopilotNanoAiu, "AIC",
            9, new(2m, 1, 0), new(5m, 3, 0), "2026-09", []) { Agent = new(1m, 1, 0) };
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
        var project = SpendBudgetAdmissionMath.EvidenceRequest(
            Copilot("project", SpendBudgetScopes.Project, null, 1m, 1m) with { Period = September }, [dispatch]);
        Assert.Equal(September, project.ProjectPeriod);
        Assert.Null(project.AgentId);
        UsageSpendEvidenceContract.ValidateRequest(project);

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
        UsageDispatchAccountingWitness Witness(params Guid[] receipts)
        {
            var items = receipts.Select(id => new UsageDispatchSourceReceiptIdentity(id, Guid.NewGuid(), new string('a', 64)))
                .ToImmutableArray();
            var dispatch = Guid.NewGuid().ToString("D");
            return new(dispatch, scope, 1, items,
                UsageDispatchAccountingWitnessContract.ComputeReceiptDigest(dispatch, scope, items),
                0m, items.Length, 0, UsageDispatchAccountingStatus.Partial, null, null, null);
        }

        var required = ImmutableHashSet.Create(first, second);
        Assert.False(SpendBudgetAdmissionMath.IsAccounted(required, Witness(first)));
        Assert.True(SpendBudgetAdmissionMath.IsAccounted(required, Witness(first, second)));
    }

    [Fact]
    public void AcceptedSelectionBudgetsAreValidatedWithTheOwnerErrorContract()
    {
        Assert.Empty(CoordinatorWorkflowCatalog.ReadSpendBudgets(
            JsonSerializer.SerializeToElement(new { runLimits = new { } })));
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
