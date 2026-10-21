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
        // Reject names that differ only by letter case; they would overwrite each other on
        // case-insensitive file systems.
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
        // Only small ".json" files keep their bytes in Data; other files cannot be read as metadata.
        if (entry.Data is null)
            return $"{clean} is not retained metadata";
        data = entry.Data;
        return null;
    }
}

/// <summary>Ports upstream content.go: the content layer reader and path rules.</summary>
internal static class AgentSuiteContent
{
    // Limits copied from the Go validator (entry count, total file bytes, retained JSON bytes, path length).
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
        // The layer is a gzip-compressed tar. Check the gzip header ourselves first, because
        // .NET's GZipStream does not expose the header's modification time.
        if (ReadGzipHeader(layer, out var modTime) is { } headerError)
            return $"content layer is not gzip: {headerError}";
        // Deterministic builds must leave the gzip MTIME at 0 so identical input gives identical bytes.
        if (modTime != 0)
            return "content gzip modification time must be unset";

        // Decompress on the fly; GoReader stops with a limit error past MaxDecompressedBytes.
        using var gzip = new GZipStream(new MemoryStream(layer, writable: false), CompressionMode.Decompress);
        var stream = new GoReader(gzip, MaxDecompressedBytes);
        var tar = new GoTarReader(stream);
        var content = new ContentSet();
        // Paths of regular files seen so far; hard links may only point to these.
        var regular = new HashSet<string>(StringComparer.Ordinal);
        // total = bytes of all regular files; retained = bytes of ".json" files kept in memory.
        long total = 0, retained = 0;
        var count = 0;
        while (true)
        {
            var error = tar.Next(out var header);
            // "EOF" here means both end-of-archive zero blocks were read.
            if (error == GoReader.Eof)
                break;
            if (error is not null)
                return $"read content tar: {error}";
            // Determinism rules for every entry: mtime is exactly 1970-01-01, and no atime/ctime.
            if (!header!.ModTimeIsEpoch)
                return $"{header.Name} tar modification time must be the Unix epoch";
            if (header.AccessTimeSet || header.ChangeTimeSet)
                return $"{header.Name} tar access and change times must be unset";
            count++;
            if (count > MaxContentEntries)
                return $"content has more than {MaxContentEntries} entries";
            // Directory names may end in '/'; strip it before the path checks.
            var rawName = header.Name;
            if (header.Typeflag == GoTarReader.TypeDir && rawName.EndsWith('/'))
                rawName = rawName[..^1];
            if (ValidateContentPath(rawName, out var name) is { } pathError)
                return pathError;
            // 0x0E00 is octal 07000: the setuid, setgid and sticky bits. None are allowed.
            if ((header.Mode & 0x0E00) != 0)
                return $"{name} has setid or sticky mode {Go.AlternateOctal(header.Mode)}";
            // 0x12 is octal 022: group-write and world-write. Files and hard links must not have them.
            if ((header.Mode & 0x12) != 0 && header.Typeflag is GoTarReader.TypeReg or GoTarReader.TypeLink)
                return $"{name} is group or world writable";

            ContentEntry record;
            // Keep only the permission bits (octal 0777).
            var mode = (uint)(header.Mode & 0x1FF);
            switch (header.Typeflag)
            {
                case GoTarReader.TypeDir:
                    record = new ContentEntry { Path = name, Type = "directory", Mode = mode, Uid = header.Uid, Gid = header.Gid };
                    break;
                case GoTarReader.TypeReg:
                {
                    // Check the running total before adding (written to avoid overflow).
                    if (header.Size < 0 || header.Size > MaxContentBytes - total)
                        return $"content exceeds {MaxContentBytes} bytes";
                    total += header.Size;
                    // Small ".json" files are kept as metadata so the validator can parse them later.
                    var retain = name.EndsWith(".json", StringComparison.Ordinal) && header.Size <= AgentSuiteJson.MaxBytes;
                    if (retain)
                    {
                        if (header.Size > MaxRetainedMetadataBytes - retained)
                            return $"retained metadata exceeds {MaxRetainedMetadataBytes} bytes";
                    }
                    // Read one extra byte so a body that is shorter or longer than Size is detected.
                    var readError = tar.ReadAll(header.Size + 1, out var data);
                    // The two branches return different messages to match the Go error texts.
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
                    // Every file gets a SHA-256 digest; Agentweaver also keeps the full bytes in Bytes.
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
                    // Symlink targets are relative to the link's folder and must stay inside the content root.
                    if (ValidateLinkTarget(name, header.Linkname) is { } linkError)
                        return linkError;
                    record = new ContentEntry { Path = name, Type = "symlink", Mode = mode, Uid = header.Uid, Gid = header.Gid, LinkTarget = header.Linkname };
                    break;
                case GoTarReader.TypeLink:
                {
                    // Hard link targets are paths from the content root and must name a regular file
                    // that appeared earlier in the archive.
                    if (ValidateContentPath(header.Linkname, out var target) is { } targetError)
                        return $"{name} hardlink target: {targetError}";
                    if (!regular.Contains(target))
                        return $"{name} hardlink target {target} is not an earlier regular file";
                    record = new ContentEntry { Path = name, Type = "hardlink", Mode = mode, Uid = header.Uid, Gid = header.Gid, LinkTarget = target };
                    break;
                }
                default:
                    // Devices, FIFOs, global PAX headers and other types are not allowed.
                    return $"{name} has unsupported tar type {header.Typeflag}";
            }
            if (content.Add(record) is { } addError)
                return addError;
        }

        // After the tar end blocks, the decompressed stream must be empty: try to read one more byte.
        var trailing = new byte[1];
        var written = stream.Read(trailing, out var trailingError);
        if (written != 0)
            return "content gzip contains data after the tar archive";
        // A decompression error raised while reaching the end of the gzip stream is reported here.
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
        // Go reports "EOF" for empty input and "unexpected EOF" for a header cut short.
        if (data.Length == 0)
            return GoReader.Eof;
        // The fixed gzip header is 10 bytes.
        if (data.Length < 10)
            return GoReader.UnexpectedEof;
        // Bytes 0-1 are the gzip magic number (1F 8B); byte 2 is the method, 8 = deflate.
        if (data[0] != 0x1f || data[1] != 0x8b || data[2] != 8)
            return "gzip: invalid header";
        // Byte 3 holds flags saying which optional header fields follow.
        var flags = data[3];
        // Bytes 4-7 are MTIME, a little-endian Unix time; 0 means "not set".
        modTime = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[4..8]);
        // Bytes 8 (extra flags) and 9 (OS) are ignored; optional fields start at offset 10.
        var offset = 10;
        // FEXTRA (0x04): a 2-byte little-endian length, then that many bytes to skip.
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
        // FNAME (0x08) and FCOMMENT (0x10): NUL-terminated strings, in that order.
        // Like Go, a string longer than 512 bytes is an invalid header.
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
        // FHCRC (0x02): a 2-byte checksum equal to the low 16 bits of the CRC-32 of all header bytes before it.
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
        // Standard CRC-32 (the one used by gzip and zip), computed bit by bit.
        // Start with all bits set, use the reversed polynomial 0xEDB88320, and invert at the end.
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
        // Basic safety: non-empty, at most 1024 bytes, valid UTF-8, and no NUL bytes.
        if (value.Length == 0 || Go.ByteLength(value) > MaxPathBytes || !Go.ValidString(value) || value.Contains('\0'))
            return $"unsafe content path {Go.Quote(value)}";
        // Only relative paths with forward slashes are allowed.
        if (value.Contains('\\') || value.StartsWith('/'))
            return $"content path must be relative POSIX syntax: {Go.Quote(value)}";
        // The path must already be in clean form (no "a//b", "a/./b", "a/../b", trailing '/'),
        // and must not point at or above the root.
        var cleaned = Go.CleanPath(value);
        if (cleaned != value || cleaned == "." || cleaned == ".." || cleaned.StartsWith("../", StringComparison.Ordinal))
            return $"content path is not normalized below the root: {Go.Quote(value)}";
        // Extra per-segment check, kept from the Go code as a second safety net.
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
        // Same basic safety rules as content paths: relative, forward slashes, valid UTF-8, bounded length.
        if (target.Length == 0 || Go.ByteLength(target) > MaxPathBytes || !Go.ValidString(target) || target.Contains('\0') ||
            target.Contains('\\') || target.StartsWith('/'))
            return $"{name} has unsafe link target {Go.Quote(target)}";
        // Resolve the target relative to the link's own folder, e.g. link "a/b" -> "../c" resolves to "c".
        // If the result climbs out of the root, the link could reach files outside the suite.
        var resolved = Go.CleanPath(Go.PathJoin(Go.PathDir(name), target));
        if (resolved is "." or ".." || resolved.StartsWith("../", StringComparison.Ordinal))
            return $"{name} link target escapes the content root";
        return null;
    }
}
