using System.Collections.Immutable;

namespace Agentweaver.Abstractions;

public sealed record ProjectRunLimitSnapshot(int MaxPromptTokens);

public sealed record ProjectRunSelectionResponse(
    string ProjectId,
    string RunId,
    long ProjectRevision,
    long ProjectConfigurationRevision,
    long PlatformRuntimeRevision,
    string ContextRevision,
    ImmutableArray<EffectiveProviderSelection> Providers,
    ProjectRunLimitSnapshot RunLimits);
