using System.Collections.Immutable;

namespace Agentweaver.Orchestrator.Core;

/// <summary>Shared identifier, output-path, and dependency helpers for workflow validators.</summary>
internal static class WorkflowValidationSupport
{
    /// <summary>Returns outputs declared by fixed work or a BuildTest command.</summary>
    public static IEnumerable<string> PrescribedOutputs(WorkflowStepDefinition step) =>
        step.FixedWork?.DeclaredOutputs ??
        (step.BuildTestCommand is { } command
            ? command.Outputs.Select(output => output.RelativePath)
            : []);

    /// <summary>Accepts bounded ASCII identifiers with a letter or digit first.</summary>
    public static bool IsStableId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= WorkflowDomainLimits.MaximumIdentifierLength &&
        IsAsciiLetterOrDigit(value[0]) &&
        value.All(character =>
            IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');

    /// <summary>Accepts a bounded opaque revision/reference without surrounding whitespace or control characters.</summary>
    public static bool IsOpaqueReference(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= WorkflowDomainLimits.MaximumIdentifierLength &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        !value.Any(char.IsControl);

    /// <summary>Rejects absolute paths, traversal, empty segments, platform separators, and control characters.</summary>
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

    /// <summary>Checks path conflicts case-insensitively, including a matching basename.</summary>
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

    /// <summary>Adds a dependency only when the exact identifier is not already present.</summary>
    public static ImmutableArray<string> AddDistinct(
        ImmutableArray<string> values,
        string value) =>
        values.Contains(value, StringComparer.Ordinal)
            ? values
            : values.Add(value);

    /// <summary>Checks whether one workflow step reaches another through its dependency edges.</summary>
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

    /// <summary>Checks whether one work item reaches another while traversing only known plan items.</summary>
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
