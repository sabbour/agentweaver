using System.IO.Compression;
using System.Security.Cryptography;

namespace Agentweaver.Projects.Config.AgentSuite;

/// <summary>Upstream <c>contentEntry</c>, plus the full bytes of every regular file for Agentweaver retention.</summary>
internal sealed class ContentEntry
{
    public required string Path { get; init; }
    public required string Type { get; init; }
    public uint Mode { get; init; }
    public long Uid { get; init; }
    public long Gid { get; init; }
    public long Size { get; init; }
    public string Digest { get; init; } = string.Empty;
    public string LinkTarget { get; init; } = string.Empty;

    /// <summary>Upstream retained metadata (<c>.json</c> files up to 4 MiB); null otherwise.</summary>
    public byte[]? Data { get; init; }

    /// <summary>Exact bytes of a regular file regardless of upstream retention.</summary>
    public byte[]? Bytes { get; init; }
}

/// <summary>Upstream <c>contentSet</c>. Entries keep insertion order.</summary>
internal sealed class ContentSet
{
    private readonly Dictionary<string, ContentEntry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _folded = new(StringComparer.Ordinal);
    private readonly List<ContentEntry> _ordered = [];

    public IReadOnlyList<ContentEntry> Entries => _ordered;

    public ContentEntry? Get(string path) => _entries.GetValueOrDefault(path);

    /// <summary>Upstream <c>contentSet.add</c>.</summary>
    public string? Add(ContentEntry entry)
    {
        if (_entries.ContainsKey(entry.Path))
            return $"duplicate content path {entry.Path}";
        var folded = Go.FoldCase(entry.Path);
        if (_folded.TryGetValue(folded, out var existing))
            return $"case-folding path collision between {existing} and {entry.Path}";
        _entries[entry.Path] = entry;
        _folded[folded] = entry.Path;
        _ordered.Add(entry);
        return null;
    }

    /// <summary>Upstream <c>contentSet.data</c>.</summary>
    public string? Data(string name, out byte[] data)
    {
        data = [];
        if (AgentSuiteContent.ValidateContentPath(name, out var clean) is { } pathError)
            return pathError;
        if (!_entries.TryGetValue(clean, out var entry))
            return $"required file {clean} is missing";
        if (entry.Type != "file")
            return $"{clean} is not a regular file";
        if (entry.Data is null)
            return $"{clean} is not retained metadata";
        data = entry.Data;
        return null;
    }
}

/// <summary>Ports upstream content.go: the content layer reader and path rules.</summary>
internal static class AgentSuiteContent
{
    public const int MaxContentEntries = 100_000;
    public const long MaxContentBytes = 4L << 30;
    public const long MaxRetainedMetadataBytes = 256L << 20;
    public const int MaxPathBytes = 1024;

    /// <summary>
    /// Agentweaver hard bound on decompressed tar bytes. Upstream allows 4 GiB; exceeding this bound
    /// raises <see cref="AgentSuiteLimitException"/> and is refused as a profile limit.
    /// </summary>
    public const long MaxDecompressedBytes = 64L << 20;

    /// <summary>Upstream <c>loadContentLayer</c>.</summary>
    public static string? LoadContentLayer(byte[] layer, out ContentSet? set)
    {
        set = null;
        if (ReadGzipHeader(layer, out var modTime) is { } headerError)
            return $"content layer is not gzip: {headerError}";
        if (modTime != 0)
            return "content gzip modification time must be unset";

        using var gzip = new GZipStream(new MemoryStream(layer, writable: false), CompressionMode.Decompress);
        var stream = new GoReader(gzip, MaxDecompressedBytes);
        var tar = new GoTarReader(stream);
        var content = new ContentSet();
        var regular = new HashSet<string>(StringComparer.Ordinal);
        long total = 0, retained = 0;
        var count = 0;
        while (true)
        {
            var error = tar.Next(out var header);
            if (error == GoReader.Eof)
                break;
            if (error is not null)
                return $"read content tar: {error}";
            if (!header!.ModTimeIsEpoch)
                return $"{header.Name} tar modification time must be the Unix epoch";
            if (header.AccessTimeSet || header.ChangeTimeSet)
                return $"{header.Name} tar access and change times must be unset";
            count++;
            if (count > MaxContentEntries)
                return $"content has more than {MaxContentEntries} entries";
            var rawName = header.Name;
            if (header.Typeflag == GoTarReader.TypeDir && rawName.EndsWith('/'))
                rawName = rawName[..^1];
            if (ValidateContentPath(rawName, out var name) is { } pathError)
                return pathError;
            if ((header.Mode & 0x0E00) != 0)
                return $"{name} has setid or sticky mode {Go.AlternateOctal(header.Mode)}";
            if ((header.Mode & 0x12) != 0 && header.Typeflag is GoTarReader.TypeReg or GoTarReader.TypeLink)
                return $"{name} is group or world writable";

            ContentEntry record;
            var mode = (uint)(header.Mode & 0x1FF);
            switch (header.Typeflag)
            {
                case GoTarReader.TypeDir:
                    record = new ContentEntry { Path = name, Type = "directory", Mode = mode, Uid = header.Uid, Gid = header.Gid };
                    break;
                case GoTarReader.TypeReg:
                {
                    if (header.Size < 0 || header.Size > MaxContentBytes - total)
                        return $"content exceeds {MaxContentBytes} bytes";
                    total += header.Size;
                    var retain = name.EndsWith(".json", StringComparison.Ordinal) && header.Size <= AgentSuiteJson.MaxBytes;
                    if (retain)
                    {
                        if (header.Size > MaxRetainedMetadataBytes - retained)
                            return $"retained metadata exceeds {MaxRetainedMetadataBytes} bytes";
                    }
                    var readError = tar.ReadAll(header.Size + 1, out var data);
                    if (retain)
                    {
                        if (readError is not null)
                            return readError;
                        if (data.LongLength != header.Size)
                            return $"{name} size does not match tar header";
                        retained += header.Size;
                    }
                    else if (readError is not null || data.LongLength != header.Size)
                    {
                        return $"read {name}: {readError ?? GoReader.UnexpectedEof}";
                    }
                    record = new ContentEntry
                    {
                        Path = name,
                        Type = "file",
                        Mode = mode,
                        Uid = header.Uid,
                        Gid = header.Gid,
                        Size = header.Size,
                        Digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(data)),
                        Data = retain ? data : null,
                        Bytes = data,
                    };
                    regular.Add(name);
                    break;
                }
                case GoTarReader.TypeSymlink:
                    if (ValidateLinkTarget(name, header.Linkname) is { } linkError)
                        return linkError;
                    record = new ContentEntry { Path = name, Type = "symlink", Mode = mode, Uid = header.Uid, Gid = header.Gid, LinkTarget = header.Linkname };
                    break;
                case GoTarReader.TypeLink:
                {
                    if (ValidateContentPath(header.Linkname, out var target) is { } targetError)
                        return $"{name} hardlink target: {targetError}";
                    if (!regular.Contains(target))
                        return $"{name} hardlink target {target} is not an earlier regular file";
                    record = new ContentEntry { Path = name, Type = "hardlink", Mode = mode, Uid = header.Uid, Gid = header.Gid, LinkTarget = target };
                    break;
                }
                default:
                    return $"{name} has unsupported tar type {header.Typeflag}";
            }
            if (content.Add(record) is { } addError)
                return addError;
        }

