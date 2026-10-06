using System.Collections.Immutable;

namespace Agentweaver.Orchestrator.Core;

internal static class WorkflowValidationSupport
{
    public static bool IsStableId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= WorkflowDomainLimits.MaximumIdentifierLength &&
        IsAsciiLetterOrDigit(value[0]) &&
        value.All(character =>
            IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');

    public static bool IsOpaqueReference(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= WorkflowDomainLimits.MaximumIdentifierLength &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        !value.Any(char.IsControl);

    public static bool IsValidOutputPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            path.Length > WorkflowDomainLimits.MaximumOutputPathLength ||
            !string.Equals(path, path.Trim(), StringComparison.Ordinal) ||
            path[0] == '/' ||
            path.Contains('\\') ||
            path.Contains(':') ||
            path.Any(char.IsControl))
            return false;

        var segments = path.Split('/');
        return segments.All(segment =>
            segment.Length > 0 && segment is not ("." or ".."));
    }

    public static bool OutputPathsMatch(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase) ||
            left.EndsWith('/' + right, StringComparison.OrdinalIgnoreCase) ||
            right.EndsWith('/' + left, StringComparison.OrdinalIgnoreCase))
            return true;

        return (!right.Contains('/') && string.Equals(
                    FileName(left), right, StringComparison.OrdinalIgnoreCase)) ||
               (!left.Contains('/') && string.Equals(
                    FileName(right), left, StringComparison.OrdinalIgnoreCase));
    }

    public static ImmutableArray<string> AddDistinct(
        ImmutableArray<string> values,
        string value) =>
        values.Contains(value, StringComparer.Ordinal)
            ? values
            : values.Add(value);

    public static bool Reaches(
        string start,
        string target,
        IReadOnlyDictionary<string, ImmutableArray<string>> dependencies)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(start);

        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current) || !dependencies.TryGetValue(current, out var currentDependencies))
                continue;

            foreach (var dependency in currentDependencies)
            {
                if (string.Equals(dependency, target, StringComparison.Ordinal))
                    return true;
                pending.Push(dependency);
            }
        }

        return false;
    }

    public static bool Reaches(
        string start,
        string target,
        IReadOnlyDictionary<string, ImmutableArray<string>> dependencies,
        IReadOnlyDictionary<string, WorkPlanItem> items)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(start);

        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current) || !dependencies.TryGetValue(current, out var currentDependencies))
                continue;

            foreach (var dependency in currentDependencies)
            {
                if (string.Equals(dependency, target, StringComparison.Ordinal))
                    return true;
                if (items.ContainsKey(dependency))
                    pending.Push(dependency);
            }
        }

        return false;
    }

    private static string FileName(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator < 0 ? path : path[(separator + 1)..];
    }

    private static bool IsAsciiLetterOrDigit(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';
}
