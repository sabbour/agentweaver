using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Agentweaver.Cli.Vault;

internal sealed class WindowsCredentialVault : ICliCredentialVault
{
    private const uint GenericCredential = 1;
    private const uint PersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int MaximumBlobBytes = 5 * 512;
    private const string Service = "Agentweaver.Cli.OAuth.v1";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public string? Get(string account)
    {
        if (!CredReadW(Target(account), GenericCredential, 0, out var credential))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) return null;
            throw new CliVaultException("Windows Credential Manager could not read the CLI sign-in.");
        }

        try
        {
            var native = Marshal.PtrToStructure<NativeCredential>(credential);
            if (native.CredentialBlobSize is 0 or > MaximumBlobBytes ||
                native.CredentialBlob == IntPtr.Zero)
                throw new CliVaultException("The saved CLI sign-in is invalid or exceeds the vault limit.");

            var bytes = new byte[(int)native.CredentialBlobSize];
            try
            {
                Marshal.Copy(native.CredentialBlob, bytes, 0, bytes.Length);
                return StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw new CliVaultException("The saved CLI sign-in is invalid.", exception);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        finally
        {
            CredFree(credential);
        }
    }

    public void Set(string account, string refreshToken)
    {
        var bytes = StrictUtf8.GetBytes(refreshToken);
        if (bytes.Length is 0 or > MaximumBlobBytes)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new CliVaultException("The refresh token exceeds the Windows credential-vault limit.");
        }

        var blob = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        var target = Marshal.StringToHGlobalUni(Target(account));
        try
        {
            var credential = new NativeCredential
            {
                Type = GenericCredential,
                TargetName = target,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob.AddrOfPinnedObject(),
                Persist = PersistLocalMachine,
            };
            if (!CredWriteW(ref credential, 0))
                throw new CliVaultException("Windows Credential Manager could not save the CLI sign-in.");
        }
        finally
        {
            Marshal.FreeHGlobal(target);
            blob.Free();
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public void Delete(string account)
    {
        if (CredDeleteW(Target(account), GenericCredential, 0)) return;
        if (Marshal.GetLastWin32Error() != ErrorNotFound)
            throw new CliVaultException("Windows Credential Manager could not remove the CLI sign-in.");
    }

    private static string Target(string account) => $"{Service}:{account}";

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredReadW(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWriteW(ref NativeCredential credential, uint flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDeleteW(string target, uint type, uint flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr buffer);
}
