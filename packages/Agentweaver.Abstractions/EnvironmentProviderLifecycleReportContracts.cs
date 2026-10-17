namespace Agentweaver.Abstractions;

/// <summary>Identifies which provider lifecycle event an Environment report describes.</summary>
public enum EnvironmentProviderLifecycleReportKind
{
    Suspend,
    Relocation
}

/// <summary>Lifecycle report states exposed by the current contract.</summary>
public enum EnvironmentProviderLifecycleReportState
{
    Pending
}

/// <summary>
/// Provider-to-Environment report for a suspend or relocation event. Validation requires a
/// versioned provider/resource identity and positive resource and fencing generations.
/// </summary>
public sealed record EnvironmentProviderLifecycleReportRequest
{
    /// <summary>Creates a report request; required nested fence and resource values cannot be null.</summary>
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
    /// <summary>Provider-side generation used to reject reports from an older provider operation.</summary>
    public long ProviderFencingGeneration { get; }
    public DateTimeOffset ReportedAt { get; }

    /// <summary>Checks contract version, event identity, adapter version, resource, and fencing values.</summary>
    /// <exception cref="ArgumentException">The report has an unsupported or invalid field.</exception>
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

/// <summary>
/// Environment-owned persisted view of an admitted provider lifecycle report, including
/// the current fence and operation identity used for replay and stale-report checks.
/// </summary>
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
    /// <summary>True when this response represents a previously admitted provider event.</summary>
    public bool Replayed { get; init; }
}
