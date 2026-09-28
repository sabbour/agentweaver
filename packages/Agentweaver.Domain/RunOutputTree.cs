using System.Text;

namespace Agentweaver.Domain;

/// <summary>Canonical, immutable Git tree contents retained with a run output revision.</summary>
public sealed class RunOutputTree
{
    private static readonly byte[] Magic = "AWTREE1\0"u8.ToArray();
    public sealed record File(string Path, int Mode, byte[] Bytes);

    public static byte[] Encode(IEnumerable<File> files)
    {
        var ordered = files.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(ordered.Length);
        string? previous = null;
        foreach (var file in ordered)
        {
            ValidatePath(file.Path, previous);
            if (file.Mode is not (33188 or 33261 or 40960) || file.Bytes is null)
                throw new RunOutputRevisionUnavailableException("invalid_tree_content");
            var path = Encoding.UTF8.GetBytes(file.Path);
            writer.Write(path.Length);
            writer.Write(path);
            writer.Write(file.Mode);
            writer.Write(file.Bytes.Length);
            writer.Write(file.Bytes);
            previous = file.Path;
        }
        return stream.ToArray();
    }

    public static IReadOnlyList<File> Decode(byte[]? contents)
    {
        if (contents is null)
            throw new RunOutputRevisionUnavailableException("missing_content");
        try
        {
            using var stream = new MemoryStream(contents, writable: false);
            using var reader = new BinaryReader(stream, new UTF8Encoding(false, true));
            if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic))
                throw new RunOutputRevisionUnavailableException("corrupt_content");
            var count = reader.ReadInt32();
            if (count < 0 || count > contents.Length / 14)
                throw new RunOutputRevisionUnavailableException("corrupt_content");
            var files = new List<File>(count);
            string? previous = null;
            for (var i = 0; i < count; i++)
            {
                var length = reader.ReadInt32();
                if (length < 1 || length > stream.Length - stream.Position)
                    throw new RunOutputRevisionUnavailableException("corrupt_content");
                var pathBytes = reader.ReadBytes(length);
                if (pathBytes.Length != length)
                    throw new RunOutputRevisionUnavailableException("corrupt_content");
                var path = new UTF8Encoding(false, true).GetString(pathBytes);
                ValidatePath(path, previous);
                var mode = reader.ReadInt32();
                var size = reader.ReadInt32();
                if (mode is not (33188 or 33261 or 40960) || size < 0 || size > stream.Length - stream.Position)
                    throw new RunOutputRevisionUnavailableException("corrupt_content");
                var bytes = reader.ReadBytes(size);
                if (bytes.Length != size)
                    throw new RunOutputRevisionUnavailableException("corrupt_content");
                files.Add(new File(path, mode, bytes));
                previous = path;
            }
            if (stream.Position != stream.Length)
                throw new RunOutputRevisionUnavailableException("corrupt_content");
            return files;
        }
        catch (Exception ex) when (ex is EndOfStreamException or DecoderFallbackException or ArgumentException)
        {
            throw new RunOutputRevisionUnavailableException("corrupt_content");
        }
    }

    private static void ValidatePath(string path, string? previous)
    {
        if (string.IsNullOrEmpty(path) || path.StartsWith('/')
            || path.Contains('\\') || path.Split('/').Any(part => part is "" or "." or "..")
            || (previous is not null && StringComparer.Ordinal.Compare(previous, path) >= 0))
            throw new RunOutputRevisionUnavailableException("corrupt_content");
    }
}
