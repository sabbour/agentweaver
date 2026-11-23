using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Agentweaver.Abstractions;

namespace Agentweaver.Environment;

/// <summary>Produces bounded, exact-catalog scan evidence without network or model calls.</summary>
internal static class RemoteMcpCatalogScanner
{
    private static readonly string[] PoisonedPhrases =
    [
        "ignore previous instructions",
        "ignore all instructions",
        "system prompt",
        "developer message",
        "do not follow",
        "exfiltrate",
        "send secrets",
        "bypass policy"
    ];

    /// <summary>Scans every tool in the candidate catalog and compares it with accepted and peer catalogs.</summary>
    public static RemoteMcpCatalogScanEvidence Scan(
        RemoteMcpParsedToolCatalog candidate,
        long configurationRevision,
        long discoveryRevision,
        RemoteMcpDiscoveryCatalogPin? acceptedBaseline,
        IEnumerable<(Guid ConnectionId, ImmutableArray<RemoteMcpToolSchemaPin> Tools)> peerCatalogs,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(peerCatalogs);
        ArgumentNullException.ThrowIfNull(timeProvider);
        var poisoned = FindPoisoned(candidate.Tools);
        var hidden = FindHidden(candidate.Tools);
        var lookalike = FindLookalike(candidate.Tools);
        var collisions = FindCrossServerCollisions(candidate.Tools, peerCatalogs);
        var drift = acceptedBaseline is null
            ? new RemoteMcpScanCheckEvidence(
                RemoteMcpCatalogScanEvidence.BaselineDriftCheck,
                RemoteMcpScanOutcome.Unknown,
                null,
                "remote_mcp_accepted_baseline_absent")
            : CompareBaseline(candidate.Tools, acceptedBaseline.Tools);
        return new(
            RemoteMcpCatalogScanEvidence.CurrentVersion,
            RemoteMcpCatalogScanEvidence.CurrentScannerId,
            RemoteMcpCatalogScanEvidence.CurrentScannerVersion,
            configurationRevision,
            discoveryRevision,
            candidate.CatalogSha256,
            acceptedBaseline?.DiscoveryRevision,
            acceptedBaseline?.CatalogSha256,
            timeProvider.GetUtcNow().ToUniversalTime(),
            RemoteMcpScanState.Completed,
            [poisoned, hidden, lookalike, collisions, drift]);
    }

    private static RemoteMcpScanCheckEvidence FindPoisoned(
        ImmutableArray<RemoteMcpToolSchemaPin> tools)
    {
        var count = tools.Count(tool =>
        {
            var description = tool.Description ?? string.Empty;
            return PoisonedPhrases.Any(phrase =>
                description.Contains(phrase, StringComparison.OrdinalIgnoreCase));
        });
        return count == 0
            ? Pass(RemoteMcpCatalogScanEvidence.PoisonedDescriptionCheck)
            : Fail(RemoteMcpCatalogScanEvidence.PoisonedDescriptionCheck, 100,
                "remote_mcp_poisoned_description_detected");
    }

    private static RemoteMcpScanCheckEvidence FindHidden(
        ImmutableArray<RemoteMcpToolSchemaPin> tools)
    {
        var count = tools.Count(tool => ContainsHiddenUnicode(tool.Name) ||
            ContainsHiddenUnicode(tool.Description) ||
            ContainsHiddenUnicode(tool.SchemaJson));
        return count == 0
            ? Pass(RemoteMcpCatalogScanEvidence.HiddenUnicodeCheck)
            : Fail(RemoteMcpCatalogScanEvidence.HiddenUnicodeCheck, 100,
                "remote_mcp_hidden_unicode_detected");
    }

    private static RemoteMcpScanCheckEvidence FindLookalike(
        ImmutableArray<RemoteMcpToolSchemaPin> tools)
    {
        var normalized = tools
            .Select(tool => (tool, Name: NormalizeName(tool.Name)))
            .GroupBy(item => item.Name, StringComparer.Ordinal)
            .Any(group => group.Select(item => item.tool.Name).Distinct(StringComparer.Ordinal).Count() > 1);
        return normalized
            ? Fail(RemoteMcpCatalogScanEvidence.LookalikeNameCheck, 100,
                "remote_mcp_lookalike_tool_name_detected")
            : Pass(RemoteMcpCatalogScanEvidence.LookalikeNameCheck);
    }

