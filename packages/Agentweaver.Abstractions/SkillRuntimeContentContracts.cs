using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Agentweaver.Abstractions;

public sealed record SkillRuntimeContentResourceV1(
    string RelativePath,
    ImmutableArray<byte> Content,
    string Sha256);

public sealed record SkillRuntimeContentV1(
    string SkillId,
    long Revision,
    string Name,
    string Description,
    string Instructions,
    string ContentDigest,
    ImmutableArray<SkillRuntimeContentResourceV1> Resources);

public sealed record SkillRuntimeContentProjectionV1(
    int ContractVersion,
    Guid RuntimeInstanceId,
    long RegistrationRevision,
    long ProjectConfigurationRevision,
    long ExecutionFence,
    string TenantId,
    string ProjectId,
    string RunId,
    string SessionId,
    string AgentId,
    string AcceptedSelectionHash,
    ImmutableArray<SkillRuntimeContentV1> Skills);

public static class SkillRuntimeContentContract
{
    public const int CurrentVersion = 1;
    public const int MaxNameLength = 64;
    public const int MaxDescriptionLength = 1024;
    public const int MaxInstructionsBytes = 256 * 1024;
    public const int MaxSkillMarkdownBytes = 512 * 1024;
    public const int MaxResourceBytes = 256 * 1024;
    public const int MaxTotalResourceBytes = 1024 * 1024;
    public const int MaxResourceCount = 64;
    public const int MaxSkillCount = 32;
    public const int MaxProjectionBytes = 2 * 1024 * 1024;
    private const int MaxResourcePathLength = 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly byte[] DigestDomain = "agentweaver.skill-content.v1"u8.ToArray();

