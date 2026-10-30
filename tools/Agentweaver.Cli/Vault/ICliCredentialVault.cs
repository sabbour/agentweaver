namespace Agentweaver.Cli.Vault;

/// <summary>Stores opaque refresh credentials in the current user's native OS vault.</summary>
internal interface ICliCredentialVault
{
    /// <summary>Returns null only when no credential exists; vault access failures must remain errors.</summary>
    string? Get(string account);

    /// <summary>Replaces the credential for this identity without exposing it through diagnostics.</summary>
    void Set(string account, string refreshToken);

    /// <summary>Removes the credential; failure to confirm removal is not equivalent to signed out.</summary>
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
        // Fail closed when the native vault is unavailable; there is intentionally no plaintext-file fallback.
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
