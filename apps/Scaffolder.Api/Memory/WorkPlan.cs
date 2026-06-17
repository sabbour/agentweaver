using System.ComponentModel.DataAnnotations;

namespace Scaffolder.Api.Memory;

public sealed class WorkPlan
{
    [Key] public int Id { get; set; }
    public int OutcomeSpecId { get; set; }
    public required string ProjectId { get; set; }
    public required string CoordinatorRunId { get; set; }
    public string? IsolationSummary { get; set; }
    public string? IntegrationBranch { get; set; }
    public required string Status { get; set; }      // planned | dispatching | assembling | in_review | complete
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