    private static RemoteMcpScanCheckEvidence FindCrossServerCollisions(
        ImmutableArray<RemoteMcpToolSchemaPin> tools,
        IEnumerable<(Guid ConnectionId, ImmutableArray<RemoteMcpToolSchemaPin> Tools)> peers)
        => HasCrossServerCollision(tools, peers)
            ? Fail(RemoteMcpCatalogScanEvidence.CrossServerCollisionCheck, 100,
                "remote_mcp_cross_server_tool_collision")
            : Pass(RemoteMcpCatalogScanEvidence.CrossServerCollisionCheck);

    public static bool HasCrossServerCollision(
        ImmutableArray<RemoteMcpToolSchemaPin> tools,
        IEnumerable<(Guid ConnectionId, ImmutableArray<RemoteMcpToolSchemaPin> Tools)> peers)
    {
        var names = tools.Select(tool => NormalizeName(tool.Name)).ToHashSet(StringComparer.Ordinal);
        return peers.Any(peer => peer.Tools.Any(tool => names.Contains(NormalizeName(tool.Name))));
    }

    private static RemoteMcpScanCheckEvidence CompareBaseline(
        ImmutableArray<RemoteMcpToolSchemaPin> candidate,
        ImmutableArray<RemoteMcpToolSchemaPin> baseline)
    {
        var before = baseline.ToDictionary(tool => tool.Name, StringComparer.Ordinal);
        var after = candidate.ToDictionary(tool => tool.Name, StringComparer.Ordinal);
        var changed = before.Keys.Union(after.Keys, StringComparer.Ordinal)
            .Count(name =>
                !before.TryGetValue(name, out var oldTool) ||
                !after.TryGetValue(name, out var newTool) ||
                oldTool.ToolRevision != newTool.ToolRevision);
        if (changed == 0)
            return Pass(RemoteMcpCatalogScanEvidence.BaselineDriftCheck);
        var total = Math.Max(before.Count, after.Count);
        var score = Math.Min(100, (int)Math.Ceiling(changed * 100d / Math.Max(1, total)));
        return Fail(
            RemoteMcpCatalogScanEvidence.BaselineDriftCheck,
            score,
            "remote_mcp_catalog_drift_detected");
    }

    private static bool ContainsHiddenUnicode(string? value)
    {
        if (value is null)
            return false;
        foreach (var rune in value.EnumerateRunes())
        {
            var scalar = rune.Value;
            if (UnicodeCategoryOf(rune) is UnicodeCategory.Control or UnicodeCategory.Format &&
                    scalar is not '\n' and not '\r' and not '\t' ||
                scalar is >= 0xE000 and <= 0xF8FF or >= 0x1D173 and <= 0x1D17A or 0x202A or 0x202B or
                    0x202C or 0x202D or 0x202E or 0x2066 or 0x2067 or 0x2068 or 0x2069)
                return true;
        }
        return false;
    }

    private static UnicodeCategory UnicodeCategoryOf(Rune rune)
    {
        Span<char> chars = stackalloc char[2];
        var count = rune.EncodeToUtf16(chars);
        return CharUnicodeInfo.GetUnicodeCategory(new string(chars[..count]), 0);
    }

    private static string NormalizeName(string name) =>
        new string(name.Normalize(NormalizationForm.FormKC).ToUpperInvariant()
            .Where(character => char.IsLetterOrDigit(character))
            .Select(FoldConfusable)
            .ToArray());

    private static char FoldConfusable(char value) => value switch
    {
        '0' or 'Ο' or 'О' => 'O',
        '1' or 'Ι' or 'І' => 'I',
        'Α' or 'А' => 'A',
        'Β' or 'В' => 'B',
        'Ε' or 'Е' => 'E',
        'Κ' or 'К' => 'K',
        'Μ' or 'М' => 'M',
        'Ρ' or 'Р' => 'P',
        'Τ' or 'Т' => 'T',
        'Χ' or 'Х' => 'X',
        _ => value
    };

    private static RemoteMcpScanCheckEvidence Pass(string checkId) =>
        new(checkId, RemoteMcpScanOutcome.Pass, null, null);

    private static RemoteMcpScanCheckEvidence Fail(string checkId, int score, string reason) =>
        new(checkId, RemoteMcpScanOutcome.Fail, score, reason);
}
