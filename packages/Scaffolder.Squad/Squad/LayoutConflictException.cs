namespace Scaffolder.Squad.Squad;

public sealed class LayoutConflictException : Exception
{
    public LayoutConflictException(string message) : base(message) { }
}
