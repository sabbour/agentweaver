namespace Agentweaver.Domain;

/// <summary>
/// Strongly-typed run identifier. Backed by a UUID v4 so identifiers are
/// unguessable and globally unique.
/// </summary>
public readonly record struct RunId(Guid Value)
{
    public static RunId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");

    public static RunId Parse(string s) => new(Guid.Parse(s));

    public static bool TryParse(string? s, out RunId id)
    {
        if (Guid.TryParse(s, out var guid))
        {
            id = new RunId(guid);
            return true;
        }

        id = default;
        return false;
    }
}
