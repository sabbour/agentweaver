namespace Agentweaver.Api.Infrastructure;

/// <summary>
/// Raised when a caller-supplied RunEvent sequence collides with an existing durable row whose
/// payload/type does not match. This is a data-integrity failure and must never be treated as
/// idempotent success.
/// </summary>
public sealed class RunEventSequenceCollisionException(string message) : InvalidOperationException(message);
