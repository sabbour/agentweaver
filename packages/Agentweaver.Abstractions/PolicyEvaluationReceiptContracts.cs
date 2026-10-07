namespace Agentweaver.Abstractions;

public sealed record PolicyEvaluationReceiptReferenceRequest(Guid ReceiptId);

public sealed record PolicyEvaluationReceiptView(
    Guid ReceiptId,
    string Issuer,
    SessionIdentity Identity,
    PolicyEvaluationSessionPayload Evidence,
    DateTimeOffset CreatedAt);

public sealed record PolicyEvaluationAppendAcknowledgment(
    Guid ReceiptId,
    SessionIdentity Identity,
    long Position,
    bool IsDuplicate);

public sealed record PolicyEvaluationReceiptAdmissionAcknowledgment(
    Guid ReceiptId,
    SessionIdentity Identity);
