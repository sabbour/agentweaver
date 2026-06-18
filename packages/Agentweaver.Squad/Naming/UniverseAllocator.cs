using Agentweaver.Squad.Model;

namespace Agentweaver.Squad.Naming;

/// <summary>
/// Pure, deterministic allocator of universe and member names. No I/O.
/// </summary>
public sealed class UniverseAllocator
{
    private readonly CastingPolicy _policy;

    public UniverseAllocator(CastingPolicy policy)
    {
        _policy = policy;
    }

    /// <summary>
    /// Proposes a universe not yet present in usage history, falling back to the
    /// first allowed universe when all allowed universes have been used. When the
    /// usage history is empty and a <paramref name="seedHint"/> is provided, a stable
    /// hash of the seed selects a varied universe so fresh projects differ from one another.
    /// </summary>
    public string ProposeUniverse(IReadOnlyList<string> usageHistory, string? seedHint = null)
    {
        var allowed = _policy.AllowlistUniverses;
        if (allowed.Count == 0)
            throw new InvalidOperationException("Casting policy has no allowed universes.");

        // If history is empty and we have a seed, use it for diversity.
        if (usageHistory.Count == 0 && seedHint is not null)
        {
            var hash = StableHash(seedHint);
            return allowed[hash % allowed.Count];
        }

        var used = new HashSet<string>(usageHistory, StringComparer.OrdinalIgnoreCase);
        foreach (var universe in allowed)
        {
            if (!used.Contains(universe)) return universe;
        }
        return allowed[0];
    }

    private static int StableHash(string s)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(s);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Math.Abs(BitConverter.ToInt32(hash, 0));
    }

    public bool IsValidUniverse(string universe)
        => _policy.AllowlistUniverses.Any(u => string.Equals(u, universe, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Allocates names for a new cast. Pulls from the universe pool in order, skipping
    /// reserved names. Overflow members receive generic names ("member-1", ...).
    /// </summary>
    public IReadOnlyList<(string Name, bool IsNamed)> AllocateNames(
        string universe,
        IReadOnlySet<string> reservedNames,
        int memberCount)
    {
        if (memberCount < 0)
            throw new ArgumentOutOfRangeException(nameof(memberCount));

        var result = new List<(string Name, bool IsNamed)>(memberCount);
        var taken = new HashSet<string>(reservedNames, StringComparer.OrdinalIgnoreCase);
        var pool = ResolvePool(universe);

        var poolIndex = 0;
        var genericIndex = 1;
        for (var i = 0; i < memberCount; i++)
        {
            string? named = null;
            while (poolIndex < pool.Count)
            {
                var candidate = pool[poolIndex++];
                if (!taken.Contains(candidate))
                {
                    named = candidate;
                    break;
                }
            }

            if (named is not null)
            {
                taken.Add(named);
                result.Add((named, true));
            }
            else
            {
                string generic;
                do
                {
                    generic = $"member-{genericIndex++}";
                } while (taken.Contains(generic));
                taken.Add(generic);
                result.Add((generic, false));
            }
        }

        return result;
    }

    /// <summary>
    /// Allocates a single name for adding to an existing team, respecting the existing universe.
    /// </summary>
    public (string Name, bool IsNamed) AllocateOne(
        string universe,
        IReadOnlySet<string> reservedNames)
        => AllocateNames(universe, reservedNames, 1)[0];

    private static IReadOnlyList<string> ResolvePool(string universe)
        => UniversePools.Pools.TryGetValue(universe, out var pool) ? pool : [];
}
