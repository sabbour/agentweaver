using System.Collections.Concurrent;
using System.Text;
using Agentweaver.Abstractions;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed class SkillContentObjectStoreTests
{
    [Fact]
    public async Task WriteAndRead_VerifiesExactImmutableContentAndResourceHashes()
    {
        var objectStore = new InMemoryObjectStore();
        var store = new SkillContentObjectStore(objectStore);
        var content = CreateContent();
        var key = new ObjectKey("skill-content/project/skill/revision-1");

        var reference = await store.WriteAsync(key, content);
        var read = await store.ReadVerifiedAsync(key, content.ContentDigest);

        Assert.Equal(content.ContentDigest, reference.ContentDigest);
        Assert.Equal(content.ContentDigest, read.ContentDigest);
        Assert.Equal(content.Instructions, read.Instructions);
        Assert.Equal(content.Description, read.Description);
        var expectedResource = Assert.Single(content.Resources);
        var actualResource = Assert.Single(read.Resources);
        Assert.Equal(expectedResource.RelativePath, actualResource.RelativePath);
        Assert.Equal(expectedResource.Sha256, actualResource.Sha256);
        Assert.Equal(expectedResource.Content.ToArray(), actualResource.Content.ToArray());
    }

    [Fact]
    public async Task Write_IsIdempotentWhenExistingObjectMatchesExpectedDigest()
    {
        var objectStore = new InMemoryObjectStore();
        var store = new SkillContentObjectStore(objectStore);
        var content = CreateContent();
        var key = new ObjectKey("skill-content/project/skill/revision-1");

        var first = await store.WriteAsync(key, content);
        var replay = await store.WriteAsync(key, content);

        Assert.Equal(first, replay);
        Assert.Equal(1, objectStore.WriteCount);
    }

    [Fact]
    public async Task ReadVerified_RejectsUnavailableAndCorruptObjects()
    {
        var objectStore = new InMemoryObjectStore();
        var store = new SkillContentObjectStore(objectStore);
        var key = new ObjectKey("skill-content/project/skill/revision-1");

        var unavailable = await Assert.ThrowsAsync<SkillContentObjectStoreException>(
            () => store.ReadVerifiedAsync(key, new string('a', 64)));
        Assert.Equal(SkillContentObjectStoreException.UnavailableCode, unavailable.Code);

        objectStore.PutRaw(key, Encoding.UTF8.GetBytes("{\"schemaVersion\":1}"));
        var corrupt = await Assert.ThrowsAsync<SkillContentObjectStoreException>(
            () => store.ReadVerifiedAsync(key, new string('a', 64)));
        Assert.Equal(SkillContentObjectStoreException.IntegrityErrorCode, corrupt.Code);
    }

    [Fact]
    public async Task ReadVerified_RejectsDigestMismatch()
    {
        var objectStore = new InMemoryObjectStore();
        var store = new SkillContentObjectStore(objectStore);
        var content = CreateContent();
        var key = new ObjectKey("skill-content/project/skill/revision-1");
        await store.WriteAsync(key, content);

        var exception = await Assert.ThrowsAsync<SkillContentObjectStoreException>(
            () => store.ReadVerifiedAsync(key, new string('a', 64)));

        Assert.Equal(SkillContentObjectStoreException.IntegrityErrorCode, exception.Code);
    }

    private static ValidatedSkillContent CreateContent() =>
        SkillContentValidator.Validate(
            Encoding.UTF8.GetBytes(
                """
                ---
                name: content-reader
                description: Read immutable project skill content.
                ---
                Use the exact assigned revision and read the reference resource.
                """),
            [
                new SkillContentResourceInput(
                    "docs/reference.md",
                    Encoding.UTF8.GetBytes("# Reference\n")),
            ]);

    private sealed class InMemoryObjectStore : IObjectStore
    {
        private readonly ConcurrentDictionary<string, byte[]> _objects = new(StringComparer.Ordinal);

        public int WriteCount { get; private set; }

        public Task WriteAsync(ObjectKey key, Stream content, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var buffer = new MemoryStream();
            content.CopyTo(buffer);
            if (!_objects.TryAdd(key.Value, buffer.ToArray()))
                throw new InvalidOperationException("Object already exists.");
            WriteCount++;
            return Task.CompletedTask;
        }

        public Task<ObjectRead?> ReadAsync(ObjectKey key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_objects.TryGetValue(key.Value, out var bytes))
                return Task.FromResult<ObjectRead?>(null);
            return Task.FromResult<ObjectRead?>(
                new ObjectRead(new MemoryStream(bytes, writable: false), bytes.Length, () => { }));
        }

        public Task<bool> DeleteAsync(ObjectKey key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_objects.TryRemove(key.Value, out _));
        }

        public void PutRaw(ObjectKey key, byte[] bytes) => _objects[key.Value] = bytes;
    }
}
