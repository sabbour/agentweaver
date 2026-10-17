using System.Collections.Immutable;

namespace Agentweaver.Abstractions;

/// <summary>Run-specific limits copied from the project configuration snapshot.</summary>
public sealed record ProjectRunLimitSnapshot(int? MaxPromptTokens);

/// <summary>
/// Projects &amp; Config response that pins the revisions and effective provider choices
/// selected for one run; consumers retain it as immutable run input.
/// </summary>
public sealed record ProjectRunSelectionResponse(
    string ProjectId,
    string RunId,
    long ProjectRevision,
    long ProjectConfigurationRevision,
    long PlatformRuntimeRevision,
    string ContextRevision,
    ImmutableArray<EffectiveProviderSelection> Providers,
    ProjectRunLimitSnapshot RunLimits);
