using System.Text;
using Agentweaver.Projects.Config.AgentSuite;
using Xunit;

namespace Agentweaver.Projects.Config.Tests.AgentSuite;

public sealed class AgentSuiteTarTests
{
    [Theory]
    [InlineData("mode", 100)]
    [InlineData("uid", 108)]
    [InlineData("gid", 116)]
    [InlineData("size", 124)]
    [InlineData("mtime", 136)]
    [InlineData("device major", 329)]
    [InlineData("device minor", 337)]
    public void LeadingPlusInOctalHeaderFieldIsRejected(string field, int offset)
    {
        var archive = MutateHeader(SingleEntryTar(), "entry", header =>
        {
            if (field.StartsWith("device", StringComparison.Ordinal))
                "0000000\0"u8.CopyTo(header.AsSpan(offset, 8));
            Assert.True(header[offset] == (byte)'0', $"{field} starts with zero before mutation.");
            header[offset] = (byte)'+';
        });
        var reader = new GoTarReader(new GoReader(new MemoryStream(archive, writable: false)));

        Assert.Equal(GoTarReader.ErrHeader, reader.Next(out var parsed));
        Assert.Null(parsed);
        Assert.Equal(GoTarReader.ErrHeader, reader.Next(out parsed));
        Assert.Null(parsed);
    }

    [Fact]
    public void LeadingPlusInChecksumIsRejectedWhenChecksumValueIsCorrect()
    {
        var archive = SingleEntryTar();
        Assert.Equal((byte)'0', archive[148]);
        // Checksum bytes count as spaces, so this preserves the correct checksum value.
        archive[148] = (byte)'+';
        var reader = new GoTarReader(new GoReader(new MemoryStream(archive, writable: false)));

        Assert.Equal(GoTarReader.ErrHeader, reader.Next(out var parsed));
        Assert.Null(parsed);
    }

    [Fact]
    public void PaddedOctalEmptyFieldBinaryUidAndSignedPaxUidKeepTheirRules()
    {
        var padded = ReadHeader(SingleEntryTar());
        Assert.Equal(0x1A4, padded.Mode);

        var emptyUid = MutateHeader(SingleEntryTar(), "entry", header =>
            "        "u8.CopyTo(header.AsSpan(108, 8)));
        Assert.Equal(0, ReadHeader(emptyUid).Uid);

        var binaryUid = MutateHeader(SingleEntryTar(), "entry", header =>
        {
            Array.Clear(header, 108, 8);
            header[108] = 0x80;
            header[115] = 7;
        });
        Assert.Equal(7, ReadHeader(binaryUid).Uid);

        var pax = AgentSuiteArchives.Tar(
        [
            TarEntry.File("entry", "x"u8.ToArray()) with
            {
                Pax = new Dictionary<string, string>(StringComparer.Ordinal) { ["uid"] = "+0" },
            },
        ]);
        pax = MutateHeader(pax, "entry", header =>
            "0000007\0"u8.CopyTo(header.AsSpan(108, 8)));
        Assert.Equal(0, ReadHeader(pax).Uid);
    }

    [Fact]
    public void LeadingPlusInContentLayerUidRejectsTheWholeLayer()
    {
        var tar = AgentSuiteArchives.Tar(AgentSuiteContentLayerTests.MinimalEntries());
        tar = MutateHeader(tar, "agentsuite.json", header => SetLeadingPlus(header, 108));
        var layer = AgentSuiteArchives.Gzip(tar);

        var result = AgentSuiteValidator.Validate(AgentSuiteArchiveFormat.ContentTarGzip, layer);

        Assert.Equal(["read content tar: archive/tar: invalid header"], result.NativeErrors.ToArray());
        Assert.Null(result.Graph);
        Assert.Empty(result.Blobs);
    }

    [Fact]
    public void LeadingPlusInOuterLayoutUidRejectsTheWholeArchive()
    {
        var files = AgentSuiteArchives.ReadDirectory(Path.Combine(AgentSuiteArchives.FixturesRoot, "valid"));
        var entries = AgentSuiteArchives.Entries(files);
        var tar = AgentSuiteArchives.Tar(entries);
        tar = MutateHeader(tar, entries[0].Path, header => SetLeadingPlus(header, 108));

        var result = AgentSuiteValidator.Validate(AgentSuiteArchiveFormat.OciLayoutTar, tar);

        var error = Assert.Single(result.ProfileErrors);
        Assert.Equal(AgentSuiteValidator.ArchiveInvalidCode, error.Code);
        Assert.Equal("The archive is not a valid tar file: archive/tar: invalid header.", error.Message);
        Assert.Null(result.Graph);
        Assert.Empty(result.Blobs);
    }

    private static byte[] SingleEntryTar() =>
        AgentSuiteArchives.Tar([TarEntry.File("entry", "x"u8.ToArray())]);

    private static TarHeader ReadHeader(byte[] archive)
    {
        var reader = new GoTarReader(new GoReader(new MemoryStream(archive, writable: false)));
        Assert.Null(reader.Next(out var header));
        return Assert.IsType<TarHeader>(header);
    }

    private static byte[] MutateHeader(byte[] archive, string entryName, Action<byte[]> mutate)
    {
        var result = archive.ToArray();
        for (var offset = 0; offset + 512 <= result.Length;)
        {
            var header = result.AsSpan(offset, 512).ToArray();
            if (header.All(static value => value == 0))
                break;
            var nul = Array.IndexOf(header, (byte)0, 0, 100);
            var name = Encoding.ASCII.GetString(header, 0, nul < 0 ? 100 : nul);
            var sizeText = Encoding.ASCII.GetString(header, 124, 12).Trim(' ', '\0');
            var size = Convert.ToInt64(sizeText, 8);
            if (name == entryName)
            {
                mutate(header);
                RepairChecksum(header);
                Array.Copy(header, 0, result, offset, 512);
                return result;
            }
            offset += 512 + checked((int)((size + 511) / 512) * 512);
        }
        throw new InvalidOperationException($"Tar entry {entryName} was not found.");
    }

    private static void SetLeadingPlus(byte[] header, int fieldOffset)
    {
        Assert.Equal((byte)'0', header[fieldOffset]);
        header[fieldOffset] = (byte)'+';
    }

    private static void RepairChecksum(byte[] header)
    {
        var checksum = 0;
        for (var index = 0; index < header.Length; index++)
            checksum += index is >= 148 and < 156 ? (byte)' ' : header[index];

        var octal = Convert.ToString(checksum, 8).PadLeft(6, '0');
        Encoding.ASCII.GetBytes(octal).CopyTo(header, 148);
        header[154] = 0;
        header[155] = (byte)' ';
    }
}
