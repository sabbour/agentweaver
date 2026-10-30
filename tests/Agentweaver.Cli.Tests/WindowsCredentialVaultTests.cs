using System.Security.Cryptography;
using Agentweaver.Cli.Vault;

namespace Agentweaver.Cli.Tests;

public sealed class WindowsCredentialVaultTests
{
    [Fact]
    public void WindowsCredentialManagerRoundTripsAndDeletesRefreshCredential()
    {
        if (!OperatingSystem.IsWindows()) return;

        var account = "agentweaver-v1-" +
            Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray())).ToLowerInvariant();
        var first = "test-only-refresh-" + Guid.NewGuid().ToString("N");
        var rotated = "test-only-rotated-" + Guid.NewGuid().ToString("N");
        var vault = new WindowsCredentialVault();
        try
        {
            Assert.Null(vault.Get(account));
            vault.Set(account, first);
            Assert.Equal(first, vault.Get(account));
            vault.Set(account, rotated);
            Assert.Equal(rotated, vault.Get(account));
            vault.Delete(account);
            Assert.Null(vault.Get(account));
        }
        finally
        {
            vault.Delete(account);
        }
    }
}
