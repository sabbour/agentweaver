namespace Agentweaver.Abstractions;

public enum EnvironmentProviderLifecycleReportKind
{
    Suspend,
    Relocation
}

public enum EnvironmentProviderLifecycleReportState
{
    Pending
}

public sealed record EnvironmentProviderLifecycleReportRequest
{
    public EnvironmentProviderLifecycleReportRequest(
        int contractVersion,
        Guid providerEventId,
        EnvironmentProviderLifecycleReportKind kind,
        EnvironmentGenerationFence fence,
        string adapterVersion,
        ProviderResourceRef resource,
        long providerFencingGeneration,
        DateTimeOffset reportedAt)
    {
        ContractVersion = contractVersion;
        ProviderEventId = providerEventId;
        Kind = kind;
        Fence = fence ?? throw new ArgumentNullException(nameof(fence));
        AdapterVersion = adapterVersion;
        Resource = resource ?? throw new ArgumentNullException(nameof(resource));
        ProviderFencingGeneration = providerFencingGeneration;
        ReportedAt = reportedAt;
    }

    public int ContractVersion { get; }
    public Guid ProviderEventId { get; }
    public EnvironmentProviderLifecycleReportKind Kind { get; }
    public EnvironmentGenerationFence Fence { get; }
    public string AdapterVersion { get; }
    public ProviderResourceRef Resource { get; }
    public long ProviderFencingGeneration { get; }
    public DateTimeOffset ReportedAt { get; }

    public EnvironmentProviderLifecycleReportRequest Validate()
    {
        if (ContractVersion < 1 ||
            ProviderEventId == Guid.Empty ||
            !Enum.IsDefined(Kind) ||
            !Version.TryParse(AdapterVersion, out var adapterVersion) ||
            adapterVersion.Major < 1 ||
            !string.Equals(adapterVersion.ToString(), AdapterVersion, StringComparison.Ordinal) ||
            Resource.Seam != ProviderSeam.Sandbox ||
            string.IsNullOrWhiteSpace(Resource.ProviderId) ||
            Resource.ProviderId.Length > 128 ||
            Resource.ProviderId.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(Resource.ResourceId) ||
            Resource.ResourceId.Length > 512 ||
            Resource.ResourceId.Any(char.IsControl) ||
            Resource.Generation < 1 ||
            ProviderFencingGeneration < 1 ||
            ReportedAt <= DateTimeOffset.MinValue)
            throw new ArgumentException("The provider lifecycle report is invalid.");
        return this;
    }
}

public sealed record EnvironmentProviderLifecycleReportSnapshot(
    EnvironmentGenerationFence Fence,
    Guid ProviderEventId,
    int ContractVersion,
    EnvironmentProviderLifecycleReportKind Kind,
    DateTimeOffset ReportedAt,
    string ProviderId,
    string AdapterVersion,
    ProviderResourceRef Resource,
    long ProviderFencingGeneration,
    long CurrentFencingGeneration,
    long LeaseRevision,
    Guid SandboxOperationId,
    string RequestFingerprint,
    string CoreOperationKey,
    long? CoreExecutionFence,
    EnvironmentProviderLifecycleReportState State,
    string? LastErrorCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public bool Replayed { get; init; }
}
