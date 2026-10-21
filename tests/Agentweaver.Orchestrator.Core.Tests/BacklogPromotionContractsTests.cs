using Agentweaver.Orchestrator.Core;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class BacklogPromotionContractsTests
{
    [Fact]
    public void PromotedTaskIdsAreStableAndScopedToProjectRunPlanAndItem()
    {
        var id = BacklogPlanPromotionIds.CreateTaskId("project", "run", "plan", "item");

        Assert.Matches("^plan-[0-9a-f]{32}$", id);
        Assert.Equal(id, BacklogPlanPromotionIds.CreateTaskId("project", "run", "plan", "item"));
        Assert.NotEqual(id, BacklogPlanPromotionIds.CreateTaskId("project", "run-2", "plan", "item"));
        Assert.NotEqual(id, BacklogPlanPromotionIds.CreateTaskId("project", "run", "plan", "item-2"));
        Assert.NotEqual(
            BacklogPlanPromotionIds.CreateTaskId("a\nb", "c", "d", "e"),
            BacklogPlanPromotionIds.CreateTaskId("a", "b\nc", "d", "e"));
    }
}
