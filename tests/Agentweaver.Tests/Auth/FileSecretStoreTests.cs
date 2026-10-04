using Agentweaver.Api.Auth;
using FluentAssertions;

namespace Agentweaver.Tests.Auth;

public sealed class FileSecretStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "agentweaver-file-secret-store-tests", Guid.NewGuid().ToString("n"));

    [Fact]
    public async Task FileSecretStore_PersistsSecretsAcrossInstances()
    {
        var first = new FileSecretStore(root);
        await first.SetSecretAsync("byok-provider-configurations", """{"active_provider_id":"p1"}""");

        var second = new FileSecretStore(root);
        var read = await second.GetSecretAsync("byok-provider-configurations");

        read.Found.Should().BeTrue();
        read.Value.Should().Be("""{"active_provider_id":"p1"}""");
    }

    [Fact]
    public async Task FileSecretStore_RejectsStaleEtag()
    {
        var store = new FileSecretStore(root);
        var etag = await store.SetSecretAsync("secret", "first");
        await store.SetSecretAsync("secret", "second", etag);

        var act = () => store.SetSecretAsync("secret", "stale", etag);

        await act.Should().ThrowAsync<SecretPreconditionFailedException>();
        (await store.GetSecretAsync("secret")).Value.Should().Be("second");
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
