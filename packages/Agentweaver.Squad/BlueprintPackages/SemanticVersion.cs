using System.Collections.Immutable;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Agentweaver.Squad.BlueprintPackages;

/// <summary>
/// SemVer 2.0.0 comparison without fixed-width integer conversion. Numeric core and prerelease
/// identifiers are compared by decimal digit count, so a package can use arbitrarily large versions.
/// </summary>
public sealed class SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
    private static readonly Regex Pattern = new(
        @"\A(?<major>0|[1-9][0-9]*)\.(?<minor>0|[1-9][0-9]*)\.(?<patch>0|[1-9][0-9]*)(?:-(?<pre>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+(?<build>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?\z",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(1));

    private readonly ImmutableArray<string> _preRelease;

    private SemanticVersion(string major, string minor, string patch, ImmutableArray<string> preRelease, string? build)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        _preRelease = preRelease;
        Build = build;
    }

    public string Major { get; }
    public string Minor { get; }
    public string Patch { get; }
    public ImmutableArray<string> PreRelease => _preRelease;
    public string? Build { get; }

    public static bool TryParse(string? value, out SemanticVersion? version)
    {
        version = null;
        if (value is null) return false;
        Match match;
        try
        {
            match = Pattern.Match(value);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
        if (!match.Success) return false;

        var prerelease = match.Groups["pre"].Success
            ? match.Groups["pre"].Value.Split('.', StringSplitOptions.None)
            : [];

        // SemVer forbids leading zeroes in numeric prerelease identifiers.
        if (prerelease.Any(identifier =>
            identifier.Length > 1 && identifier[0] == '0' && identifier.All(char.IsAsciiDigit)))
            return false;

        version = new SemanticVersion(
            match.Groups["major"].Value,
            match.Groups["minor"].Value,
            match.Groups["patch"].Value,
            [.. prerelease],
            match.Groups["build"].Success ? match.Groups["build"].Value : null);
        return true;
    }

    public static SemanticVersion Parse(string value) =>
        TryParse(value, out var version) ? version! : throw new FormatException($"Invalid SemVer: {value}");

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null) return 1;
        var result = CompareDecimal(Major, other.Major);
        if (result != 0) return result;
        result = CompareDecimal(Minor, other.Minor);
        if (result != 0) return result;
        result = CompareDecimal(Patch, other.Patch);
        if (result != 0) return result;

        if (_preRelease.Length == 0 || other._preRelease.Length == 0)
            return _preRelease.Length == other._preRelease.Length ? 0 : _preRelease.Length == 0 ? 1 : -1;

        for (var index = 0; index < Math.Min(_preRelease.Length, other._preRelease.Length); index++)
        {
            result = ComparePrerelease(_preRelease[index], other._preRelease[index]);
            if (result != 0) return result;
        }
        return _preRelease.Length.CompareTo(other._preRelease.Length);
    }

    public bool Equals(SemanticVersion? other) => other is not null && CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is SemanticVersion version && Equals(version);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Major, StringComparer.Ordinal);
        hash.Add(Minor, StringComparer.Ordinal);
        hash.Add(Patch, StringComparer.Ordinal);
        hash.Add(_preRelease.Length);
        foreach (var identifier in _preRelease) hash.Add(identifier, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
    public override string ToString() =>
        $"{Major}.{Minor}.{Patch}{(_preRelease.Length == 0 ? string.Empty : $"-{string.Join('.', _preRelease)}")}{(Build is null ? string.Empty : $"+{Build}")}";

    private static int ComparePrerelease(string left, string right)
    {
        var leftNumeric = left.All(char.IsAsciiDigit);
        var rightNumeric = right.All(char.IsAsciiDigit);
        if (leftNumeric && rightNumeric) return CompareDecimal(left, right);
        if (leftNumeric != rightNumeric) return leftNumeric ? -1 : 1;
        return string.CompareOrdinal(left, right);
    }

    private static int CompareDecimal(string left, string right) =>
        left.Length != right.Length ? left.Length.CompareTo(right.Length) : string.CompareOrdinal(left, right);
}
