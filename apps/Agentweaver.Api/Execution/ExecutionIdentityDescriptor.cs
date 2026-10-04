using System.Security.Cryptography;
using System.Text;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;

namespace Agentweaver.Api.Execution;

public sealed record ExecutionIdentityDescriptor
{
    public const int CurrentSchemaVersion = 1;
    public const string ApiServiceIdentity = "service:agentweaver-api";

    public required string DescriptorId { get; init; }
    public required int SchemaVersion { get; init; }
    public required string RunId { get; init; }
    public required int Attempt { get; init; }
    public string? ProjectId { get; init; }
    public required string InitiatingPrincipalId { get; init; }
    public required string ExecutingServiceId { get; init; }
    public required string AgentAssignmentId { get; init; }
    public string? AgentRole { get; init; }
    public string? AgentDisplayName { get; init; }
    public string? ParentRunId { get; init; }
    public string? ParentDescriptorId { get; init; }
    public string? RetryOfRunId { get; init; }
    public string? RetryOfDescriptorId { get; init; }
    public string? WorkflowRunId { get; init; }
    public string? SubtaskId { get; init; }
    public string? ApprovalPolicySnapshotId { get; init; }
    public string? ExecutableWorkflowContentDigest { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    public static ExecutionIdentityDescriptor Create(Run run) =>
        CreateCore(
            run,
            string.IsNullOrWhiteSpace(run.ParentRunId) ? null : DescriptorIdFor(run.ParentRunId, 1),
            string.IsNullOrWhiteSpace(run.RetriedFrom) ? null : DescriptorIdFor(run.RetriedFrom, 1));

    public static ExecutionIdentityDescriptor CreateWithResolvedLineage(
        Run run,
        string? parentDescriptorId,
        string? retryOfDescriptorId) =>
        CreateCore(run, parentDescriptorId, retryOfDescriptorId);

    private static ExecutionIdentityDescriptor CreateCore(
        Run run,
        string? parentDescriptorId,
        string? retryOfDescriptorId)
    {
        ArgumentNullException.ThrowIfNull(run);
        var runId = run.Id.ToString();
        var attempt = run.LifecycleGeneration;
        var agentRole = string.IsNullOrWhiteSpace(run.AgentName) ? null : run.AgentName.Trim();
        var assignmentId = StableId(
            "assignment",
            run.ProjectId?.ToString(),
            run.ParentRunId,
            run.SubtaskId,
            agentRole ?? "unassigned");

        return new ExecutionIdentityDescriptor
        {
            DescriptorId = DescriptorIdFor(runId, attempt),
            SchemaVersion = CurrentSchemaVersion,
            RunId = runId,
            Attempt = attempt,
            ProjectId = run.ProjectId?.ToString(),
            InitiatingPrincipalId = run.SubmittingUser,
            ExecutingServiceId = ApiServiceIdentity,
            AgentAssignmentId = assignmentId,
            AgentRole = agentRole,
            AgentDisplayName = agentRole,
            ParentRunId = run.ParentRunId,
            ParentDescriptorId = parentDescriptorId,
            RetryOfRunId = run.RetriedFrom,
            RetryOfDescriptorId = retryOfDescriptorId,
            WorkflowRunId = run.WorkflowRunId,
            SubtaskId = run.SubtaskId,
            ApprovalPolicySnapshotId = run.ApprovalPolicySnapshotId,
            ExecutableWorkflowContentDigest = run.ExecutableWorkflowContentDigest,
            CreatedAt = attempt == 1 && run.StartedAt != default
                ? run.StartedAt
                : DateTimeOffset.UtcNow,
        };
    }

    public ExecutionIdentityRecord ToRecord() => new()
    {
        DescriptorId = DescriptorId,
        SchemaVersion = SchemaVersion,
        RunId = RunId,
        Attempt = Attempt,
        ProjectId = ProjectId,
        InitiatingPrincipalId = InitiatingPrincipalId,
        ExecutingServiceId = ExecutingServiceId,
        AgentAssignmentId = AgentAssignmentId,
        AgentRole = AgentRole,
        AgentDisplayName = AgentDisplayName,
        ParentRunId = ParentRunId,
        ParentDescriptorId = ParentDescriptorId,
        RetryOfRunId = RetryOfRunId,
        RetryOfDescriptorId = RetryOfDescriptorId,
        WorkflowRunId = WorkflowRunId,
        SubtaskId = SubtaskId,
        ApprovalPolicySnapshotId = ApprovalPolicySnapshotId,
        ExecutableWorkflowContentDigest = ExecutableWorkflowContentDigest,
        CreatedAt = CreatedAt,
    };

    public static ExecutionIdentityDescriptor FromRecord(ExecutionIdentityRecord record) => new()
    {
        DescriptorId = record.DescriptorId,
        SchemaVersion = record.SchemaVersion,
        RunId = record.RunId,
        Attempt = record.Attempt,
        ProjectId = record.ProjectId,
        InitiatingPrincipalId = record.InitiatingPrincipalId,
        ExecutingServiceId = record.ExecutingServiceId,
        AgentAssignmentId = record.AgentAssignmentId,
        AgentRole = record.AgentRole,
        AgentDisplayName = record.AgentDisplayName,
        ParentRunId = record.ParentRunId,
        ParentDescriptorId = record.ParentDescriptorId,
        RetryOfRunId = record.RetryOfRunId,
        RetryOfDescriptorId = record.RetryOfDescriptorId,
        WorkflowRunId = record.WorkflowRunId,
        SubtaskId = record.SubtaskId,
        ApprovalPolicySnapshotId = record.ApprovalPolicySnapshotId,
        ExecutableWorkflowContentDigest = record.ExecutableWorkflowContentDigest,
        CreatedAt = record.CreatedAt,
    };

    public static string DescriptorIdFor(string runId, int attempt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        return StableId("execution", runId, attempt.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static string StableId(string prefix, params string?[] values)
    {
        var canonical = string.Join("\n", values.Select(value => value ?? ""));
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
        return $"{prefix}-{digest}";
    }
}
