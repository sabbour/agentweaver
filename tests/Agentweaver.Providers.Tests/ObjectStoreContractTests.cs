using Agentweaver.Abstractions;
using Xunit;

namespace Agentweaver.Providers.Tests;

public sealed class ObjectStoreContractTests
{
    [Theory]
    [InlineData("checkpoints/run-42/payload")]
    [InlineData("one")]
    [InlineData("artifacts/file.bin")]
    public void AcceptsRelativeOpaqueKeys(string value)
    {
        var key = new ObjectKey(value);
        Assert.Equal(value, key.Value);
        Assert.Equal(value, key.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("../escape")]
    [InlineData("a/../b")]
    [InlineData("a/./b")]
    [InlineData("a//b")]
    [InlineData("/root")]
    [InlineData("end/")]
    [InlineData("a\\b")]
    [InlineData("a\nb")]
    public void RejectsUnsafeKeys(string value) =>
        Assert.ThrowsAny<ArgumentException>(() => new ObjectKey(value));

    [Fact]
    public void RejectsNullAndOversizedKeys()
    {
        Assert.Throws<ArgumentNullException>(() => new ObjectKey(null!));
        Assert.Throws<ArgumentException>(() => new ObjectKey(new string('a', 1025)));
        Assert.Equal(1024, new ObjectKey(new string('a', 1024)).Value.Length);
    }

    [Fact]
    public void ReadOwnsTransportDisposalAndPreservesContentAndLength()
    {
        using var content = new MemoryStream([0, 255, 42]);
        var disposed = false;
        var read = new ObjectRead(content, 3, () =>
        {
            content.Dispose();
            disposed = true;
        });

        Assert.Equal(3, read.Length);
        Assert.Same(content, read.Content);
        Assert.Equal(0, read.Content.ReadByte());
        Assert.False(disposed);
        read.Dispose();
        Assert.True(disposed);
        Assert.Throws<ObjectDisposedException>(() => read.Content.ReadByte());
    }

    [Fact]
    public void ReadRequiresValidTransportAndLength()
    {
        using var content = new MemoryStream();
        Assert.Throws<ArgumentNullException>(() => new ObjectRead(null!, 0, () => { }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ObjectRead(content, -1, () => { }));
        Assert.Throws<ArgumentNullException>(() => new ObjectRead(content, 0, null!));
    }
}
