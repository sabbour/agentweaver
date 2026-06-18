namespace Agentweaver.Domain;

public sealed record RunEvent(int Sequence, string Type, object Payload);
