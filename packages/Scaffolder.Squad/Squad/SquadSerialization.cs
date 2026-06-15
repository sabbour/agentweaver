using System.Text.Json;
using System.Text.Json.Serialization;
using Scaffolder.Squad.Model;

namespace Scaffolder.Squad.Squad;

/// <summary>
/// Shared JSON options and event-sidecar rebuild logic for the canonical casting layout.
/// </summary>
internal static class SquadSerialization
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions LineOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string SerializeLine(object value)
        => JsonSerializer.Serialize(value, value.GetType(), LineOptions);

    /// <summary>
    /// Rebuilds the registry from append-only event lines. Latest event per agent name wins.
    /// </summary>
    public static CastingRegistry RebuildRegistry(IEnumerable<string> eventLines)
    {
        var agents = new Dictionary<string, RegistryMember>(StringComparer.Ordinal);
        foreach (var line in eventLines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            RegistryMember? member;
            try
            {
                member = JsonSerializer.Deserialize<RegistryMember>(line, LineOptions);
            }
            catch (JsonException)
            {
                continue;
            }
            if (member is null) continue;
            agents[member.Name] = member;
        }
        return new CastingRegistry(agents);
    }

    /// <summary>
    /// Rebuilds history from append-only snapshot event lines.
    /// </summary>
    public static CastHistory RebuildHistory(IEnumerable<string> eventLines)
    {
        var snapshots = new List<CastSnapshot>();
        foreach (var line in eventLines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            CastSnapshot? snapshot;
            try
            {
                snapshot = JsonSerializer.Deserialize<CastSnapshot>(line, LineOptions);
            }
            catch (JsonException)
            {
                continue;
            }
            if (snapshot is not null) snapshots.Add(snapshot);
        }

        var ordered = snapshots.OrderBy(s => s.CreatedAt).ToList();
        var usage = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in ordered)
        {
            if (!string.IsNullOrEmpty(s.Universe) && seen.Add(s.Universe))
                usage.Add(s.Universe);
        }

        return new CastHistory(snapshots, usage);
    }
}
