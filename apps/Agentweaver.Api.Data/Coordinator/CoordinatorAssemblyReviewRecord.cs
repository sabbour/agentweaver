using System.ComponentModel.DataAnnotations;

namespace Agentweaver.Api.Coordinator;

/// <summary>
/// Durable state for the collective assembly review gate.
/// Persists the review request context plus any submitted decision so restart recovery can
/// resume from <c>in_review</c> without losing the human's approval.
/// </summary>
public sealed class CoordinatorAssemblyReviewRecord
{
    [Key] public int Id { get; set; }

    public required string CoordinatorRunId { get; set; }
    public string? OwnerUser { get; set; }
    public string? IntegrationBranch { get; set; }
    public string? AggregateTreeHash { get; set; }
    public string? DecisionJson { get; set; }
    public string? Reviewer { get; set; }
    public DateTimeOffset? DecisionSubmittedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