        var trailing = new byte[1];
        var written = stream.Read(trailing, out var trailingError);
        if (written != 0)
            return "content gzip contains data after the tar archive";
        if (trailingError is not null && trailingError != GoReader.Eof)
            return $"finish content gzip stream: {trailingError}";
        set = content;
        return null;
    }

    /// <summary>
    /// Go <c>gzip.NewReader</c> header parsing. Returns the Go error text and the header MTIME.
    /// </summary>
    public static string? ReadGzipHeader(ReadOnlySpan<byte> data, out uint modTime)
    {
        modTime = 0;
        if (data.Length == 0)
            return GoReader.Eof;
        if (data.Length < 10)
            return GoReader.UnexpectedEof;
        if (data[0] != 0x1f || data[1] != 0x8b || data[2] != 8)
            return "gzip: invalid header";
        var flags = data[3];
        modTime = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[4..8]);
        var offset = 10;
        if ((flags & 0x04) != 0)
        {
            if (data.Length < offset + 2)
                return GoReader.UnexpectedEof;
            var length = data[offset] | data[offset + 1] << 8;
            offset += 2;
            if (data.Length < offset + length)
                return GoReader.UnexpectedEof;
            offset += length;
        }
        foreach (var flag in new byte[] { 0x08, 0x10 })
        {
            if ((flags & flag) == 0)
                continue;
            var end = data[offset..].IndexOf((byte)0);
            if (end < 0)
                return data.Length - offset >= 512 ? "gzip: invalid header" : GoReader.UnexpectedEof;
            if (end >= 512)
                return "gzip: invalid header";
            offset += end + 1;
        }
        if ((flags & 0x02) != 0)
        {
            if (data.Length < offset + 2)
                return GoReader.UnexpectedEof;
            var expected = data[offset] | data[offset + 1] << 8;
            var crc = Crc32(data[..offset]);
            if ((crc & 0xffff) != expected)
                return "gzip: invalid header";
        }
        return null;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? crc >> 1 ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }

    /// <summary>Upstream <c>validateContentPath</c>; returns the error text or null and the clean path.</summary>
    public static string? ValidateContentPath(string value, out string clean)
    {
        clean = string.Empty;
        if (value.Length == 0 || Go.ByteLength(value) > MaxPathBytes || !Go.ValidString(value) || value.Contains('\0'))
            return $"unsafe content path {Go.Quote(value)}";
        if (value.Contains('\\') || value.StartsWith('/'))
            return $"content path must be relative POSIX syntax: {Go.Quote(value)}";
        var cleaned = Go.CleanPath(value);
        if (cleaned != value || cleaned == "." || cleaned == ".." || cleaned.StartsWith("../", StringComparison.Ordinal))
            return $"content path is not normalized below the root: {Go.Quote(value)}";
        foreach (var part in cleaned.Split('/'))
        {
            if (part is "" or "." or "..")
                return $"content path contains an unsafe segment: {Go.Quote(value)}";
        }
        clean = cleaned;
        return null;
    }

    /// <summary>Upstream <c>validateLinkTarget</c>.</summary>
    public static string? ValidateLinkTarget(string name, string target)
    {
        if (target.Length == 0 || Go.ByteLength(target) > MaxPathBytes || !Go.ValidString(target) || target.Contains('\0') ||
            target.Contains('\\') || target.StartsWith('/'))
            return $"{name} has unsafe link target {Go.Quote(target)}";
        var resolved = Go.CleanPath(Go.PathJoin(Go.PathDir(name), target));
        if (resolved is "." or ".." || resolved.StartsWith("../", StringComparison.Ordinal))
            return $"{name} link target escapes the content root";
        return null;
    }
}
