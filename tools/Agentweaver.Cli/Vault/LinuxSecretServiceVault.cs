using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Agentweaver.Cli.Vault;

internal sealed class LinuxSecretServiceVault : ICliCredentialVault
{
    private const int StringAttribute = 0;
    private const int SecretSearchAll = 2;
    private const int MaximumCredentialBytes = 16 * 1024;
    private const string SecretLibrary = "libsecret-1.so.0";
    private const string GlibLibrary = "libglib-2.0.so.0";
    private const string GObjectLibrary = "libgobject-2.0.so.0";
    private const string Service = "agentweaver-cli-oauth-v1";
    private const string Label = "Agentweaver CLI OAuth refresh token";
    private static readonly IntPtr Secret = NativeLibrary.Load(SecretLibrary);
    private static readonly IntPtr Glib = NativeLibrary.Load(GlibLibrary);
    private static readonly IntPtr GObject = NativeLibrary.Load(GObjectLibrary);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public string? Get(string account)
    {
        using var schema = new NativeSchema();
        using var attributes = CreateAttributes(account);
        var error = IntPtr.Zero;
        var password = SecretPasswordLookupVSync(
            schema.Handle, attributes.Handle, IntPtr.Zero, out error);
        if (error != IntPtr.Zero)
        {
            if (password != IntPtr.Zero) SecretPasswordFree(password);
            ThrowSecretServiceError(error, "Linux Secret Service could not read the CLI sign-in.");
        }
        if (password == IntPtr.Zero) return null;

        try
        {
            var length = GetBoundedLength(password);
            if (length is 0 or > MaximumCredentialBytes)
                throw new CliVaultException("The saved CLI sign-in is invalid or exceeds the vault limit.");

            var bytes = new byte[length];
            try
            {
                Marshal.Copy(password, bytes, 0, bytes.Length);
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
            SecretPasswordFree(password);
        }
    }

    public void Set(string account, string refreshToken)
    {
        var bytes = StrictUtf8.GetBytes(refreshToken);
        if (bytes.Length is 0 or > MaximumCredentialBytes)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new CliVaultException("The refresh token exceeds the Linux Secret Service limit.");
        }

        var password = Marshal.AllocHGlobal(bytes.Length + 1);
        try
        {
            Marshal.Copy(bytes, 0, password, bytes.Length);
            Marshal.WriteByte(password, bytes.Length, 0);
            using var schema = new NativeSchema();
            using var attributes = CreateAttributes(account);
            var error = IntPtr.Zero;
            var stored = SecretPasswordStoreVSync(
                schema.Handle, attributes.Handle, IntPtr.Zero, Label, password, IntPtr.Zero, out error);
            if (error != IntPtr.Zero)
                ThrowSecretServiceError(error, "Linux Secret Service could not save the CLI sign-in.");
            if (stored == 0)
                throw new CliVaultException("Linux Secret Service could not save the CLI sign-in.");
        }
        finally
        {
            Marshal.Copy(new byte[bytes.Length + 1], 0, password, bytes.Length + 1);
            Marshal.FreeHGlobal(password);
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public void Delete(string account)
    {
        using var schema = new NativeSchema();
        using var attributes = CreateAttributes(account);
        var error = IntPtr.Zero;
        var cleared = SecretPasswordClearVSync(
            schema.Handle, attributes.Handle, IntPtr.Zero, out error);
        if (error != IntPtr.Zero)
            ThrowSecretServiceError(error, "Linux Secret Service could not remove the CLI sign-in.");
        var remaining = SecretServiceSearchSync(
            IntPtr.Zero, schema.Handle, attributes.Handle, SecretSearchAll, IntPtr.Zero, out error);
        if (error != IntPtr.Zero)
        {
            if (remaining != IntPtr.Zero) FreeSearchResults(remaining);
            ThrowSecretServiceError(error, "Linux Secret Service could not confirm CLI sign-in removal.");
        }
        if (remaining != IntPtr.Zero)
        {
            FreeSearchResults(remaining);
            throw new CliVaultException(cleared == 0
                ? "Linux Secret Service could not remove the CLI sign-in."
                : "Linux Secret Service could not remove every CLI sign-in.");
        }
    }

    private static NativeHashTable CreateAttributes(string account)
    {
        var hash = NativeLibrary.GetExport(Glib, "g_str_hash");
        var equal = NativeLibrary.GetExport(Glib, "g_str_equal");
        var free = NativeLibrary.GetExport(Glib, "g_free");
        var table = new NativeHashTable(
            GHashTableNewFull(hash, equal, free, free));
        try
        {
            if (table.Handle == IntPtr.Zero)
                throw new CliVaultException("Linux Secret Service could not prepare credential lookup.");

            table.Add("service", Service);
            table.Add("account", account);
            return table;
        }
        catch
        {
            table.Dispose();
            throw;
        }
    }

    private static int GetBoundedLength(IntPtr value)
    {
        for (var index = 0; index <= MaximumCredentialBytes; index++)
        {
            if (Marshal.ReadByte(value, index) == 0) return index;
        }
        return MaximumCredentialBytes + 1;
    }

    private static void ThrowSecretServiceError(IntPtr error, string message)
    {
        GErrorFree(error);
        throw new CliVaultException(message);
    }

    private static void FreeSearchResults(IntPtr items) =>
        GListFreeFull(items, NativeLibrary.GetExport(GObject, "g_object_unref"));

    private sealed class NativeSchema : IDisposable
    {
        public NativeSchema()
        {
            var hash = NativeLibrary.GetExport(Glib, "g_str_hash");
            var equal = NativeLibrary.GetExport(Glib, "g_str_equal");
            var free = NativeLibrary.GetExport(Glib, "g_free");
            using var attributes = new NativeHashTable(
                GHashTableNewFull(hash, equal, free, IntPtr.Zero));
            if (attributes.Handle == IntPtr.Zero)
                throw new CliVaultException("Linux Secret Service could not prepare its credential schema.");

            attributes.Add("service", new IntPtr(StringAttribute));
            attributes.Add("account", new IntPtr(StringAttribute));
            Handle = SecretSchemaNewV(Service, 0, attributes.Handle);
            if (Handle == IntPtr.Zero)
                throw new CliVaultException("Linux Secret Service could not initialize its credential schema.");
        }

        public IntPtr Handle { get; }
        public void Dispose()
        {
            if (Handle != IntPtr.Zero) SecretSchemaUnref(Handle);
        }
    }

    private sealed class NativeHashTable : IDisposable
    {
        public NativeHashTable(IntPtr handle) => Handle = handle;
        public IntPtr Handle { get; }

        public void Add(string key, string value)
        {
            var nativeKey = GStrDup(key);
            if (nativeKey == IntPtr.Zero)
                throw new CliVaultException("Linux Secret Service could not prepare credential lookup.");
            var nativeValue = GStrDup(value);
            if (nativeValue == IntPtr.Zero)
            {
                GFree(nativeKey);
                throw new CliVaultException("Linux Secret Service could not prepare credential lookup.");
            }
            if (GHashTableInsert(Handle, nativeKey, nativeValue) == 0)
                throw new CliVaultException("Linux Secret Service could not prepare credential lookup.");
        }

        public void Add(string key, IntPtr value)
        {
            var nativeKey = GStrDup(key);
            if (nativeKey == IntPtr.Zero)
                throw new CliVaultException("Linux Secret Service could not prepare its credential schema.");
            if (GHashTableInsert(Handle, nativeKey, value) == 0)
                throw new CliVaultException("Linux Secret Service could not prepare its credential schema.");
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero) GHashTableDestroy(Handle);
        }
    }

    [DllImport(SecretLibrary, EntryPoint = "secret_schema_newv", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SecretSchemaNewV(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags, IntPtr attributes);

    [DllImport(SecretLibrary, EntryPoint = "secret_schema_unref", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SecretSchemaUnref(IntPtr schema);

    [DllImport(SecretLibrary, EntryPoint = "secret_password_lookupv_sync", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SecretPasswordLookupVSync(
        IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);

    [DllImport(SecretLibrary, EntryPoint = "secret_password_storev_sync", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SecretPasswordStoreVSync(
        IntPtr schema,
        IntPtr attributes,
        IntPtr collection,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string label,
        IntPtr password,
        IntPtr cancellable,
        out IntPtr error);

    [DllImport(SecretLibrary, EntryPoint = "secret_password_clearv_sync", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SecretPasswordClearVSync(
        IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);

    [DllImport(SecretLibrary, EntryPoint = "secret_service_search_sync", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SecretServiceSearchSync(
        IntPtr service, IntPtr schema, IntPtr attributes, int flags, IntPtr cancellable, out IntPtr error);

    [DllImport(SecretLibrary, EntryPoint = "secret_password_free", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SecretPasswordFree(IntPtr password);

    [DllImport(GlibLibrary, EntryPoint = "g_hash_table_new_full", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr GHashTableNewFull(
        IntPtr hashFunction, IntPtr equalFunction, IntPtr keyDestroy, IntPtr valueDestroy);

    [DllImport(GlibLibrary, EntryPoint = "g_hash_table_insert", CallingConvention = CallingConvention.Cdecl)]
    private static extern int GHashTableInsert(IntPtr table, IntPtr key, IntPtr value);

    [DllImport(GlibLibrary, EntryPoint = "g_hash_table_destroy", CallingConvention = CallingConvention.Cdecl)]
    private static extern void GHashTableDestroy(IntPtr table);

    [DllImport(GlibLibrary, EntryPoint = "g_strdup", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr GStrDup([MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(GlibLibrary, EntryPoint = "g_free", CallingConvention = CallingConvention.Cdecl)]
    private static extern void GFree(IntPtr value);

    [DllImport(GlibLibrary, EntryPoint = "g_error_free", CallingConvention = CallingConvention.Cdecl)]
    private static extern void GErrorFree(IntPtr error);

    [DllImport(GlibLibrary, EntryPoint = "g_list_free_full", CallingConvention = CallingConvention.Cdecl)]
    private static extern void GListFreeFull(IntPtr list, IntPtr freeFunction);
}
