using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Agentweaver.Cli.Vault;

internal sealed class MacOsKeychainVault : ICliCredentialVault
{
    private const int ItemNotFound = -25300;
    private const int MaximumCredentialBytes = 16 * 1024;
    private const uint Utf8StringEncoding = 0x08000100;
    private const string Service = "Agentweaver.Cli.OAuth.v1";
    private static readonly IntPtr Security = NativeLibrary.Load(
        "/System/Library/Frameworks/Security.framework/Security");
    private static readonly IntPtr CoreFoundation = NativeLibrary.Load(
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation");
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public string? Get(string account)
    {
        using var query = new NativeDictionary();
        query.AddGlobal("kSecClass", "kSecClassGenericPassword");
        query.AddString("kSecAttrService", Service);
        query.AddString("kSecAttrAccount", account);
        query.AddGlobal("kSecMatchLimit", "kSecMatchLimitOne");
        query.AddGlobal("kSecReturnData", "kCFBooleanTrue");

        var status = SecItemCopyMatching(query.Handle, out var item);
        if (status == ItemNotFound) return null;
        if (status != 0)
            throw new CliVaultException("macOS Keychain could not read the CLI sign-in.");
        if (item == IntPtr.Zero)
            throw new CliVaultException("The saved CLI sign-in is invalid.");
        try
        {
            var length = CFDataGetLength(item);
            if (length is <= 0 or > MaximumCredentialBytes)
                throw new CliVaultException("The saved CLI sign-in is invalid or exceeds the vault limit.");

            var bytes = new byte[(int)length];
            try
            {
                var data = CFDataGetBytePtr(item);
                if (data == IntPtr.Zero)
                    throw new CliVaultException("The saved CLI sign-in is invalid.");
                Marshal.Copy(data, bytes, 0, bytes.Length);
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
            CFRelease(item);
        }
    }

    public void Set(string account, string refreshToken)
    {
        var bytes = StrictUtf8.GetBytes(refreshToken);
        if (bytes.Length is 0 or > MaximumCredentialBytes)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new CliVaultException("The refresh token exceeds the macOS Keychain limit.");
        }

        var buffer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            using var value = new NativeData(buffer, bytes.Length);
            using var query = CreateItemQuery(account);
            using var updates = new NativeDictionary();
            updates.AddData("kSecValueData", value.Handle);

            var status = SecItemUpdate(query.Handle, updates.Handle);
            if (status == ItemNotFound)
            {
                using var item = CreateItemQuery(account);
                item.AddData("kSecValueData", value.Handle);
                item.AddGlobal("kSecAttrAccessible", "kSecAttrAccessibleWhenUnlocked");
                status = SecItemAdd(item.Handle, IntPtr.Zero);
            }
            if (status != 0)
                throw new CliVaultException("macOS Keychain could not save the CLI sign-in.");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public void Delete(string account)
    {
        using var query = CreateItemQuery(account);
        var status = SecItemDelete(query.Handle);
        if (status is not (0 or ItemNotFound))
            throw new CliVaultException("macOS Keychain could not remove the CLI sign-in.");
    }

    private static NativeDictionary CreateItemQuery(string account)
    {
        var query = new NativeDictionary();
        try
        {
            query.AddGlobal("kSecClass", "kSecClassGenericPassword");
            query.AddString("kSecAttrService", Service);
            query.AddString("kSecAttrAccount", account);
            return query;
        }
        catch
        {
            query.Dispose();
            throw;
        }
    }

    private static IntPtr Global(string library, string symbol)
    {
        var value = Marshal.ReadIntPtr(
            NativeLibrary.GetExport(library == "Security" ? Security : CoreFoundation, symbol));
        return value != IntPtr.Zero
            ? value
            : throw new CliVaultException("macOS Keychain returned an invalid native attribute.");
    }

    private sealed class NativeDictionary : IDisposable
    {
        private readonly List<IntPtr> _ownedValues = [];

        public NativeDictionary()
        {
            Handle = CFDictionaryCreateMutable(IntPtr.Zero, UIntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (Handle == IntPtr.Zero)
                throw new CliVaultException("macOS Keychain could not prepare a secure credential operation.");
        }

        public IntPtr Handle { get; }

        public void AddGlobal(string key, string value) =>
            CFDictionarySetValue(Handle, Global(key.StartsWith("kSec", StringComparison.Ordinal)
                ? "Security" : "CoreFoundation", key), Global(value.StartsWith("kSec", StringComparison.Ordinal)
                ? "Security" : "CoreFoundation", value));

        public void AddString(string key, string value)
        {
            var item = CreateString(value);
            _ownedValues.Add(item);
            CFDictionarySetValue(Handle, Global("Security", key), item);
        }

        public void AddData(string key, IntPtr value) =>
            CFDictionarySetValue(Handle, Global("Security", key), value);

        public void Dispose()
        {
            CFRelease(Handle);
            foreach (var value in _ownedValues) CFRelease(value);
        }
    }

    private sealed class NativeData : IDisposable
    {
        public NativeData(IntPtr bytes, int length)
        {
            Handle = CFDataCreate(IntPtr.Zero, bytes, (nint)length);
            if (Handle == IntPtr.Zero)
                throw new CliVaultException("macOS Keychain could not prepare secure credential data.");
        }

        public IntPtr Handle { get; }
        public void Dispose() => CFRelease(Handle);
    }

    private static IntPtr CreateString(string value)
    {
        var item = CFStringCreateWithCString(IntPtr.Zero, value, Utf8StringEncoding);
        return item != IntPtr.Zero
            ? item
            : throw new CliVaultException("macOS Keychain could not encode a secure credential attribute.");
    }

    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);

    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecItemAdd(IntPtr attributes, IntPtr result);

    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecItemUpdate(IntPtr query, IntPtr attributes);

    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecItemDelete(IntPtr query);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFDictionaryCreateMutable(
        IntPtr allocator, UIntPtr capacity, IntPtr keyCallbacks, IntPtr valueCallbacks);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFDictionarySetValue(IntPtr dictionary, IntPtr key, IntPtr value);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFStringCreateWithCString(
        IntPtr allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFDataCreate(IntPtr allocator, IntPtr bytes, nint length);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern nint CFDataGetLength(IntPtr data);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFDataGetBytePtr(IntPtr data);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr value);
}
