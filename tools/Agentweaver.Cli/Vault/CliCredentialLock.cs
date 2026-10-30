using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Agentweaver.Cli.Vault;

internal sealed class CliCredentialLock
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProcessLocks =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(6);
    private readonly string _directory;
    private readonly TimeSpan _timeout;

    public CliCredentialLock(string? directory = null, TimeSpan? timeout = null)
    {
        _directory = Path.GetFullPath(directory ?? DefaultDirectory());
        _timeout = timeout ?? DefaultTimeout;
        if (_timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public async Task<T> RunAsync<T>(
        string account,
        Func<RefreshIntent, CancellationToken, Task<T>> action,
        CancellationToken ct)
    {
        if (!IsCredentialAccount(account))
            throw new ArgumentException("The credential lock key is invalid.", nameof(account));

        EnsurePrivateDirectory();
        var path = Path.Combine(_directory, account + ".lock");
        var processLock = ProcessLocks.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));
        if (!await processLock.WaitAsync(_timeout, ct).ConfigureAwait(false))
            throw new TimeoutException("Timed out waiting for another CLI process to finish using this sign-in.");

        try
        {
            using var stream = OpenLockFile(path);
            await AcquireOperatingSystemLockAsync(stream, ct).ConfigureAwait(false);
            try
            {
                return await action(new RefreshIntent(_directory, account), ct).ConfigureAwait(false);
            }
            finally
            {
                ReleaseOperatingSystemLock(stream);
            }
        }
        finally
        {
            processLock.Release();
        }
    }

    internal static string DefaultDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local))
                throw new CliVaultException("The per-user local credential-state directory is unavailable.");
            return Path.Combine(local, "Agentweaver", "cli-auth-locks");
        }
        if (OperatingSystem.IsMacOS())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support", "Agentweaver", "cli-auth-locks");

        var xdg = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        var state = !string.IsNullOrWhiteSpace(xdg) && Path.IsPathFullyQualified(xdg)
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
        return Path.Combine(state, "agentweaver", "cli-auth-locks");
    }

    private static bool IsCredentialAccount(string account) =>
        account.Length == 79 &&
        account.StartsWith("agentweaver-v1-", StringComparison.Ordinal) &&
        account.AsSpan(15).IndexOfAnyExcept("0123456789abcdef") < 0;

    private void EnsurePrivateDirectory()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(_directory);
                return;
            }

            EnsureDurableDirectoryPath(_directory);
            File.SetUnixFileMode(_directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if (new DirectoryInfo(_directory).LinkTarget is not null)
                throw new CliVaultException("The CLI credential-state directory must not be a symbolic link.");
            SyncDirectory(_directory);
        }
        catch (CliVaultException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new CliVaultException("The private CLI credential-state directory is unavailable.", exception);
        }
    }

    private static void EnsureDurableDirectoryPath(string path)
    {
        if (OperatingSystem.IsWindows()) return;

        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new IOException("The CLI credential-state directory has no filesystem root.");
        var current = root;
        foreach (var segment in fullPath[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var parent = current;
            current = Path.Combine(current, segment);
            if (Directory.Exists(current)) continue;
            Directory.CreateDirectory(current,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            SyncDirectory(parent);
        }
    }

    private static void SyncDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) return;

        var directoryFlag = OperatingSystem.IsMacOS() ? OpenDirectoryMac : OpenDirectoryLinux;
        var descriptor = Open(path, OpenReadOnly | directoryFlag);
        if (descriptor < 0)
            throw DirectorySyncError(Marshal.GetLastPInvokeError());

        var syncError = FSync(descriptor) == 0 ? 0 : Marshal.GetLastPInvokeError();
        var closeError = Close(descriptor) == 0 ? 0 : Marshal.GetLastPInvokeError();
        if (syncError != 0) throw DirectorySyncError(syncError);
        if (closeError != 0) throw DirectorySyncError(closeError);
    }

    private static IOException DirectorySyncError(int error) =>
        new($"The operating system could not durably sync CLI credential state (errno {error}).");

    private static FileStream OpenLockFile(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var options = new FileStreamOptions
                {
                    Mode = FileMode.OpenOrCreate,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.ReadWrite,
                    Options = FileOptions.None,
                };
                return new FileStream(path, options);
            }

            var create = OperatingSystem.IsMacOS() ? OpenCreateMac : OpenCreateLinux;
            var closeOnExec = OperatingSystem.IsMacOS() ? OpenCloseOnExecMac : OpenCloseOnExecLinux;
            var descriptor = OpenFile(path, OpenReadWrite | create | closeOnExec, UnixPrivateFileMode);
            if (descriptor < 0)
                throw new IOException(
                    $"The operating system could not open the persistent CLI credential lock file (errno {Marshal.GetLastPInvokeError()}).");

            var handle = new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
            try
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                return new FileStream(handle, FileAccess.ReadWrite);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new CliVaultException("The persistent CLI credential lock file is unavailable.", exception);
        }
    }

    private async Task AcquireOperatingSystemLockAsync(FileStream stream, CancellationToken ct)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(_timeout.TotalSeconds * Stopwatch.Frequency);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (TryAcquireOperatingSystemLock(stream.SafeFileHandle))
                return;

            if (Stopwatch.GetTimestamp() >= deadline)
                throw new TimeoutException("Timed out waiting for another CLI process to finish using this sign-in.");
            await Task.Delay(TimeSpan.FromMilliseconds(40), ct).ConfigureAwait(false);
        }
    }

    private static bool TryAcquireOperatingSystemLock(SafeFileHandle handle)
    {
        if (OperatingSystem.IsWindows())
        {
            var overlap = default(Overlapped);
            if (LockFileEx(handle, LockExclusive | LockImmediately, 0, 1, 0, ref overlap))
                return true;
            if (Marshal.GetLastPInvokeError() == ErrorLockViolation)
                return false;
            throw new CliVaultException("Windows could not acquire the CLI credential lock.");
        }

        var descriptor = handle.DangerousGetHandle().ToInt32();
        if (Flock(descriptor, FlockExclusive | FlockImmediately) == 0)
            return true;
        var error = Marshal.GetLastPInvokeError();
        if (error is ErrorAgain or ErrorWouldBlock or ErrorInterrupted)
            return false;
        throw new CliVaultException("The operating system could not acquire the CLI credential lock.");
    }

    private static void ReleaseOperatingSystemLock(FileStream stream)
    {
        if (OperatingSystem.IsWindows())
        {
            var overlap = default(Overlapped);
            if (!UnlockFileEx(stream.SafeFileHandle, 0, 1, 0, ref overlap))
                throw new CliVaultException("Windows could not release the CLI credential lock.");
            return;
        }

        if (Flock(stream.SafeFileHandle.DangerousGetHandle().ToInt32(), FlockUnlock) != 0)
            throw new CliVaultException("The operating system could not release the CLI credential lock.");
    }

    internal sealed class RefreshIntent(string directory, string account)
    {
        private readonly string _directory = directory;
        private readonly string _path = Path.Combine(directory, account + ".refreshing");

        public bool Exists()
        {
            try
            {
                using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
                return true;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new CliVaultException("The CLI refresh-recovery state cannot be inspected.", exception);
            }
        }

        public void Mark()
        {
            try
            {
                var options = new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.Read,
                    Options = FileOptions.WriteThrough,
                };
                if (!OperatingSystem.IsWindows())
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using var stream = new FileStream(_path, options);
                stream.WriteByte(1);
                stream.Flush(flushToDisk: true);
                SyncDirectory(_directory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new CliVaultException(
                    "The CLI could not durably record refresh recovery state before contacting the Broker.",
                    exception);
            }
        }

        public void Clear()
        {
            try
            {
                File.Delete(_path);
                SyncDirectory(_directory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new CliVaultException("The CLI could not clear refresh recovery state.", exception);
            }
        }
    }

    private const uint LockExclusive = 0x00000002;
    private const uint LockImmediately = 0x00000001;
    private const int FlockExclusive = 2;
    private const int FlockImmediately = 4;
    private const int FlockUnlock = 8;
    private const int ErrorLockViolation = 33;
    private const int ErrorAgain = 11;
    private const int ErrorWouldBlock = 35;
    private const int ErrorInterrupted = 4;
    private const int OpenReadOnly = 0;
    private const int OpenReadWrite = 0x00000002;
    private const int OpenCreateLinux = 0x00000040;
    private const int OpenCreateMac = 0x00000200;
    private const int OpenCloseOnExecLinux = 0x00080000;
    private const int OpenCloseOnExecMac = 0x01000000;
    private const int OpenDirectoryLinux = 0x00010000;
    private const int OpenDirectoryMac = 0x00100000;
    private const uint UnixPrivateFileMode = 0x00000180;

    [StructLayout(LayoutKind.Sequential)]
    private struct Overlapped
    {
        public IntPtr Internal;
        public IntPtr InternalHigh;
        public uint Offset;
        public uint OffsetHigh;
        public IntPtr Event;
    }

    [DllImport("kernel32.dll", EntryPoint = "LockFileEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockFileEx(
        SafeFileHandle handle,
        uint flags,
        uint reserved,
        uint bytesLow,
        uint bytesHigh,
        ref Overlapped overlap);

    [DllImport("kernel32.dll", EntryPoint = "UnlockFileEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnlockFileEx(
        SafeFileHandle handle,
        uint reserved,
        uint bytesLow,
        uint bytesHigh,
        ref Overlapped overlap);

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(int descriptor, int operation);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenFile(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mode);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int FSync(int descriptor);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int descriptor);
}
