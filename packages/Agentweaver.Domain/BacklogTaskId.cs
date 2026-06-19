namespace Agentweaver.Domain;

public readonly record struct BacklogTaskId(Guid Value)
{
    public static BacklogTaskId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
    public static BacklogTaskId Parse(string s) => new(Guid.Parse(s));
    public static bool TryParse(string? s, out BacklogTaskId id)
    {
        if (Guid.TryParse(s, out var g)) { id = new(g); return true; }
        id = default; return false;
    }
}
