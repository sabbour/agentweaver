using System.Text;

namespace Agentweaver.Projects.Config.AgentSuite;

/// <summary>Thrown when decompressed or archive data exceeds the Agentweaver hard read bound.</summary>
internal sealed class AgentSuiteLimitException(string message) : Exception(message);

/// <summary>A Go <c>io.Reader</c> view of a stream with Go error strings ("EOF", "unexpected EOF").</summary>
internal sealed class GoReader(Stream stream, long maxBytes = long.MaxValue)
{
    public const string Eof = "EOF";
    public const string UnexpectedEof = "unexpected EOF";

    // .NET does not expose Go's flate error offsets; corrupt compressed data maps to this text.
    public const string CorruptInput = "flate: corrupt input";

    private long _total;

    public int Read(Span<byte> buffer, out string? error)
    {
        error = null;
        if (buffer.IsEmpty)
            return 0;
        int read;
        try
        {
            read = stream.Read(buffer);
        }
        catch (InvalidDataException)
        {
            error = CorruptInput;
            return 0;
        }
        _total += read;
        if (_total > maxBytes)
            throw new AgentSuiteLimitException($"Content expands beyond {maxBytes} bytes.");
        if (read == 0)
            error = Eof;
        return read;
    }

    /// <summary>Go <c>io.ReadFull</c>.</summary>
    public string? ReadFull(Span<byte> buffer, out int count)
    {
        count = 0;
        while (count < buffer.Length)
        {
            var read = Read(buffer[count..], out var error);
            count += read;
            if (error is not null)
            {
                if (count == buffer.Length)
                    return null;
                return error == Eof ? (count == 0 ? Eof : UnexpectedEof) : error;
            }
        }
        return null;
    }

    /// <summary>The archive/tar <c>tryReadFull</c>: like ReadFull, but a short read keeps <c>EOF</c>.</summary>
    public string? TryReadFull(Span<byte> buffer)
    {
        var count = 0;
        while (count < buffer.Length)
        {
            var read = Read(buffer[count..], out var error);
            count += read;
            if (error is not null)
                return count == buffer.Length ? null : error;
        }
        return null;
    }
}

/// <summary>One parsed tar header with Go <c>archive/tar</c> field semantics.</summary>
internal sealed class TarHeader
{
    public string Name { get; set; } = string.Empty;
    public string Linkname { get; set; } = string.Empty;
    public byte Typeflag { get; set; }
    public long Mode { get; set; }
    public long Uid { get; set; }
    public long Gid { get; set; }
    public long Size { get; set; }

    /// <summary>ModTime seconds and nanoseconds; <see cref="ModTimeZero"/> marks Go's zero <c>time.Time</c>.</summary>
    public long ModSeconds { get; set; }
    public long ModNanos { get; set; }
    public bool ModTimeZero { get; set; }
    public bool AccessTimeSet { get; set; }
    public bool ChangeTimeSet { get; set; }

    public bool ModTimeIsEpoch => !ModTimeZero && ModSeconds == 0 && ModNanos == 0;
}

/// <summary>
/// A port of Go <c>archive/tar.Reader</c> covering USTAR, PAX ('x'/'g'), GNU ('L'/'K') and STAR headers,
/// including its end-of-archive, padding, checksum and numeric parsing rules.
/// </summary>
internal sealed class GoTarReader(GoReader reader)
{
    public const byte TypeReg = (byte)'0';
    public const byte TypeRegA = 0;
    public const byte TypeLink = (byte)'1';
    public const byte TypeSymlink = (byte)'2';
    public const byte TypeChar = (byte)'3';
    public const byte TypeBlock = (byte)'4';
    public const byte TypeDir = (byte)'5';
    public const byte TypeFifo = (byte)'6';
    public const byte TypeXHeader = (byte)'x';
    public const byte TypeXGlobalHeader = (byte)'g';
    public const byte TypeGnuLongName = (byte)'L';
    public const byte TypeGnuLongLink = (byte)'K';

    public const string ErrHeader = "archive/tar: invalid header";
    public const string ErrFieldTooLong = "archive/tar: header field too long";
    private const int MaxSpecialFileSize = 1 << 20;

    private readonly byte[] _block = new byte[512];
    private long _remaining;
    private int _pad;
    private string? _error;

