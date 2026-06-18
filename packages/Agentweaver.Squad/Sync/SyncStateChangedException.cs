namespace Agentweaver.Squad.Sync;

public sealed class SyncStateChangedException : Exception
{
    public SyncStateChangedException(string message) : base(message) { }
}
