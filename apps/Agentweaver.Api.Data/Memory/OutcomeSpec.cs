using System.ComponentModel.DataAnnotations;

namespace Agentweaver.Api.Memory;

public sealed class OutcomeSpec
{
    [Key] public int Id { get; set; }
    public required string ProjectId { get; set; }
    public required string CoordinatorRunId { get; set; }
    public required string Goal { get; set; }
    public required string DesiredOutcome { get; set; }
    public required string Scope { get; set; }
    public required string Assumptions { get; set; }
    public string? ClarifyingQuestions { get; set; }
    public required string Status { get; set; }      // drafting | awaiting_confirmation | confirmed | declined
    public string? ConfirmedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
