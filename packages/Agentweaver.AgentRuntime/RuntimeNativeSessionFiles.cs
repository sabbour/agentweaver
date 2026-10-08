using System.IO.Compression;
using System.Text;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;

namespace Agentweaver.AgentRuntime;

#pragma warning disable GHCP001 // Native filesystem RPC contracts belong to the pinned SDK.
// The SDK owns the file contents. Agentweaver only archives the virtual filesystem.
internal sealed class RuntimeNativeSessionFiles(params string[] forbiddenCredentials) : SessionFsProvider
{
    private static readonly UTF8Encoding Utf8 = new(false, throwOnInvalidBytes: true);
    private readonly object _gate = new();
    private readonly Dictionary<string, NativeFile> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _directories = new(StringComparer.Ordinal) { "" };

    internal byte[] Capture()
    {
        lock (_gate)
        {
            if (_files.Count == 0)
                throw new RuntimeAuthorizationException("runtime_sdk_cache_unavailable");
            using var buffer = new MemoryStream();
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                long size = 0;
                foreach (var directory in _directories.Where(path => path.Length > 0).Order(StringComparer.Ordinal))
                    archive.CreateEntry(directory + "/", CompressionLevel.NoCompression).LastWriteTime =
                        new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                foreach (var (path, file) in _files.OrderBy(item => item.Key, StringComparer.Ordinal))
                {
                    RequireCredentialSafe(file.Content);
                    var bytes = Utf8.GetBytes(file.Content);
                    size += bytes.Length;
                    if (size > SessionMaterialValidation.MaximumBytes)
                        throw new RuntimeAuthorizationException("runtime_sdk_cache_too_large");
                    var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
                    entry.LastWriteTime = file.ModifiedAt;
                    using var destination = entry.Open();
                    destination.Write(bytes);
                }
            }
            if (buffer.Length > SessionMaterialValidation.MaximumBytes)
                throw new RuntimeAuthorizationException("runtime_sdk_cache_too_large");
            return buffer.ToArray();
        }
    }

    internal void Restore(byte[] bytes)
    {
        if (bytes is not { Length: > 0 and <= SessionMaterialValidation.MaximumBytes })
            throw new RuntimeAuthorizationException("runtime_sdk_cache_invalid");
        var restored = new RuntimeNativeSessionFiles(forbiddenCredentials);
        try
        {
            using var buffer = new MemoryStream(bytes, writable: false);
            using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);
            long size = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in archive.Entries)
            {
                var directory = entry.FullName.EndsWith("/", StringComparison.Ordinal);
                var path = Normalize(directory ? entry.FullName[..^1] : entry.FullName);
                if (entry.FullName != path + (directory ? "/" : "") ||
                    path.Length == 0 || !seen.Add(path) || entry.Length < 0 ||
                    (size += entry.Length) > SessionMaterialValidation.MaximumBytes)
                    throw new RuntimeAuthorizationException("runtime_sdk_cache_invalid");
                if (directory)
                {
                    if (entry.Length != 0)
                        throw new RuntimeAuthorizationException("runtime_sdk_cache_invalid");
                    restored.CreateDirectory(path, recursive: true);
                }
                else
                {
                    using var content = entry.Open();
                    var value = new byte[checked((int)entry.Length)];
                    content.ReadExactly(value);
                    if (content.ReadByte() != -1)
                        throw new RuntimeAuthorizationException("runtime_sdk_cache_invalid");
                    restored.Write(path, Utf8.GetString(value), append: false);
                    restored._files[path] = restored._files[path] with
                    {
                        CreatedAt = entry.LastWriteTime, ModifiedAt = entry.LastWriteTime
                    };
                }
            }
            if (restored._files.Count == 0)
                throw new RuntimeAuthorizationException("runtime_sdk_cache_unavailable");
        }
        catch (Exception failure) when (failure is IOException or InvalidDataException or DecoderFallbackException)
        {
            throw new RuntimeAuthorizationException("runtime_sdk_cache_invalid");
        }
        lock (_gate)
        {
            if (_files.Count != 0)
                throw new InvalidOperationException("Native cache restore must precede SDK session creation.");
            _directories.Clear();
            _directories.UnionWith(restored._directories);
            foreach (var file in restored._files)
                _files.Add(file.Key, file.Value);
        }
    }

    protected override Task<string> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            return Task.FromResult(_files.TryGetValue(Normalize(path), out var file)
                ? file.Content : throw new FileNotFoundException("Native session file is unavailable."));
    }

    protected override Task WriteFileAsync(
        string path, string content, int? mode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            Write(Normalize(path), content, append: false);
        return Task.CompletedTask;
    }

    protected override Task AppendFileAsync(
        string path, string content, int? mode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            Write(Normalize(path), content, append: true);
        return Task.CompletedTask;
    }

    protected override Task<bool> ExistsAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var normalized = Normalize(path);
            return Task.FromResult(_files.ContainsKey(normalized) || _directories.Contains(normalized));
        }
    }

    protected override Task<SessionFsStatResult> StatAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var normalized = Normalize(path);
            if (_files.TryGetValue(normalized, out var file))
                return Task.FromResult(new SessionFsStatResult
                {
                    IsFile = true, Size = Utf8.GetByteCount(file.Content),
                    Birthtime = file.CreatedAt, Mtime = file.ModifiedAt
                });
            if (!_directories.Contains(normalized))
                throw new DirectoryNotFoundException("Native session path is unavailable.");
            return Task.FromResult(new SessionFsStatResult { IsDirectory = true });
        }
    }

    protected override Task MakeDirectoryAsync(
        string path, bool recursive, int? mode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            CreateDirectory(Normalize(path), recursive);
        return Task.CompletedTask;
    }

    protected override async Task<IList<string>> ReadDirectoryAsync(
        string path, CancellationToken cancellationToken) =>
        (await ReadDirectoryWithTypesAsync(path, cancellationToken)).Select(entry => entry.Name).ToArray();

    protected override Task<IList<SessionFsReaddirWithTypesEntry>> ReadDirectoryWithTypesAsync(
        string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var normalized = Normalize(path);
            if (!_directories.Contains(normalized))
                throw new DirectoryNotFoundException("Native session directory is unavailable.");
            IList<SessionFsReaddirWithTypesEntry> entries = _directories
                .Where(item => item.Length > 0 && Parent(item) == normalized)
                .Select(item => new SessionFsReaddirWithTypesEntry
                {
                    Name = Leaf(item), Type = SessionFsReaddirWithTypesEntryType.Directory
                })
                .Concat(_files.Keys.Where(item => Parent(item) == normalized)
                    .Select(item => new SessionFsReaddirWithTypesEntry
                    {
                        Name = Leaf(item), Type = SessionFsReaddirWithTypesEntryType.File
                    }))
                .OrderBy(entry => entry.Name, StringComparer.Ordinal).ToArray();
            return Task.FromResult(entries);
        }
    }

    protected override Task RemoveAsync(
        string path, bool recursive, bool force, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var normalized = Normalize(path);
            if (normalized.Length == 0)
                throw new IOException("The native session root cannot be removed.");
            if (_files.Remove(normalized))
                return Task.CompletedTask;
            if (!_directories.Contains(normalized))
            {
                if (!force)
                    throw new FileNotFoundException("Native session path is unavailable.");
                return Task.CompletedTask;
            }
            var descendants = _files.Keys.Concat(_directories).Where(item => Below(item, normalized)).ToArray();
            if (!recursive && descendants.Length > 0)
                throw new IOException("The native session directory is not empty.");
            foreach (var item in descendants)
            {
                _files.Remove(item);
                _directories.Remove(item);
            }
            _directories.Remove(normalized);
        }
        return Task.CompletedTask;
    }

    protected override Task RenameAsync(string source, string destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var from = Normalize(source);
            var to = Normalize(destination);
            if (from.Length == 0 || to.Length == 0 || Below(to, from) ||
                _files.ContainsKey(to) || _directories.Contains(to) || !_directories.Contains(Parent(to)))
                throw new IOException("The native session rename is invalid.");
            if (_files.Remove(from, out var file))
                _files.Add(to, file);
            else if (_directories.Contains(from))
            {
                foreach (var item in _files.Keys.Where(item => Below(item, from)).ToArray())
                {
                    var moved = _files[item];
                    _files.Remove(item);
                    _files.Add(to + item[from.Length..], moved);
                }
                foreach (var item in _directories.Where(item => item == from || Below(item, from)).ToArray())
                {
                    _directories.Remove(item);
                    _directories.Add(to + item[from.Length..]);
                }
            }
            else
                throw new FileNotFoundException("Native session path is unavailable.");
        }
        return Task.CompletedTask;
    }

    private void Write(string path, string content, bool append)
    {
        if (path.Length == 0 || _directories.Contains(path))
            throw new IOException("Native session file path is invalid.");
        var previous = _files.GetValueOrDefault(path);
        var value = append ? (previous?.Content ?? "") + content : content;
        RequireCredentialSafe(value);
        CreateDirectory(Parent(path), recursive: true);
        var now = DateTimeOffset.UtcNow;
        _files[path] = new(value, previous?.CreatedAt ?? now, now);
    }

    private void CreateDirectory(string path, bool recursive)
    {
        if (_files.ContainsKey(path))
            throw new IOException("Native session path is a file.");
        if (_directories.Contains(path))
            return;
        var parent = Parent(path);
        if (!_directories.Contains(parent))
        {
            if (!recursive)
                throw new DirectoryNotFoundException("Native session parent directory is unavailable.");
            CreateDirectory(parent, recursive: true);
        }
        _directories.Add(path);
    }

    private void RequireCredentialSafe(string content)
    {
        if (forbiddenCredentials.Any(value =>
                value.Length > 0 && content.Contains(value, StringComparison.Ordinal)))
            throw new RuntimeAuthorizationException("runtime_sdk_cache_contains_credential");
    }

    private static string Normalize(string path)
    {
        if (path is null || path.Length > 1024 || path.Contains('\\') || path.Contains(':') ||
            path.Any(char.IsControl))
            throw new IOException("Native session path is invalid.");
        var trimmed = path.Trim('/');
        if (trimmed is "" or ".")
            return "";
        if (trimmed.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new IOException("Native session path escapes its private filesystem.");
        return trimmed;
    }

    private static string Parent(string path) => path.LastIndexOf('/') is var index && index >= 0 ? path[..index] : "";
    private static string Leaf(string path) => path[(path.LastIndexOf('/') + 1)..];
    private static bool Below(string path, string directory) => path.StartsWith(directory + "/", StringComparison.Ordinal);
    private sealed record NativeFile(string Content, DateTimeOffset CreatedAt, DateTimeOffset ModifiedAt);
}
#pragma warning restore GHCP001