    public static string ComputeContentDigest(
        string name,
        string description,
        string instructions,
        ImmutableArray<SkillRuntimeContentResourceV1> resources)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(instructions);
        if (resources.IsDefault)
            throw new ArgumentException("A skill resource inventory is required.", nameof(resources));

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendFrame(hash, DigestDomain);
        AppendFrame(hash, StrictUtf8.GetBytes(name));
        AppendFrame(hash, StrictUtf8.GetBytes(description));
        AppendFrame(hash, StrictUtf8.GetBytes(instructions));
        Span<byte> count = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(count, resources.Length);
        AppendFrame(hash, count);
        foreach (var resource in resources.OrderBy(item => item.RelativePath, StringComparer.Ordinal))
        {
            AppendFrame(hash, StrictUtf8.GetBytes(resource.RelativePath));
            AppendFrame(hash, resource.Content.AsSpan());
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static void ValidateProjection(SkillRuntimeContentProjectionV1 projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        if (projection.ContractVersion != CurrentVersion ||
            projection.RuntimeInstanceId == Guid.Empty ||
            projection.RegistrationRevision <= 0 ||
            projection.ProjectConfigurationRevision <= 0 ||
            projection.ExecutionFence <= 0 ||
            projection.Skills.IsDefault ||
            projection.Skills.Length > MaxSkillCount)
            throw new ArgumentException("The skill-content projection binding is invalid.");
        RequireIdentifier(projection.TenantId, nameof(projection.TenantId));
        RequireIdentifier(projection.ProjectId, nameof(projection.ProjectId));
        RequireIdentifier(projection.RunId, nameof(projection.RunId));
        RequireIdentifier(projection.SessionId, nameof(projection.SessionId));
        RequireIdentifier(projection.AgentId, nameof(projection.AgentId));
        RequireHash(projection.AcceptedSelectionHash, nameof(projection.AcceptedSelectionHash));

        var skillIds = new HashSet<string>(StringComparer.Ordinal);
        long totalBytes = 0;
        foreach (var skill in projection.Skills)
        {
            RequireIdentifier(skill.SkillId, nameof(skill.SkillId));
            if (!skillIds.Add(skill.SkillId) || skill.Revision <= 0 ||
                string.IsNullOrWhiteSpace(skill.Name) || string.IsNullOrWhiteSpace(skill.Description) ||
                string.IsNullOrWhiteSpace(skill.Instructions) || skill.Resources.IsDefault)
                throw new ArgumentException("The skill-content projection contains an invalid skill revision.");
            if (skill.Name.Length > MaxNameLength ||
                skill.Description.Length > MaxDescriptionLength ||
                skill.Instructions.Contains('\0') ||
                StrictUtf8.GetByteCount(skill.Instructions) > MaxInstructionsBytes)
                throw new ArgumentException("The skill-content projection contains oversized skill text.");
            RequireHash(skill.ContentDigest, nameof(skill.ContentDigest));
            if (skill.Resources.Length > MaxResourceCount)
                throw new ArgumentException("The skill-content projection contains too many resources.");

            var resourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long totalResourceBytes = 0;
            foreach (var resource in skill.Resources)
            {
                if (NormalizeResourcePath(resource.RelativePath) != resource.RelativePath ||
                    !resourcePaths.Add(resource.RelativePath) || resource.Content.IsDefault)
                    throw new ArgumentException("The skill-content projection contains an invalid resource.");
                if (resource.Content.Length > MaxResourceBytes)
                    throw new ArgumentException("A skill resource exceeds its size limit.");
                RequireHash(resource.Sha256, nameof(resource.Sha256));
                var resourceHash = Convert.ToHexString(SHA256.HashData(resource.Content.AsSpan()))
                    .ToLowerInvariant();
                if (!string.Equals(resourceHash, resource.Sha256, StringComparison.Ordinal))
                    throw new ArgumentException("A skill resource digest does not match its content.");
                var resourceText = StrictUtf8.GetString(resource.Content.AsSpan());
                if (resourceText.Contains('\0'))
                    throw new ArgumentException("A skill resource contains a NUL byte.");
                totalResourceBytes = checked(totalResourceBytes + resource.Content.Length);
                totalBytes = checked(totalBytes + StrictUtf8.GetByteCount(resource.RelativePath) +
                    resource.Content.Length);
            }
            if (totalResourceBytes > MaxTotalResourceBytes)
                throw new ArgumentException("A skill resource inventory exceeds its aggregate size limit.");

            var digest = ComputeContentDigest(skill.Name, skill.Description, skill.Instructions, skill.Resources);
            if (!string.Equals(digest, skill.ContentDigest, StringComparison.Ordinal))
                throw new ArgumentException("A skill content digest does not match its content.");
            totalBytes = checked(totalBytes + StrictUtf8.GetByteCount(skill.Name) +
                StrictUtf8.GetByteCount(skill.Description) + StrictUtf8.GetByteCount(skill.Instructions));
            if (totalBytes > MaxProjectionBytes)
                throw new ArgumentException("The skill-content projection exceeds its size limit.");
        }
    }

    public static string NormalizeResourcePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new ArgumentException("Resource paths must be non-empty relative paths.", nameof(raw));
        var trimmed = raw.Trim();
        if (trimmed.StartsWith('/') || trimmed.StartsWith('\\'))
            throw new ArgumentException($"Resource path '{raw}' is rooted.", nameof(raw));

        var path = trimmed.Replace('\\', '/');
        if (path.Length == 0 || path.Length > MaxResourcePathLength)
            throw new ArgumentException($"Resource path '{raw}' is invalid or rooted.", nameof(raw));

        var segments = path.Split('/', StringSplitOptions.None);
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment is "." or ".." ||
                segment.Contains(':') ||
                segment.Any(char.IsControl) ||
                segment.EndsWith(' ') || segment.EndsWith('.') ||
                !segment.IsNormalized(NormalizationForm.FormC) ||
                IsWindowsDeviceName(segment))
                throw new ArgumentException($"Resource path '{raw}' contains an unsafe path segment.", nameof(raw));
        }
        return string.Join('/', segments);
    }

    private static void AppendFrame(IncrementalHash hash, ReadOnlySpan<byte> bytes)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static void RequireIdentifier(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '_' or '-' or ':')))
            throw new ArgumentException("A valid skill-content identifier is required.", name);
    }

    private static void RequireHash(string? value, string name)
    {
        if (value is not { Length: 64 } ||
            value.Any(character => character is not (>= 'a' and <= 'f' or >= '0' and <= '9')))
            throw new ArgumentException("A lowercase SHA-256 digest is required.", name);
    }

    private static bool IsWindowsDeviceName(string segment)
    {
        var stem = segment.Split('.', 2)[0];
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
               Regex.IsMatch(stem, @"^(COM|LPT)[1-9]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
