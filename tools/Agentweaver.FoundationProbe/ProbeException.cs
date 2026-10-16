namespace Agentweaver.FoundationProbe;

internal sealed class ProbeException : Exception
{
    public ProbeException(string code, Exception? innerException = null)
        : base(code, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}
