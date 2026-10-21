using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Agentweaver.Projects.Config.Tests.AgentSuite;

/// <summary>One hand-built tar entry. Type is the tar typeflag byte.</summary>
internal sealed record TarEntry(string Path, byte[] Data, char Type = '0', int Mode = 0x1A4)
{
    public string LinkName { get; init; } = string.Empty;
    public long ModTime { get; init; }
    public IReadOnlyDictionary<string, string>? Pax { get; init; }

    public static TarEntry File(string path, byte[] data) => new(path, data);

    public static TarEntry File(string path, string text) => new(path, Encoding.UTF8.GetBytes(text));

    // Default file mode 0x1A4 is octal 0644; directories use typeflag '5' and 0x1ED (octal 0755).
    public static TarEntry Directory(string path) => new(path.TrimEnd('/') + "/", [], '5', 0x1ED);
}

/// <summary>Deterministic ustar/gzip writers and fixture readers for AgentSuite tests.</summary>
internal static class AgentSuiteArchives
{
    public static string FixturesRoot => Path.Combine(AppContext.BaseDirectory, "Fixtures", "AgentSuite");

    public static string UpstreamRoot => Path.Combine(AppContext.BaseDirectory, "AgentSuite", "Upstream");

    public static string Digest(byte[] data) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(data));

    public static byte[] Tar(IEnumerable<TarEntry> entries, byte[]? trailing = null)
    {
        using var output = new MemoryStream();
        foreach (var entry in entries)
        {
            // Entries with PAX attributes get an extra 'x' header record in front of them.
            if (entry.Pax is { Count: > 0 } pax)
            {
                var records = new StringBuilder();
                foreach (var (key, value) in pax)
                {
                    // A PAX record is "<length> <key>=<value>\n" and the length counts its own digits,
                    // so repeat until the digit count stops changing.
                    var body = $" {key}={value}\n";
                    var length = body.Length + 1;
                    while ((length.ToString().Length + body.Length) != length)
                        length = length.ToString().Length + body.Length;
                    records.Append(length).Append(body);
                }
                var data = Encoding.UTF8.GetBytes(records.ToString());
                WriteHeader(output, "PaxHeaders/entry", 'x', 0x1A4, data.Length, 0, string.Empty);
                WriteData(output, data);
            }
            // When PAX supplies the real path (for example a path longer than 100 bytes),
            // the ustar header only needs a short placeholder name.
            var headerName = entry.Pax?.ContainsKey("path") == true ? "pax-path" : entry.Path;
            WriteHeader(output, headerName, entry.Type, entry.Mode, entry.Data.Length, entry.ModTime, entry.LinkName);
            WriteData(output, entry.Data);
        }
        // Two zero-filled 512-byte blocks mark the end of a tar archive.
        output.Write(new byte[1024]);
        // Optional bytes after the end marker let tests build archives with trailing garbage.
        if (trailing is not null)
            output.Write(trailing);
        return output.ToArray();
    }

    public static byte[] Gzip(byte[] data, uint modTime = 0)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(data);
        var bytes = output.ToArray();
        // Bytes 4-7 of the gzip header hold the modification time as a little-endian uint32.
        // Overwriting it lets tests produce both valid (0) and invalid (non-zero) layers.
        BitConverter.TryWriteBytes(bytes.AsSpan(4, 4), modTime);
        if (!BitConverter.IsLittleEndian)
            bytes.AsSpan(4, 4).Reverse();
        return bytes;
    }

    private static void WriteHeader(Stream output, string name, char type, int mode, long size, long modTime, string linkName)
    {
        var header = new byte[512];
        var nameBytes = Encoding.UTF8.GetBytes(name);
        if (nameBytes.Length > 100)
            throw new ArgumentException("Test tar names must fit the ustar name field.", nameof(name));
        nameBytes.CopyTo(header, 0);
        // Fixed ustar field offsets: mode, uid, gid, size, mtime, then the typeflag at 156
        // and the link name at 157.
        Octal(header, 100, 8, mode);
        Octal(header, 108, 8, 0);
        Octal(header, 116, 8, 0);
        Octal(header, 124, 12, size);
        Octal(header, 136, 12, modTime);
        header[156] = (byte)type;
        Encoding.UTF8.GetBytes(linkName).CopyTo(header, 157);
        // POSIX ustar magic and version.
        "ustar\0"u8.CopyTo(header.AsSpan(257));
        "00"u8.CopyTo(header.AsSpan(263));
        // The checksum is computed with its own 8-byte field filled with spaces,
        // then stored as six octal digits, a NUL and a space.
        for (var i = 148; i < 156; i++)
            header[i] = (byte)' ';
        var sum = header.Sum(value => value);
        Octal(header, 148, 7, sum);
        header[155] = (byte)' ';
        output.Write(header);
    }

    private static void Octal(byte[] header, int offset, int length, long value)
    {
        // Tar numeric fields are zero-padded octal text ending in a NUL byte.
        var text = Convert.ToString(value, 8).PadLeft(length - 1, '0');
        Encoding.ASCII.GetBytes(text).CopyTo(header, offset);
        header[offset + length - 1] = 0;
    }

    private static void WriteData(Stream output, byte[] data)
    {
        output.Write(data);
        // File data is padded with zeros up to the next 512-byte block boundary.
        var padding = (512 - data.Length % 512) % 512;
        output.Write(new byte[padding]);
    }

    /// <summary>Loads a layout or content directory as relative POSIX path to bytes.</summary>
    public static Dictionary<string, byte[]> ReadDirectory(string root)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in System.IO.Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            // Use forward slashes so keys match tar paths on every OS.
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            // Skip hidden files such as .gitkeep that are not part of the fixture.
            if (Path.GetFileName(relative).StartsWith('.'))
                continue;
            files[relative] = System.IO.File.ReadAllBytes(file);
        }
        return files;
    }

    /// <summary>Builds a deterministic tar of a directory map: implied directories first, sorted paths.</summary>
    public static List<TarEntry> Entries(IReadOnlyDictionary<string, byte[]> files)
    {
        var directories = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in files.Keys)
        {
            // Collect every parent directory of the file, e.g. "a/b/c.txt" adds "a/b" and "a".
            var slash = path.LastIndexOf('/');
            while (slash > 0)
            {
                directories.Add(path[..slash]);
                slash = path.LastIndexOf('/', slash - 1);
            }
        }
        var entries = directories.Select(TarEntry.Directory).ToList();
        entries.AddRange(files.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => TarEntry.File(pair.Key, pair.Value)));
        // Sort ignoring the trailing slash so a directory lands right before its own contents.
        return entries.OrderBy(entry => entry.Path.TrimEnd('/'), StringComparer.Ordinal).ToList();
    }

    public static byte[] DirectoryContentLayer(string root) => Gzip(Tar(Entries(ReadDirectory(root))));

    /// <summary>Reads the content layer of a fixture layout back into a mutable map of regular files.</summary>
    public static Dictionary<string, byte[]> FixtureContent(string fixture)
    {
        // Validate the layout to locate its single content layer blob.
        var layout = ReadDirectory(Path.Combine(FixturesRoot, fixture));
        var result = Agentweaver.Projects.Config.AgentSuite.AgentSuiteValidator.ValidateOciLayout(layout);
        var layer = result.Blobs.Single(blob => blob.MediaType == "application/vnd.agentsuite.content.v1.tar+gzip");
        // Unpack the layer with the framework tar reader, independent of the validator's own parser.
        using var gzip = new GZipStream(new MemoryStream(layer.Data.ToArray()), CompressionMode.Decompress);
        using var reader = new System.Formats.Tar.TarReader(gzip);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        while (reader.GetNextEntry() is { } entry)
        {
            // Only regular files carry content; directories and links are skipped.
            if (entry.EntryType is not (System.Formats.Tar.TarEntryType.RegularFile or System.Formats.Tar.TarEntryType.V7RegularFile))
                continue;
            using var data = new MemoryStream();
            entry.DataStream?.CopyTo(data);
            files[entry.Name] = data.ToArray();
        }
        return files;
    }
}
