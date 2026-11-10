using System.Collections.Immutable;

namespace Agentweaver.Abstractions;

/// <summary>Reasoning-effort values that v1 runtime contracts can carry.</summary>
public static class ModelReasoningEfforts
{
    /// <summary>All known values, ordered from least to most reasoning.</summary>
    public static ImmutableArray<string> SupportedValues { get; } =
        ["none", "minimal", "low", "medium", "high", "xhigh"];

    /// <summary>Checks whether a value is part of the v1 reasoning-effort vocabulary.</summary>
    public static bool IsSupported(string? value) =>
        value is not null && SupportedValues.Contains(value, StringComparer.Ordinal);
}
