using System.Security.Cryptography;
using Agentweaver.Cli.Vault;

namespace Agentweaver.Cli.Tests;

public sealed class LinuxSecretServiceVaultTests
{
    [Fact]
    public void LinuxSecretServiceStoresAndRepeatedlyDeletesOnlyItsTestCredential()
    {
        if (!OperatingSystem.IsLinux() ||
            Environment.GetEnvironmentVariable("AGENTWEAVER_TEST_SECRET_SERVICE") != "1")
            return;

        var account = "agentweaver-v1-" +
            Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray())).ToLowerInvariant();
        var token = "test-only-refresh-" + Guid.NewGuid().ToString("N");
        var vault = new LinuxSecretServiceVault();
        try
        {
            vault.Delete(account);
            vault.Delete(account);
            Assert.Null(vault.Get(account));

            vault.Set(account, token);
            Assert.Equal(token, vault.Get(account));
            vault.Delete(account);
            vault.Delete(account);

            Assert.Null(vault.Get(account));
        }
        finally
        {
            vault.Delete(account);
        }
    }
}
