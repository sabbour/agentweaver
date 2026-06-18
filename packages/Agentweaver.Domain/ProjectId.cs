namespace Agentweaver.Domain;

public readonly record struct ProjectId(Guid Value)
{
    public static ProjectId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
    public static ProjectId Parse(string s) => new(Guid.Parse(s));
    public static bool TryParse(string? s, out ProjectId id)
    {
        if (Guid.TryParse(s, out var g)) { id = new(g); return true; }
        id = default; return false;
    }
}
