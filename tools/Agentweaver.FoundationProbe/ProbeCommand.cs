namespace Agentweaver.FoundationProbe;

/// <summary>Validated command-line choice for planning, help, or target-bound execution.</summary>
internal abstract record ProbeCommand
{
    private ProbeCommand() { }

    internal sealed record Plan : ProbeCommand;
    internal sealed record Help : ProbeCommand;
    internal sealed record Execute(string TargetPath) : ProbeCommand;

    /// <summary>Accepts only the exact supported argument forms and rejects all other invocations.</summary>
    public static ProbeCommand Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0 || args is ["--plan"])
            return new Plan();
        if (args is ["--help"])
            return new Help();
        if (args is ["--execute", "--target", var target] && !string.IsNullOrWhiteSpace(target))
            return new Execute(target);
        throw new ProbeException("usage");
    }
}