    /// <summary>Go <c>Reader.Next</c>. Returns <c>"EOF"</c> at the normal end of the archive.</summary>
    public string? Next(out TarHeader? header)
    {
        header = null;
        if (_error is not null)
            return _error;
        var error = NextCore(out header);
        _error = error;
        return error;
    }

    private string? NextCore(out TarHeader? result)
    {
        result = null;
        Dictionary<string, string>? pax = null;
        string? longName = null;
        string? longLink = null;
        while (true)
        {
            if (Discard(_remaining) is { } discardError)
                return discardError;
            _remaining = 0;
            if (_pad > 0 && reader.TryReadFull(new byte[_pad]) is { } padError)
                return padError;
            _pad = 0;
            if (ReadHeader(out var header) is { } headerError)
                return headerError;
            if (HandleRegularFile(header!) is { } regularError)
                return regularError;
            var flag = header!.Typeflag;
            if (flag is TypeXHeader or TypeXGlobalHeader)
            {
                if (ParsePax(out var records) is { } paxError)
                    return paxError;
                if (flag == TypeXGlobalHeader)
                {
                    if (MergePax(header, records!) is { } globalError)
                        return globalError;
                    result = new TarHeader { Name = header.Name, Typeflag = flag, ModTimeZero = true };
                    return null;
                }
                pax = records;
                continue;
            }
            if (flag is TypeGnuLongName or TypeGnuLongLink)
            {
                if (ReadSpecialFile(out var data) is { } specialError)
                    return specialError;
                var value = ParseString(data!);
                if (flag == TypeGnuLongName)
                    longName = value;
                else
                    longLink = value;
                continue;
            }
            if (pax is not null && MergePax(header, pax) is { } mergeError)
                return mergeError;
            if (!string.IsNullOrEmpty(longName))
                header.Name = longName;
            if (!string.IsNullOrEmpty(longLink))
                header.Linkname = longLink;
            if (header.Typeflag == TypeRegA)
                header.Typeflag = header.Name.EndsWith('/') ? TypeDir : TypeReg;
            if (HandleRegularFile(header) is { } finalError)
                return finalError;
            result = header;
            return null;
        }
    }

    /// <summary>Reads entry data with Go <c>regFileReader</c> semantics.</summary>
    public int Read(Span<byte> buffer, out string? error)
    {
        error = null;
        if (_error is not null)
        {
            error = _error;
            return 0;
        }
        if (_remaining == 0)
        {
            error = GoReader.Eof;
            return 0;
        }
        if (buffer.Length > _remaining)
            buffer = buffer[..(int)_remaining];
        var read = reader.Read(buffer, out var readError);
        _remaining -= read;
        if (readError is not null)
        {
            error = readError == GoReader.Eof && _remaining > 0 ? GoReader.UnexpectedEof : readError;
            if (error != GoReader.Eof)
                _error = error;
        }
        return read;
    }

