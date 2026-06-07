using System.Text;

namespace Scaffolder.SandboxFs;

/// <summary>
/// Sandboxed file I/O. Every operation validates the path lexically before
/// opening and re-verifies the opened handle's real path afterwards. Returns
/// structured results and never throws into the agent loop; the caller emits
/// the corresponding tool.result / tool.rejected / tool.error events.
/// </summary>
public sealed class SandboxedFileTools
{
    private readonly string _sandboxRoot;

    public SandboxedFileTools(string sandboxRoot)
    {
        _sandboxRoot = Path.GetFullPath(sandboxRoot);
    }

    public string SandboxRoot => _sandboxRoot;

    /// <summary>
    /// Reads a text file. Returns (content, null) on success; (null, failure)
    /// on any failure.
    /// </summary>
    public async Task<(string? Content, SandboxReadFailure? Failure)> ReadFileAsync(
        string requestedPath, CancellationToken ct = default)
    {
        string resolvedPath;
        try
        {
            resolvedPath = SandboxPathValidator.ValidateAndResolve(requestedPath, _sandboxRoot);
        }
        catch (SandboxViolationException ex)
        {
            return (null, new SandboxReadFailure(SandboxFailureKind.Rejected, ex.Message));
        }

        if (!File.Exists(resolvedPath))
            return (null, new SandboxReadFailure(SandboxFailureKind.NotFound, $"File not found: {requestedPath}"));

        try
        {
            await using var fs = new FileStream(resolvedPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, useAsync: true);

            // Verify the opened handle resolves inside the sandbox before reading
            // a single byte. The handle is owned by the FileStream; do not dispose it.
            SandboxPathValidator.VerifyOpenedHandle(fs.SafeFileHandle, _sandboxRoot, requestedPath);

            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            var content = await reader.ReadToEndAsync(ct);
            return (content, null);
        }
        catch (SandboxViolationException ex)
        {
            return (null, new SandboxReadFailure(SandboxFailureKind.Rejected, ex.Message));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, new SandboxReadFailure(SandboxFailureKind.Error, ex.Message));
        }
    }

    /// <summary>
    /// Writes UTF-8 text to a file (creates or overwrites). Returns
    /// (bytesWritten, null) on success; (0, failure) on failure.
    /// </summary>
    public async Task<(long BytesWritten, SandboxWriteFailure? Failure)> WriteFileAsync(
        string requestedPath, string content, CancellationToken ct = default)
    {
        string resolvedPath;
        try
        {
            resolvedPath = SandboxPathValidator.ValidateAndResolve(requestedPath, _sandboxRoot);
        }
        catch (SandboxViolationException ex)
        {
            return (0, new SandboxWriteFailure(SandboxFailureKind.Rejected, ex.Message));
        }

        try
        {
            var dir = Path.GetDirectoryName(resolvedPath);
            if (dir is not null && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                // Re-validate after creating directories so a freshly created
                // path cannot have introduced a reparse-point ancestor.
                SandboxPathValidator.ValidateAndResolve(requestedPath, _sandboxRoot);
            }

            var bytes = Encoding.UTF8.GetBytes(content);
            await using var fs = new FileStream(resolvedPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 4096, useAsync: true);

            SandboxPathValidator.VerifyOpenedHandle(fs.SafeFileHandle, _sandboxRoot, requestedPath);

            await fs.WriteAsync(bytes, ct);
            await fs.FlushAsync(ct);
            return (bytes.Length, null);
        }
        catch (SandboxViolationException ex)
        {
            return (0, new SandboxWriteFailure(SandboxFailureKind.Rejected, ex.Message));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (0, new SandboxWriteFailure(SandboxFailureKind.Error, ex.Message));
        }
    }
}

public enum SandboxFailureKind
{
    Rejected,
    NotFound,
    Error
}

public sealed record SandboxReadFailure(SandboxFailureKind Kind, string Message);

public sealed record SandboxWriteFailure(SandboxFailureKind Kind, string Message);
