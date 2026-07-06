namespace Agentweaver.Domain.Skills;

/// <summary>Identifies a catalog skill within a project.</summary>
public readonly record struct SkillId(Guid Value)
{
    public static SkillId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
    public static SkillId Parse(string s) => new(Guid.Parse(s));
    public static bool TryParse(string? s, out SkillId id)
    {
        if (Guid.TryParse(s, out var g)) { id = new(g); return true; }
        id = default; return false;
    }
}
