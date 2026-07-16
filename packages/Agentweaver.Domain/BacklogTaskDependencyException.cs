namespace Agentweaver.Domain;

public sealed class BacklogTaskDependencyException(string message) : InvalidOperationException(message);
