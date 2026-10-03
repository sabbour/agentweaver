namespace Agentweaver.Identity.Broker;

/// <summary>
/// A locally persisted identity, linked to an external federated principal by the
/// normalized (issuer, subject) pair. The broker never stores upstream credentials or
/// tokens; only enough profile data to issue local tokens and evaluate consent.
/// </summary>
public sealed class BrokerUser
{
    public Guid Id { get; set; }

    public required string Issuer { get; set; }

    public required string Subject { get; set; }

    public string? DisplayName { get; set; }

    public string? Email { get; set; }

    public required DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// A disabled user can no longer obtain or refresh tokens, even with an existing
    /// permanent authorization. Re-checked at every token exchange.
    /// </summary>
    public bool Disabled { get; set; }
}
