namespace Agentweaver.Api.Memory;

/// <summary>
/// Database-owned, immutable pointer to a run's private provider snapshot secret.
/// The secret reference is deliberately opaque and does not expose provider identity or credentials.
/// </summary>
public sealed class RunModelProviderSnapshotOwner
{
    public string RunId { get; set; } = "";
    public string SecretReference { get; set; } = "";
    public DateTimeOffset CapturedAt { get; set; }
}
