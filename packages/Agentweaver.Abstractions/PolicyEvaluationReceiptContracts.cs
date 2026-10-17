namespace Agentweaver.Abstractions;

/// <summary>Identifies the policy-evaluation receipt requested by a consumer.</summary>
public sealed record PolicyEvaluationReceiptReferenceRequest(Guid ReceiptId);

/// <summary>Session-owner view of an appended policy evaluation and its evidence.</summary>
public sealed record PolicyEvaluationReceiptView(
    Guid ReceiptId,
    string Issuer,
    SessionIdentity Identity,
    PolicyEvaluationSessionPayload Evidence,
    DateTimeOffset CreatedAt);

/// <summary>Acknowledges append of a policy evaluation, including its journal position and replay status.</summary>
public sealed record PolicyEvaluationAppendAcknowledgment(
    Guid ReceiptId,
    SessionIdentity Identity,
    long Position,
    bool IsDuplicate);

/// <summary>Acknowledges that a receipt identity and session identity were admitted by the owner.</summary>
public sealed record PolicyEvaluationReceiptAdmissionAcknowledgment(
    Guid ReceiptId,
    SessionIdentity Identity);