    /// <summary>Go <c>io.ReadAll</c> over the current entry, bounded to <paramref name="limit"/> bytes.</summary>
    public string? ReadAll(long limit, out byte[] data)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[32 * 1024];
        while (buffer.Length < limit)
        {
            var size = (int)Math.Min(chunk.Length, limit - buffer.Length);
            var read = Read(chunk.AsSpan(0, size), out var error);
            buffer.Write(chunk, 0, read);
            if (error is not null)
            {
                data = buffer.ToArray();
                return error == GoReader.Eof ? null : error;
            }
        }
        data = buffer.ToArray();
        return null;
    }

    private string? Discard(long count)
    {
        var chunk = new byte[32 * 1024];
        while (count > 0)
        {
            var read = reader.Read(chunk.AsSpan(0, (int)Math.Min(chunk.Length, count)), out var error);
            count -= read;
            if (error is not null && count > 0)
                return error == GoReader.Eof ? GoReader.UnexpectedEof : error;
        }
        return null;
    }

    private string? HandleRegularFile(TarHeader header)
    {
        var size = header.Size;
        if (header.Typeflag is TypeLink or TypeSymlink or TypeChar or TypeBlock or TypeDir or TypeFifo)
            size = 0;
        if (size < 0)
            return ErrHeader;
        _pad = (int)(-size & 511);
        _remaining = size;
        return null;
    }

    private string? ReadSpecialFile(out byte[]? data)
    {
        var error = ReadAll(MaxSpecialFileSize + 1, out var buffer);
        data = buffer;
        if (buffer.Length > MaxSpecialFileSize)
            return ErrFieldTooLong;
        return error;
    }

    private string? ParsePax(out Dictionary<string, string>? records)
    {
        records = null;
        if (ReadSpecialFile(out var data) is { } error)
            return error;
        var text = Go.Decode(data!);
        var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
        while (text.Length > 0)
        {
            if (!ParsePaxRecord(text, out var key, out var value, out var rest))
                return ErrHeader;
            text = rest;
            parsed[key] = value;
        }
        records = parsed;
        return null;
    }

    private static bool ParsePaxRecord(string text, out string key, out string value, out string rest)
    {
        key = value = rest = string.Empty;
        var bytes = Go.Bytes(text);
        var space = Array.IndexOf(bytes, (byte)' ');
        if (space < 0)
            return false;
        var lengthText = Encoding.Latin1.GetString(bytes, 0, space);
        if (!long.TryParse(lengthText, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var n) ||
            n < 5 || n > bytes.Length)
            return false;
        n -= space + 1;
        if (n <= 0)
            return false;
        var recordStart = space + 1;
        var record = bytes.AsSpan(recordStart, (int)n - 1);
        if (bytes[recordStart + (int)n - 1] != '\n')
            return false;
        var equals = record.IndexOf((byte)'=');
        if (equals < 0)
            return false;
        key = Go.Decode(record[..equals]);
        value = Go.Decode(record[(equals + 1)..]);
        rest = Go.Decode(bytes.AsSpan(recordStart + (int)n));
        if (key.Length == 0 || key.Contains('='))
            return false;
        return key is "path" or "linkpath" or "uname" or "gname" ? !value.Contains('\0') : !key.Contains('\0');
    }

    private static string? MergePax(TarHeader header, Dictionary<string, string> records)
    {
        foreach (var (key, value) in records)
        {
            if (value.Length == 0)
                continue;
            var ok = true;
            switch (key)
            {
                case "path": header.Name = value; break;
                case "linkpath": header.Linkname = value; break;
                case "uid": ok = ParseGoInt(value, out var uid); header.Uid = uid; break;
                case "gid": ok = ParseGoInt(value, out var gid); header.Gid = gid; break;
                case "atime": ok = ParsePaxTime(value, out _, out _); header.AccessTimeSet = ok; break;
                case "ctime": ok = ParsePaxTime(value, out _, out _); header.ChangeTimeSet = ok; break;
                case "mtime":
                    ok = ParsePaxTime(value, out var seconds, out var nanos);
                    header.ModSeconds = seconds;
                    header.ModNanos = nanos;
                    header.ModTimeZero = !ok;
                    break;
                case "size": ok = ParseGoInt(value, out var size); header.Size = size; break;
            }
            if (!ok)
                return ErrHeader;
        }
        return null;
    }

    private static bool ParseGoInt(string value, out long result) =>
        long.TryParse(value, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out result);

    /// <summary>Go <c>parsePAXTime</c>, normalized like <c>time.Unix(sec, nsec)</c>.</summary>
    private static bool ParsePaxTime(string value, out long seconds, out long nanos)
    {
        nanos = 0;
        var dot = value.IndexOf('.');
        var secondsText = dot < 0 ? value : value[..dot];
        var fraction = dot < 0 ? string.Empty : value[(dot + 1)..];
        if (!ParseGoInt(secondsText, out seconds))
            return false;
        if (fraction.Length == 0)
            return true;
        if (fraction.Any(c => c < '0' || c > '9'))
            return false;
        fraction = fraction.Length < 9 ? fraction.PadRight(9, '0') : fraction[..9];
        nanos = long.Parse(fraction, System.Globalization.CultureInfo.InvariantCulture);
        if (secondsText.StartsWith('-'))
            nanos = -nanos;
        if (nanos < 0)
        {
            seconds -= 1;
            nanos += 1_000_000_000;
        }
        return true;
    }

    private string? ReadHeader(out TarHeader? header)
    {
        header = null;
        if (reader.ReadFull(_block, out _) is { } error)
            return error;
        if (_block.All(b => b == 0))
        {
            if (reader.ReadFull(_block, out _) is { } secondError)
                return secondError;
            return _block.All(b => b == 0) ? GoReader.Eof : ErrHeader;
        }
        var parser = new FieldParser();
        var block = _block.AsSpan();
        var checksum = parser.ParseOctal(block[148..156]);
        var (unsigned, signed) = Checksums(block);
        if (parser.Failed || (checksum != unsigned && checksum != signed))
            return ErrHeader;
        var magic = block[257..263];
        var version = block[263..265];
        var trailer = block[508..512];
        var isUstar = magic.SequenceEqual("ustar\0"u8);
        var format = isUstar && trailer.SequenceEqual("tar\0"u8) ? 3
            : isUstar ? 2
            : magic.SequenceEqual("ustar "u8) && version.SequenceEqual(" \0"u8) ? 4
            : 1;

        var result = new TarHeader
        {
            Typeflag = block[156],
            Name = ParseString(block[..100]),
            Linkname = ParseString(block[157..257]),
            Size = parser.ParseNumeric(block[124..136]),
            Mode = parser.ParseNumeric(block[100..108]),
            Uid = parser.ParseNumeric(block[108..116]),
            Gid = parser.ParseNumeric(block[116..124]),
            ModSeconds = parser.ParseNumeric(block[136..148]),
        };
        if (format > 1)
        {
            parser.ParseNumeric(block[329..337]);
            parser.ParseNumeric(block[337..345]);
            var prefix = string.Empty;
            switch (format)
            {
                case 2:
                    prefix = ParseString(block[345..500]);
                    break;
                case 3:
                    prefix = ParseString(block[345..476]);
                    parser.ParseNumeric(block[476..488]);
                    parser.ParseNumeric(block[488..500]);
                    result.AccessTimeSet = true;
                    result.ChangeTimeSet = true;
                    break;
                case 4:
                {
                    var gnu = new FieldParser();
                    var access = block[345] != 0;
                    var change = block[357] != 0;
                    if (access)
                        gnu.ParseNumeric(block[345..357]);
                    if (change)
                        gnu.ParseNumeric(block[357..369]);
                    if (!gnu.Failed)
                    {
                        result.AccessTimeSet = access;
                        result.ChangeTimeSet = change;
                    }
                    break;
                }
            }
            if (prefix.Length > 0)
                result.Name = prefix + "/" + result.Name;
        }
        if (parser.Failed)
            return ErrHeader;
        header = result;
        return null;
    }

    private static (long Unsigned, long Signed) Checksums(ReadOnlySpan<byte> block)
    {
        long unsigned = 0, signed = 0;
        for (var index = 0; index < block.Length; index++)
        {
            var c = index is >= 148 and < 156 ? (byte)' ' : block[index];
            unsigned += c;
            signed += (sbyte)c;
        }
        return (unsigned, signed);
    }

    private static string ParseString(ReadOnlySpan<byte> field)
    {
        var nul = field.IndexOf((byte)0);
        return Go.Decode(nul >= 0 ? field[..nul] : field);
    }

    private sealed class FieldParser
    {
        public bool Failed { get; private set; }

        public long ParseNumeric(ReadOnlySpan<byte> field)
        {
            if (field.Length > 0 && (field[0] & 0x80) != 0)
            {
                var inverse = (field[0] & 0x40) != 0 ? (byte)0xff : (byte)0;
                ulong value = 0;
                for (var index = 0; index < field.Length; index++)
                {
                    var c = (byte)(field[index] ^ inverse);
                    if (index == 0)
                        c &= 0x7f;
                    if (value >> 56 > 0)
                    {
                        Failed = true;
                        return 0;
                    }
                    value = value << 8 | c;
                }
                if (value >> 63 > 0)
                {
                    Failed = true;
                    return 0;
                }
                return inverse == 0xff ? ~(long)value : (long)value;
            }
            return ParseOctal(field);
        }

        public long ParseOctal(ReadOnlySpan<byte> field)
        {
            var start = 0;
            var end = field.Length;
            while (start < end && field[start] is (byte)' ' or 0)
                start++;
            while (end > start && field[end - 1] is (byte)' ' or 0)
                end--;
            if (start == end)
                return 0;
            var trimmed = field[start..end];
            var nul = trimmed.IndexOf((byte)0);
            if (nul >= 0)
                trimmed = trimmed[..nul];
            ulong value = 0;
            var digits = trimmed;
            if (digits.Length > 0 && digits[0] == '+')
                digits = digits[1..];
            if (digits.Length == 0)
            {
                Failed = true;
                return 0;
            }
            foreach (var c in digits)
            {
                if (c < '0' || c > '7' || value >> 61 != 0)
                {
                    Failed = true;
                    return 0;
                }
                value = value << 3 | (uint)(c - '0');
            }
            return (long)value;
        }
    }
}
