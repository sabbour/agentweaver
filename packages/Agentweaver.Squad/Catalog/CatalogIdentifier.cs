using System.Text.RegularExpressions;

namespace Agentweaver.Squad.Catalog;

/// <summary>Portable identifiers used by embedded catalog assets and runtime-selected catalog entries.</summary>
public static partial class CatalogIdentifier
{
    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "con", "prn", "aux", "nul", "clock$",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };

    /// <summary>
    /// Returns whether an id can safely be used as a catalog resource segment and as a portable file
    /// stem. It intentionally accepts only lowercase ASCII kebab-case, so paths, controls, Unicode
    /// confusables, and platform-specific spellings all fail closed.
    /// </summary>
    public static bool IsSafe(string? value) =>
        value is { Length: > 0 and <= 64 } &&
        KebabCase().IsMatch(value) &&
        !WindowsReservedNames.Contains(value);

    public static string? ValidationError(string? value, string kind) =>
        IsSafe(value)
            ? null
            : $"{kind} must be lowercase ASCII kebab-case (1-64 characters) and not a reserved platform name.";

    public static string? ToResourceStem(string? value) =>
        IsSafe(value) ? value!.Replace('-', '_') : null;

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex KebabCase();
}
