namespace Agentweaver.Api.Memory;

public sealed class BlueprintGenerationJobRecord
{
    public string JobId { get; set; } = "";
    public string Subject { get; set; } = "";
    public string IdempotencyKey { get; set; } = "";
    public string RequestFingerprint { get; set; } = "";
    public string Description { get; set; } = "";
    public string? ProjectId { get; set; }
    public string? TargetRepository { get; set; }
    public string? BlueprintModel { get; set; }
    public string? WorkflowModel { get; set; }
    public string ProviderKind { get; set; } = "";
    public string? ProviderType { get; set; }
    public string ProviderKey { get; set; } = "";
    public string ProviderScope { get; set; } = "";
    public string ResolutionScope { get; set; } = "";
    public string? CredentialBindingVersion { get; set; }
    public string QueuedProviderKey { get; set; } = "";
    public string Status { get; set; } = "";
    public int Attempt { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public string? FailureCode { get; set; }
    public string? FailureMessage { get; set; }
    public bool FailureRetryable { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class BlueprintGenerationArtifactRecord
{
    public string ArtifactId { get; set; } = "";
    public string JobId { get; set; } = "";
    public string LogicalId { get; set; } = "";
    public int Version { get; set; }
    public string BlueprintJson { get; set; } = "";
    public string? GeneratedWorkflowYaml { get; set; }
    public string WarningsJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; }
}
