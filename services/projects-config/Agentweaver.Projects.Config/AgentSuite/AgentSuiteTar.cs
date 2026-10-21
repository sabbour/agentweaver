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
        // Go readers may return 0 bytes with no error for an empty buffer; do not touch the stream.
        if (buffer.IsEmpty)
            return 0;
        int read;
        try
        {
            read = stream.Read(buffer);
        }
        catch (InvalidDataException)
        {
            // The .NET decompressor throws on bad deflate data; report it as the Go error text instead.
            error = CorruptInput;
            return 0;
        }
        // Count every byte read so far. Going past the limit stops the import (guards against
        // "zip bombs" where a small compressed file expands to a huge size).
        _total += read;
        if (_total > maxBytes)
            throw new AgentSuiteLimitException($"Content expands beyond {maxBytes} bytes.");
        // A .NET stream signals end-of-data by returning 0; Go signals it with the "EOF" error.
        if (read == 0)
            error = Eof;
        return read;
    }

    /// <summary>Go <c>io.ReadFull</c>.</summary>
    public string? ReadFull(Span<byte> buffer, out int count)
    {
        count = 0;
        // Keep reading until the buffer is full; a single Read may return fewer bytes.
        while (count < buffer.Length)
        {
            var read = Read(buffer[count..], out var error);
            count += read;
            if (error is not null)
            {
                // The buffer filled on the same call that hit the error: treat as success.
                if (count == buffer.Length)
                    return null;
                // Go rule: nothing read at all -> "EOF"; partly read -> "unexpected EOF".
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
            // Unlike ReadFull, a short read reports the raw error ("EOF" stays "EOF"),
            // which is what the Go tar reader returns for missing padding bytes.
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

    // True only when an mtime was actually set and equals 1970-01-01T00:00:00Z exactly.
    // Deterministic suite archives must use this value for every entry.
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

    // Error strings copied from Go so validation messages match the Go validator exactly.
    public const string ErrHeader = "archive/tar: invalid header";
    public const string ErrFieldTooLong = "archive/tar: header field too long";
    // Largest allowed body for metadata entries (PAX records, GNU long names): 1 MiB.
    private const int MaxSpecialFileSize = 1 << 20;

    // Tar is a sequence of 512-byte blocks; each header uses exactly one block.
    private readonly byte[] _block = new byte[512];
    // Bytes of the current entry's body not yet read by the caller.
    private long _remaining;
    // Zero bytes after the body that round it up to a multiple of 512.
    private int _pad;
    // First error seen; once set, every later call returns it (Go's "sticky" error).
    private string? _error;

    /// <summary>Go <c>Reader.Next</c>. Returns <c>"EOF"</c> at the normal end of the archive.</summary>
    public string? Next(out TarHeader? header)
    {
        header = null;
        // After any failure (or the end of the archive) the reader stays in that state.
        if (_error is not null)
            return _error;
        var error = NextCore(out header);
        _error = error;
        return error;
    }

    private string? NextCore(out TarHeader? result)
    {
        result = null;
        // Values from metadata entries that apply to the next real entry.
        Dictionary<string, string>? pax = null;
        string? longName = null;
        string? longLink = null;
        // Loop because metadata entries (PAX, GNU long names) are not returned to the caller;
        // they are consumed and then merged into the following real entry.
        while (true)
        {
            // Skip any body bytes the caller did not read from the previous entry.
            if (Discard(_remaining) is { } discardError)
                return discardError;
            _remaining = 0;
            // Skip the zero padding that rounds the previous body up to a 512-byte boundary.
            if (_pad > 0 && reader.TryReadFull(new byte[_pad]) is { } padError)
                return padError;
            _pad = 0;
            // Read the next 512-byte header (returns "EOF" on the two zero end blocks).
            if (ReadHeader(out var header) is { } headerError)
                return headerError;
            // Set up body size and padding so metadata bodies below can be read.
            if (HandleRegularFile(header!) is { } regularError)
                return regularError;
            var flag = header!.Typeflag;
            if (flag is TypeXHeader or TypeXGlobalHeader)
            {
                // PAX entry: its body holds "key=value" records that override header fields.
                if (ParsePax(out var records) is { } paxError)
                    return paxError;
                if (flag == TypeXGlobalHeader)
                {
                    // A global PAX header is returned to the caller as its own entry (as Go does)
                    // so the validator can reject it; its records are only checked, not kept.
                    if (MergePax(header, records!) is { } globalError)
                        return globalError;
                    result = new TarHeader { Name = header.Name, Typeflag = flag, ModTimeZero = true };
                    return null;
                }
                // A local PAX header applies only to the next entry.
                pax = records;
                continue;
            }
            if (flag is TypeGnuLongName or TypeGnuLongLink)
            {
                // GNU long-name entry: its body is the full name (or link target) of the next entry,
                // used when the name does not fit in the 100-byte header field.
                if (ReadSpecialFile(out var data) is { } specialError)
                    return specialError;
                var value = ParseString(data!);
                if (flag == TypeGnuLongName)
                    longName = value;
                else
                    longLink = value;
                continue;
            }
            // This is a real entry: apply pending PAX records and GNU long names to it.
            if (pax is not null && MergePax(header, pax) is { } mergeError)
                return mergeError;
            if (!string.IsNullOrEmpty(longName))
                header.Name = longName;
            if (!string.IsNullOrEmpty(longLink))
                header.Linkname = longLink;
            // Old tar files use type 0 (NUL) for files; a trailing '/' meant a directory.
            if (header.Typeflag == TypeRegA)
                header.Typeflag = header.Name.EndsWith('/') ? TypeDir : TypeReg;
            // Recompute body size because PAX "size" or the type change may have altered it.
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
        // A previous fatal error is returned again (sticky error).
        if (_error is not null)
        {
            error = _error;
            return 0;
        }
        // The current entry's body is fully read.
        if (_remaining == 0)
        {
            error = GoReader.Eof;
            return 0;
        }
        // Never read past the end of this entry's body into padding or the next header.
        if (buffer.Length > _remaining)
            buffer = buffer[..(int)_remaining];
        var read = reader.Read(buffer, out var readError);
        _remaining -= read;
        if (readError is not null)
        {
            // The stream ended before the size declared in the header: the archive is truncated.
            error = readError == GoReader.Eof && _remaining > 0 ? GoReader.UnexpectedEof : readError;
            // Real failures become sticky; a clean "EOF" for this entry does not.
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
        // Stop once the limit is reached; callers pass limit+1 to detect "too large".
        while (buffer.Length < limit)
        {
            var size = (int)Math.Min(chunk.Length, limit - buffer.Length);
            var read = Read(chunk.AsSpan(0, size), out var error);
            buffer.Write(chunk, 0, read);
            if (error is not null)
            {
                data = buffer.ToArray();
                // Reaching the end of the entry is the normal way to finish, so "EOF" is not an error.
                return error == GoReader.Eof ? null : error;
            }
        }
        data = buffer.ToArray();
        return null;
    }

    private string? Discard(long count)
    {
        // Read and throw away bytes in chunks (the stream may not support seeking).
        var chunk = new byte[32 * 1024];
        while (count > 0)
        {
            var read = reader.Read(chunk.AsSpan(0, (int)Math.Min(chunk.Length, count)), out var error);
            count -= read;
            // Running out of data before skipping everything means the archive is truncated.
            if (error is not null && count > 0)
                return error == GoReader.Eof ? GoReader.UnexpectedEof : error;
        }
        return null;
    }

    private string? HandleRegularFile(TarHeader header)
    {
        var size = header.Size;
        // Links, devices, directories and FIFOs never have a body, whatever the size field says.
        if (header.Typeflag is TypeLink or TypeSymlink or TypeChar or TypeBlock or TypeDir or TypeFifo)
            size = 0;
        if (size < 0)
            return ErrHeader;
        // Padding needed to reach the next multiple of 512: (-size) mod 512.
        _pad = (int)(-size & 511);
        _remaining = size;
        return null;
    }

    private string? ReadSpecialFile(out byte[]? data)
    {
        // Read one byte more than the limit so an oversized body can be detected.
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
        // Decode bytes one-to-one into chars (Go strings are raw bytes) so byte lengths stay exact.
        var text = Go.Decode(data!);
        var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
        // The body is a list of records; each call consumes one and returns the rest.
        while (text.Length > 0)
        {
            if (!ParsePaxRecord(text, out var key, out var value, out var rest))
                return ErrHeader;
            text = rest;
            // A repeated key overrides the earlier value, as in Go.
            parsed[key] = value;
        }
        records = parsed;
        return null;
    }

    private static bool ParsePaxRecord(string text, out string key, out string value, out string rest)
    {
        // A PAX record looks like "<length> <key>=<value>\n", where <length> is the decimal
        // byte count of the whole record, including the length digits and the newline.
        key = value = rest = string.Empty;
        var bytes = Go.Bytes(text);
        var space = Array.IndexOf(bytes, (byte)' ');
        if (space < 0)
            return false;
        var lengthText = Encoding.Latin1.GetString(bytes, 0, space);
        // 5 is the shortest possible record ("5 a=\n"); the record must also fit in the data.
        if (!long.TryParse(lengthText, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var n) ||
            n < 5 || n > bytes.Length)
            return false;
        // From here n is the length of "<key>=<value>\n" (the part after the space).
        n -= space + 1;
        if (n <= 0)
            return false;
        var recordStart = space + 1;
        var record = bytes.AsSpan(recordStart, (int)n - 1);
        // The declared length must end exactly on a newline.
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
        // NUL bytes are rejected in name-like values and in all keys (same check as Go).
        return key is "path" or "linkpath" or "uname" or "gname" ? !value.Contains('\0') : !key.Contains('\0');
    }

    private static string? MergePax(TarHeader header, Dictionary<string, string> records)
    {
        foreach (var (key, value) in records)
        {
            // An empty value means "unset this field"; Go ignores it here.
            if (value.Length == 0)
                continue;
            var ok = true;
            // Only keys that affect validation are applied; unknown keys are ignored like in Go.
            switch (key)
            {
                case "path": header.Name = value; break;
                case "linkpath": header.Linkname = value; break;
                case "uid": ok = ParseGoInt(value, out var uid); header.Uid = uid; break;
                case "gid": ok = ParseGoInt(value, out var gid); header.Gid = gid; break;
                // Access/change times are only recorded as "present"; suite archives must not have them.
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
            // Any value that fails to parse makes the whole header invalid.
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
        // Format is "<seconds>[.<fraction>]", e.g. "1700000000.5" or "-1.25".
        var dot = value.IndexOf('.');
        var secondsText = dot < 0 ? value : value[..dot];
        var fraction = dot < 0 ? string.Empty : value[(dot + 1)..];
        if (!ParseGoInt(secondsText, out seconds))
            return false;
        if (fraction.Length == 0)
            return true;
        if (fraction.Any(c => c < '0' || c > '9'))
            return false;
        // Convert the fraction to exactly 9 digits (nanoseconds): pad short ones, cut long ones.
        fraction = fraction.Length < 9 ? fraction.PadRight(9, '0') : fraction[..9];
        nanos = long.Parse(fraction, System.Globalization.CultureInfo.InvariantCulture);
        // For negative times the fraction is also negative ("-1.25" = -1 s - 0.25 s).
        if (secondsText.StartsWith('-'))
            nanos = -nanos;
        // Normalize so nanoseconds are always in [0, 1e9), borrowing one second if needed.
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
        // The archive ends with two all-zero 512-byte blocks.
        if (_block.All(b => b == 0))
        {
            // The second block must also be present and all zero; one zero block alone is invalid.
            if (reader.ReadFull(_block, out _) is { } secondError)
                return secondError;
            return _block.All(b => b == 0) ? GoReader.Eof : ErrHeader;
        }
        var parser = new FieldParser();
        var block = _block.AsSpan();
        // Bytes 148-155 hold the header checksum as octal text.
        var checksum = parser.ParseOctal(block[148..156]);
        // Old tar tools summed bytes as signed values, so accept either the unsigned or signed sum.
        var (unsigned, signed) = Checksums(block);
        if (parser.Failed || (checksum != unsigned && checksum != signed))
            return ErrHeader;
        // Detect the header format from the magic (257-262), version (263-264) and trailer (508-511):
        // 1 = old V7, 2 = POSIX USTAR, 3 = STAR (USTAR plus "tar\0" trailer), 4 = GNU.
        var magic = block[257..263];
        var version = block[263..265];
        var trailer = block[508..512];
        var isUstar = magic.SequenceEqual("ustar\0"u8);
        var format = isUstar && trailer.SequenceEqual("tar\0"u8) ? 3
            : isUstar ? 2
            : magic.SequenceEqual("ustar "u8) && version.SequenceEqual(" \0"u8) ? 4
            : 1;

        // Fields shared by all formats. Offsets: name 0-99, mode 100-107, uid 108-115,
        // gid 116-123, size 124-135, mtime 136-147, type 156, link name 157-256.
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
            // Device major/minor (329-344): parsed only so invalid numbers are rejected, as in Go.
            parser.ParseNumeric(block[329..337]);
            parser.ParseNumeric(block[337..345]);
            var prefix = string.Empty;
            switch (format)
            {
                case 2:
                    // USTAR: a 155-byte name prefix (345-499) that is joined to the name with '/'.
                    prefix = ParseString(block[345..500]);
                    break;
                case 3:
                    // STAR: a shorter prefix (345-475) followed by atime (476-487) and ctime (488-499).
                    // These time fields always exist in STAR, so the times count as set.
                    prefix = ParseString(block[345..476]);
                    parser.ParseNumeric(block[476..488]);
                    parser.ParseNumeric(block[488..500]);
                    result.AccessTimeSet = true;
                    result.ChangeTimeSet = true;
                    break;
                case 4:
                {
                    // GNU: optional atime (345-356) and ctime (357-368); a leading zero byte means "not set".
                    // A separate parser is used so bad GNU times are ignored instead of failing the header.
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
        // Any numeric field that failed to parse makes the header invalid.
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
            // The checksum field itself (148-155) is counted as eight spaces, per the tar spec.
            var c = index is >= 148 and < 156 ? (byte)' ' : block[index];
            unsigned += c;
            signed += (sbyte)c;
        }
        return (unsigned, signed);
    }

    private static string ParseString(ReadOnlySpan<byte> field)
    {
        // Text fields are NUL-terminated if shorter than the field; otherwise they fill it.
        var nul = field.IndexOf((byte)0);
        return Go.Decode(nul >= 0 ? field[..nul] : field);
    }

    // Parses numeric header fields and remembers any failure, so many fields can be parsed
    // and checked once at the end (same pattern as Go's tar "parser").
    private sealed class FieldParser
    {
        public bool Failed { get; private set; }

        public long ParseNumeric(ReadOnlySpan<byte> field)
        {
            // If the top bit of the first byte is set, the number is stored in binary ("base-256")
            // instead of octal text. This lets large values fit in small fields.
            if (field.Length > 0 && (field[0] & 0x80) != 0)
            {
                // Bit 0x40 in the first byte marks a negative number (two's complement);
                // XOR with 0xff flips the bits so the magnitude can be read the same way.
                var inverse = (field[0] & 0x40) != 0 ? (byte)0xff : (byte)0;
                ulong value = 0;
                for (var index = 0; index < field.Length; index++)
                {
                    var c = (byte)(field[index] ^ inverse);
                    // Drop the marker bit from the first byte.
                    if (index == 0)
                        c &= 0x7f;
                    // Shifting in another byte would overflow 64 bits.
                    if (value >> 56 > 0)
                    {
                        Failed = true;
                        return 0;
                    }
                    value = value << 8 | c;
                }
                // The result must fit in a signed 64-bit number.
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
            // Octal text may be padded with spaces or NULs on either side; trim them.
            var start = 0;
            var end = field.Length;
            while (start < end && field[start] is (byte)' ' or 0)
                start++;
            while (end > start && field[end - 1] is (byte)' ' or 0)
                end--;
            // An empty field means zero.
            if (start == end)
                return 0;
            var trimmed = field[start..end];
            var nul = trimmed.IndexOf((byte)0);
            if (nul >= 0)
                trimmed = trimmed[..nul];
            ulong value = 0;
            var digits = trimmed;
            // Go's unsigned number parser accepts one leading '+'.
            if (digits.Length > 0 && digits[0] == '+')
                digits = digits[1..];
            if (digits.Length == 0)
            {
                Failed = true;
                return 0;
            }
            foreach (var c in digits)
            {
                // Reject non-octal digits, and stop before the next shift by 3 bits overflows 64 bits.
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
