namespace Agentweaver.Cli.Vault;

internal interface ICliCredentialVault
{
    string? Get(string account);
    void Set(string account, string refreshToken);
    void Delete(string account);
}

internal sealed class CliVaultException : Exception
{
    public CliVaultException(string message) : base(message) { }
    public CliVaultException(string message, Exception innerException) : base(message, innerException) { }
}

internal static class NativeCredentialVault
{
    public static ICliCredentialVault CreateDefault()
    {
        if (OperatingSystem.IsWindows()) return new WindowsCredentialVault();
        if (OperatingSystem.IsMacOS())
        {
            try { return new MacOsKeychainVault(); }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException)
            {
                throw new PlatformNotSupportedException(
                    "CLI sign-in requires the macOS Security.framework Keychain.", exception);
            }
        }
        if (OperatingSystem.IsLinux())
        {
            try { return new LinuxSecretServiceVault(); }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException)
            {
                throw new PlatformNotSupportedException(
                    "CLI sign-in requires libsecret and an unlocked Linux Secret Service session.", exception);
            }
        }
        throw new PlatformNotSupportedException(
            "CLI sign-in requires Windows Credential Manager, macOS Keychain, or Linux Secret Service.");
    }
}
