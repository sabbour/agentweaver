namespace Agentweaver.Api.Memory;

/// <summary>EF records for the owner-private immutable Blueprint package library.</summary>
public sealed class BlueprintPackageLibraryRecord
{
    public string OwnerId { get; set; } = "";
    public string PackageId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class BlueprintPackageVersionRecord
{
    public string OwnerId { get; set; } = "";
    public string PackageId { get; set; } = "";
    public string CanonicalVersion { get; set; } = "";
    public string ContentDigest { get; set; } = "";
    public string PayloadSetDigest { get; set; } = "";
    public string RawManifestSha256 { get; set; } = "";
    public string? ContainerSha256 { get; set; }
    public byte[] RawManifest { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class BlueprintPackagePayloadRecord
{
    public string OwnerId { get; set; } = "";
    public string PackageId { get; set; } = "";
    public string CanonicalVersion { get; set; } = "";
    public string Path { get; set; } = "";
    public byte[] Bytes { get; set; } = [];
}

public sealed class BlueprintPackageAcquisitionRecord
{
    public string OwnerId { get; set; } = "";
    public string PackageId { get; set; } = "";
    public string CanonicalVersion { get; set; } = "";
    public int Ordinal { get; set; }
    public string Source { get; set; } = "";
    public string? Producer { get; set; }
    public string? Repository { get; set; }
    public string? Revision { get; set; }
    public DateTimeOffset? AcquiredAt { get; set; }
}
