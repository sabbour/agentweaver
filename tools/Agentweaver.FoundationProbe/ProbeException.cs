namespace Agentweaver.FoundationProbe;

/// <summary>Probe failure carrying the stable code printed by the command-line entry point.</summary>
internal sealed class ProbeException : Exception
{
    /// <summary>Creates a coded probe failure and optionally retains its underlying exception.</summary>
    public ProbeException(string code, Exception? innerException = null)
        : base(code, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}
