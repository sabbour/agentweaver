using System.Text;

namespace Agentweaver.SandboxFs;

/// <summary>
/// Sandboxed file I/O. Every operation validates the path lexically before
/// opening and re-verifies the opened handle's real path afterwards. Returns
/// structured results and never throws into the agent loop; the caller emits
/// the corresponding tool.result / tool.rejected / tool.error events.
/// </summary>
public sealed class SandboxedFileTools
{
    private readonly string _sandboxRoot;
    private readonly int _maxOutputBytes;

    public SandboxedFileTools(string sandboxRoot, int maxOutputBytes = 0)
    {
        _sandboxRoot = Path.GetFullPath(sandboxRoot);
        _maxOutputBytes = maxOutputBytes;

        // Seraph A2: reject if the sandbox root itself is a reparse point
        if (Directory.Exists(_sandboxRoot))
        {
            var rootInfo = new DirectoryInfo(_sandboxRoot);
            if (rootInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new SandboxViolationException(_sandboxRoot, _sandboxRoot,
                    "sandbox root is a symbolic link or junction; refusing to operate");
        }
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

        if (Directory.Exists(resolvedPath))
            return (null, new SandboxReadFailure(SandboxFailureKind.NotFound,
                $"Path is a directory; use list_directory: {requestedPath}"));

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
            if (_maxOutputBytes > 0 && Encoding.UTF8.GetByteCount(content) > _maxOutputBytes)
            {
                // Truncate to max bytes at a UTF-16 char boundary safe for the model.
                content = content[..GetCharLimitForBytes(content, _maxOutputBytes)] + "\n[truncated]";
            }
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

            // Reject reparse point at target path before destructive open (defense-in-depth).
            // Residual TOCTOU window remains but attack requires concurrent filesystem writes
            // by a local privileged attacker — outside the practical threat model.
            if (File.Exists(resolvedPath) || Directory.Exists(resolvedPath))
            {
                var attrs = File.GetAttributes(resolvedPath);
                if (attrs.HasFlag(FileAttributes.ReparsePoint))
                    throw new SandboxViolationException(resolvedPath, _sandboxRoot,
                        "path is a symbolic link or junction; refusing to write");
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

    /// <summary>
    /// Reads a range of lines from a text file (1-based, inclusive). Returns
    /// (content, null) on success; (null, failure) on failure.
    /// If <paramref name="endLine"/> is negative, reads to end of file.
    /// If <paramref name="startLine"/> is beyond total lines, returns empty string (not an error).
    /// </summary>
    public async Task<(string? Content, SandboxReadFailure? Failure)> ReadFileRangeAsync(
        string requestedPath, int startLine, int endLine, CancellationToken ct = default)
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

        if (Directory.Exists(resolvedPath))
            return (null, new SandboxReadFailure(SandboxFailureKind.NotFound,
                $"Path is a directory; use list_directory: {requestedPath}"));

        if (!File.Exists(resolvedPath))
            return (null, new SandboxReadFailure(SandboxFailureKind.NotFound, $"File not found: {requestedPath}"));

        try
        {
            await using var fs = new FileStream(resolvedPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, useAsync: true);

            SandboxPathValidator.VerifyOpenedHandle(fs.SafeFileHandle, _sandboxRoot, requestedPath);

            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            var content = await reader.ReadToEndAsync(ct);

            bool trailingNewline = content.Length > 0 && content[^1] == '\n';
            var lines = content.Split('\n').ToList();
            if (trailingNewline && lines.Count > 0 && lines[^1] == string.Empty)
                lines.RemoveAt(lines.Count - 1);

            int totalLines = lines.Count;
            int start = Math.Max(0, startLine - 1);
            if (start >= totalLines)
                return (string.Empty, null);

            int end = endLine < 0 ? totalLines - 1 : Math.Min(endLine - 1, totalLines - 1);
            if (start > end)
                return (string.Empty, null);

            var selected = lines.GetRange(start, end - start + 1);
            var result = string.Join("\n", selected) + (trailingNewline ? "\n" : string.Empty);
            return (result, null);
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
    /// Creates a new file with the given content. Fails if the file already exists.
    /// Returns (bytesWritten, null) on success; (0, failure) on failure.
    /// </summary>
    public async Task<(long BytesWritten, SandboxWriteFailure? Failure)> CreateFileAsync(
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

        if (File.Exists(resolvedPath))
            return (0, new SandboxWriteFailure(SandboxFailureKind.Error, $"File already exists: {requestedPath}"));

        try
        {
            var dir = Path.GetDirectoryName(resolvedPath);
            if (dir is not null && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                SandboxPathValidator.ValidateAndResolve(requestedPath, _sandboxRoot);
            }

            // Reject reparse point at target path before destructive open (defense-in-depth).
            // Residual TOCTOU window remains but attack requires concurrent filesystem writes
            // by a local privileged attacker — outside the practical threat model.
            if (File.Exists(resolvedPath) || Directory.Exists(resolvedPath))
            {
                var attrs = File.GetAttributes(resolvedPath);
                if (attrs.HasFlag(FileAttributes.ReparsePoint))
                    throw new SandboxViolationException(resolvedPath, _sandboxRoot,
                        "path is a symbolic link or junction; refusing to write");
            }

            var bytes = Encoding.UTF8.GetBytes(content);
            await using var fs = new FileStream(resolvedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
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

    /// <summary>
    /// Replaces a single unique occurrence of <paramref name="oldStr"/> with
    /// <paramref name="newStr"/> in the file. Fails if the string is not found or
    /// if it appears more than once. Returns (true, null) on success; (false, failure)
    /// on failure.
    /// </summary>
    public async Task<(bool Replaced, SandboxWriteFailure? Failure)> StrReplaceAsync(
        string requestedPath, string oldStr, string newStr, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(oldStr))
            return (false, new SandboxWriteFailure(SandboxFailureKind.Error, "oldStr must not be empty."));

        string resolvedPath;
        try
        {
            resolvedPath = SandboxPathValidator.ValidateAndResolve(requestedPath, _sandboxRoot);
        }
        catch (SandboxViolationException ex)
        {
            return (false, new SandboxWriteFailure(SandboxFailureKind.Rejected, ex.Message));
        }

        if (!File.Exists(resolvedPath))
            return (false, new SandboxWriteFailure(SandboxFailureKind.NotFound, $"File not found: {requestedPath}"));

        try
        {
            // Reject reparse point at target path before destructive open (defense-in-depth).
            // Residual TOCTOU window remains but attack requires concurrent filesystem writes
            // by a local privileged attacker — outside the practical threat model.
            if (File.Exists(resolvedPath) || Directory.Exists(resolvedPath))
            {
                var attrs = File.GetAttributes(resolvedPath);
                if (attrs.HasFlag(FileAttributes.ReparsePoint))
                    throw new SandboxViolationException(resolvedPath, _sandboxRoot,
                        "path is a symbolic link or junction; refusing to write");
            }

            string fileContent;
            await using (var readFs = new FileStream(resolvedPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, useAsync: true))
            {
                SandboxPathValidator.VerifyOpenedHandle(readFs.SafeFileHandle, _sandboxRoot, requestedPath);
                using var reader = new StreamReader(readFs, Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true, leaveOpen: true);
                fileContent = await reader.ReadToEndAsync(ct);
            }

            int count = 0;
            int idx = 0;
            while ((idx = fileContent.IndexOf(oldStr, idx, StringComparison.Ordinal)) >= 0)
            {
                count++;
                idx += oldStr.Length;
            }

            if (count == 0)
                return (false, new SandboxWriteFailure(SandboxFailureKind.Error, "String not found"));
            if (count > 1)
                return (false, new SandboxWriteFailure(SandboxFailureKind.Error,
                    $"Multiple occurrences found ({count}); must be unique"));

            var newContent = fileContent.Replace(oldStr, newStr, StringComparison.Ordinal);
            var bytes = Encoding.UTF8.GetBytes(newContent);

            // Reject reparse point at target path before destructive open (defense-in-depth).
            if (File.Exists(resolvedPath) || Directory.Exists(resolvedPath))
            {
                var attrs = File.GetAttributes(resolvedPath);
                if (attrs.HasFlag(FileAttributes.ReparsePoint))
                    throw new SandboxViolationException(resolvedPath, _sandboxRoot,
                        "path is a symbolic link or junction; refusing to write");
            }

            await using var writeFs = new FileStream(resolvedPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 4096, useAsync: true);
            SandboxPathValidator.VerifyOpenedHandle(writeFs.SafeFileHandle, _sandboxRoot, requestedPath);
            await writeFs.WriteAsync(bytes, ct);
            await writeFs.FlushAsync(ct);
            return (true, null);
        }
        catch (SandboxViolationException ex)
        {
            return (false, new SandboxWriteFailure(SandboxFailureKind.Rejected, ex.Message));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, new SandboxWriteFailure(SandboxFailureKind.Error, ex.Message));
        }
    }

    /// <summary>
    /// Inserts <paramref name="newStr"/> as a new line before <paramref name="insertLine"/>
    /// (1-based). Zero or negative inserts at the beginning; beyond total lines appends at
    /// the end. Returns (true, null) on success; (false, failure) on failure.
    /// </summary>
    public async Task<(bool Inserted, SandboxWriteFailure? Failure)> InsertAtLineAsync(
        string requestedPath, int insertLine, string newStr, CancellationToken ct = default)
    {
        string resolvedPath;
        try
        {
            resolvedPath = SandboxPathValidator.ValidateAndResolve(requestedPath, _sandboxRoot);
        }
        catch (SandboxViolationException ex)
        {
            return (false, new SandboxWriteFailure(SandboxFailureKind.Rejected, ex.Message));
        }

        if (!File.Exists(resolvedPath))
            return (false, new SandboxWriteFailure(SandboxFailureKind.NotFound, $"File not found: {requestedPath}"));

        try
        {
            // Reject reparse point at target path before destructive open (defense-in-depth).
            // Residual TOCTOU window remains but attack requires concurrent filesystem writes
            // by a local privileged attacker — outside the practical threat model.
            if (File.Exists(resolvedPath) || Directory.Exists(resolvedPath))
            {
                var attrs = File.GetAttributes(resolvedPath);
                if (attrs.HasFlag(FileAttributes.ReparsePoint))
                    throw new SandboxViolationException(resolvedPath, _sandboxRoot,
                        "path is a symbolic link or junction; refusing to write");
            }

            string fileContent;
            await using (var readFs = new FileStream(resolvedPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, useAsync: true))
            {
                SandboxPathValidator.VerifyOpenedHandle(readFs.SafeFileHandle, _sandboxRoot, requestedPath);
                using var reader = new StreamReader(readFs, Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true, leaveOpen: true);
                fileContent = await reader.ReadToEndAsync(ct);
            }

            bool trailingNewline = fileContent.Length > 0 && fileContent[^1] == '\n';
            var lineList = fileContent.Split('\n').ToList();
            if (trailingNewline && lineList.Count > 0 && lineList[^1] == string.Empty)
                lineList.RemoveAt(lineList.Count - 1);

            int insertIndex = insertLine <= 0 ? 0 : Math.Min(insertLine - 1, lineList.Count);
            lineList.Insert(insertIndex, newStr);

            var newContent = string.Join("\n", lineList) + (trailingNewline ? "\n" : string.Empty);
            var bytes = Encoding.UTF8.GetBytes(newContent);

            // Reject reparse point at target path before destructive open (defense-in-depth).
            if (File.Exists(resolvedPath) || Directory.Exists(resolvedPath))
            {
                var attrs = File.GetAttributes(resolvedPath);
                if (attrs.HasFlag(FileAttributes.ReparsePoint))
                    throw new SandboxViolationException(resolvedPath, _sandboxRoot,
                        "path is a symbolic link or junction; refusing to write");
            }

            await using var writeFs = new FileStream(resolvedPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 4096, useAsync: true);
            SandboxPathValidator.VerifyOpenedHandle(writeFs.SafeFileHandle, _sandboxRoot, requestedPath);
            await writeFs.WriteAsync(bytes, ct);
            await writeFs.FlushAsync(ct);
            return (true, null);
        }
        catch (SandboxViolationException ex)
        {
            return (false, new SandboxWriteFailure(SandboxFailureKind.Rejected, ex.Message));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, new SandboxWriteFailure(SandboxFailureKind.Error, ex.Message));
        }
    }

    /// <summary>
    /// Applies a Copilot CLI custom patch (Add File / Delete File / Update File / Move to).
    /// Two-phase: validates ALL paths in Phase 1, then writes in Phase 2.
    /// Returns <see cref="ApplyPatchResult"/> indicating per-hunk outcomes.
    /// </summary>
    public async Task<ApplyPatchResult> ApplyPatchAsync(string patch, CancellationToken ct = default)
    {
        var hunks = ParsePatch(patch, out var parseError);
        if (hunks is null)
            return new ApplyPatchResult(false, parseError, []);

        // Phase 1: validate every path before touching the filesystem
        var resolvedPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var hunk in hunks)
        {
            if (!resolvedPaths.ContainsKey(hunk.Path))
            {
                try
                {
                    if (hunk.Path.Contains('\0'))
                        throw new SandboxViolationException(hunk.Path, _sandboxRoot, "path contains null byte");
                    resolvedPaths[hunk.Path] = SandboxPathValidator.ValidateAndResolve(hunk.Path, _sandboxRoot);
                }
                catch (SandboxViolationException ex)
                {
                    return new ApplyPatchResult(false, $"Path validation failed: {hunk.Path}: {ex.Message}", []);
                }
            }

            if (hunk.MoveTo is not null && !resolvedPaths.ContainsKey(hunk.MoveTo))
            {
                try
                {
                    if (hunk.MoveTo.Contains('\0'))
                        throw new SandboxViolationException(hunk.MoveTo, _sandboxRoot, "path contains null byte");
                    resolvedPaths[hunk.MoveTo] = SandboxPathValidator.ValidateAndResolve(hunk.MoveTo, _sandboxRoot);
                }
                catch (SandboxViolationException ex)
                {
                    return new ApplyPatchResult(false, $"Path validation failed: {hunk.MoveTo}: {ex.Message}", []);
                }
            }
        }

        // Phase 2: apply
        var results = new List<HunkResult>();
        foreach (var hunk in hunks)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await ApplyHunkAsync(hunk, resolvedPaths, ct));
        }

        bool allOk = results.All(r => r.Success);
        return new ApplyPatchResult(allOk, allOk ? null : "One or more hunks failed.", results);
    }

    private async Task<HunkResult> ApplyHunkAsync(
        PatchHunk hunk,
        Dictionary<string, string> resolvedPaths,
        CancellationToken ct)
    {
        var resolvedPath = resolvedPaths[hunk.Path];

        switch (hunk.Type)
        {
            case PatchHunkType.AddFile:
            {
                var content = string.Join("\n", hunk.NewLines);
                var (_, failure) = await CreateFileAsync(hunk.Path, content, ct);
                return failure is not null
                    ? new HunkResult(hunk.Path, false, failure.Message)
                    : new HunkResult(hunk.Path, true, null);
            }

            case PatchHunkType.DeleteFile:
            {
                if (!File.Exists(resolvedPath))
                    return new HunkResult(hunk.Path, false, $"File not found: {hunk.Path}");
                try
                {
                    File.Delete(resolvedPath);
                    return new HunkResult(hunk.Path, true, null);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return new HunkResult(hunk.Path, false, ex.Message);
                }
            }

            case PatchHunkType.UpdateFile:
            {
                if (!File.Exists(resolvedPath))
                    return new HunkResult(hunk.Path, false, $"File not found: {hunk.Path}");

                string fileContent;
                try
                {
                    await using var readFs = new FileStream(resolvedPath, FileMode.Open, FileAccess.Read,
                        FileShare.Read, bufferSize: 4096, useAsync: true);
                    SandboxPathValidator.VerifyOpenedHandle(readFs.SafeFileHandle, _sandboxRoot, hunk.Path);
                    using var reader = new StreamReader(readFs, Encoding.UTF8,
                        detectEncodingFromByteOrderMarks: true, leaveOpen: true);
                    fileContent = await reader.ReadToEndAsync(ct);
                }
                catch (SandboxViolationException ex)
                {
                    return new HunkResult(hunk.Path, false, ex.Message);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return new HunkResult(hunk.Path, false, ex.Message);
                }

                var fileLines = fileContent.Split('\n').ToList();
                var applyError = ApplyDiffSections(fileLines, hunk.DiffSections);
                if (applyError is not null)
                    return new HunkResult(hunk.Path, false, applyError);

                var newContent = string.Join("\n", fileLines);
                var bytes = Encoding.UTF8.GetBytes(newContent);
                var writeTarget = hunk.MoveTo ?? hunk.Path;
                var resolvedWriteTarget = resolvedPaths[writeTarget];

                try
                {
                    if (hunk.MoveTo is not null)
                    {
                        var dir = Path.GetDirectoryName(resolvedWriteTarget);
                        if (dir is not null && !Directory.Exists(dir))
                            Directory.CreateDirectory(dir);

                        await using var newFs = new FileStream(resolvedWriteTarget, FileMode.Create,
                            FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true);
                        SandboxPathValidator.VerifyOpenedHandle(newFs.SafeFileHandle, _sandboxRoot, hunk.MoveTo);
                        await newFs.WriteAsync(bytes, ct);
                        await newFs.FlushAsync(ct);
                        File.Delete(resolvedPath);
                    }
                    else
                    {
                        await using var writeFs = new FileStream(resolvedPath, FileMode.Create,
                            FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true);
                        SandboxPathValidator.VerifyOpenedHandle(writeFs.SafeFileHandle, _sandboxRoot, hunk.Path);
                        await writeFs.WriteAsync(bytes, ct);
                        await writeFs.FlushAsync(ct);
                    }
                }
                catch (SandboxViolationException ex)
                {
                    return new HunkResult(hunk.Path, false, ex.Message);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return new HunkResult(hunk.Path, false, ex.Message);
                }

                return new HunkResult(hunk.Path, true, null);
            }

            default:
                return new HunkResult(hunk.Path, false, $"Unknown hunk type: {hunk.Type}");
        }
    }

    private static string? ApplyDiffSections(
        List<string> fileLines,
        List<List<(char Op, string Content)>> sections)
    {
        foreach (var section in sections)
        {
            if (section.Count == 0)
                continue;

            var beforeLines = section
                .Where(d => d.Op == ' ' || d.Op == '-')
                .Select(d => d.Content)
                .ToList();

            var afterLines = section
                .Where(d => d.Op == ' ' || d.Op == '+')
                .Select(d => d.Content)
                .ToList();

            if (beforeLines.Count == 0)
                return "Cannot apply pure addition hunk without context lines";

            int matchIndex = FindBlockInLines(fileLines, beforeLines, 0);
            if (matchIndex < 0)
                return "Context not found in file";

            if (FindBlockInLines(fileLines, beforeLines, matchIndex + 1) >= 0)
                return "Context matches multiple locations in file";

            fileLines.RemoveRange(matchIndex, beforeLines.Count);
            fileLines.InsertRange(matchIndex, afterLines);
        }

        return null;
    }

    private static int FindBlockInLines(List<string> lines, List<string> block, int startFrom)
    {
        if (block.Count == 0)
            return startFrom;

        for (int i = startFrom; i <= lines.Count - block.Count; i++)
        {
            bool match = true;
            for (int j = 0; j < block.Count; j++)
            {
                if (lines[i + j] != block[j])
                {
                    match = false;
                    break;
                }
            }
            if (match)
                return i;
        }

        return -1;
    }

    private static List<PatchHunk>? ParsePatch(string patch, out string? error)
    {
        error = null;
        var rawLines = patch.Split('\n');
        var lines = Array.ConvertAll(rawLines, l => l.TrimEnd('\r'));

        int i = 0;
        while (i < lines.Length && lines[i].Trim() != "*** Begin Patch")
            i++;

        if (i >= lines.Length)
        {
            error = "Missing '*** Begin Patch' marker";
            return null;
        }
        i++; // skip Begin Patch line

        var hunks = new List<PatchHunk>();
        PatchHunk? currentHunk = null;
        List<(char Op, string Content)>? currentSection = null;
        bool foundEndPatch = false;

        while (i < lines.Length)
        {
            var line = lines[i];

            if (line == "*** End Patch")
            {
                foundEndPatch = true;
                if (currentHunk is not null)
                    hunks.Add(currentHunk);
                break;
            }

            if (line.StartsWith("*** Add File: ", StringComparison.Ordinal))
            {
                if (currentHunk is not null)
                    hunks.Add(currentHunk);
                currentHunk = new PatchHunk
                {
                    Type = PatchHunkType.AddFile,
                    Path = line["*** Add File: ".Length..].Trim(),
                };
                currentSection = null;
                i++;
                continue;
            }

            if (line.StartsWith("*** Delete File: ", StringComparison.Ordinal))
            {
                if (currentHunk is not null)
                    hunks.Add(currentHunk);
                var deleteHunk = new PatchHunk
                {
                    Type = PatchHunkType.DeleteFile,
                    Path = line["*** Delete File: ".Length..].Trim(),
                };
                hunks.Add(deleteHunk);
                currentHunk = null;
                currentSection = null;
                i++;
                continue;
            }

            if (line.StartsWith("*** Update File: ", StringComparison.Ordinal))
            {
                if (currentHunk is not null)
                    hunks.Add(currentHunk);
                currentHunk = new PatchHunk
                {
                    Type = PatchHunkType.UpdateFile,
                    Path = line["*** Update File: ".Length..].Trim(),
                };
                currentSection = null;
                i++;
                continue;
            }

            if (line.StartsWith("*** Move to: ", StringComparison.Ordinal))
            {
                if (currentHunk is not null)
                {
                    currentHunk.MoveTo = line["*** Move to: ".Length..].Trim();
                    hunks.Add(currentHunk);
                    currentHunk = null;
                }
                currentSection = null;
                i++;
                continue;
            }

            if (currentHunk?.Type == PatchHunkType.AddFile)
            {
                if (line.Length > 0 && line[0] == '+')
                    currentHunk.NewLines.Add(line[1..]);
                i++;
                continue;
            }

            if (currentHunk?.Type == PatchHunkType.UpdateFile)
            {
                if (line.StartsWith("@@", StringComparison.Ordinal))
                {
                    currentSection = [];
                    currentHunk.DiffSections.Add(currentSection);
                    i++;
                    continue;
                }

                if (currentSection is not null && line.Length > 0 &&
                    (line[0] == ' ' || line[0] == '+' || line[0] == '-'))
                {
                    currentSection.Add((line[0], line[1..]));
                }

                i++;
                continue;
            }

            i++;
        }

        if (!foundEndPatch)
        {
            error = "Malformed patch: missing '*** End Patch' marker.";
            return null;
        }

        return hunks;
    }

    /// Returns the maximum number of UTF-16 chars in <paramref name="s"/> such
    /// that the UTF-8 encoding stays within <paramref name="maxBytes"/>.
    private static int GetCharLimitForBytes(string s, int maxBytes)
    {
        int byteCount = 0;
        for (int i = 0; i < s.Length; )
        {
            int charBytes = Encoding.UTF8.GetByteCount(s, i, 1);
            if (byteCount + charBytes > maxBytes)
                return i;
            byteCount += charBytes;
            i++;
        }
        return s.Length;
    }

    private enum PatchHunkType { AddFile, DeleteFile, UpdateFile }

    private sealed class PatchHunk
    {
        public PatchHunkType Type;
        public string Path = string.Empty;
        public string? MoveTo;
        public List<string> NewLines { get; } = [];
        public List<List<(char Op, string Content)>> DiffSections { get; } = [];
    }

    /// <summary>
    /// Lists immediate children (non-recursive) of a directory. Does NOT follow
    /// reparse points. Returns (entries, null) on success; (null, failure) on failure.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="ReadFileAsync"/> and <see cref="WriteFileAsync"/>, this method does
    /// not perform an open-then-verify handle check. Containment relies on lexical validation
    /// via <c>ValidateAndResolve</c> plus <c>AttributesToSkip = ReparsePoint</c> to exclude
    /// junction/symlink children from enumeration results. A narrow TOCTOU window exists where
    /// a local attacker with concurrent filesystem write access could swap the validated
    /// directory for a junction between validation and enumeration. The worst-case outcome is
    /// disclosure of bare filenames (never file content) from outside the sandbox.
    /// Content access remains fully protected because ReadFile/WriteFile retain handle-level
    /// verification. The residual risk is filename enumeration under a local concurrent-write
    /// attacker, which is outside the practical threat model for this sandbox.
    /// </remarks>
    public Task<(IReadOnlyList<SandboxDirectoryEntry>? Entries, SandboxReadFailure? Failure)> ListDirectoryAsync(
        string requestedPath, CancellationToken ct = default)
    {
        string resolvedPath;
        try
        {
            resolvedPath = SandboxPathValidator.ValidateAndResolve(requestedPath, _sandboxRoot);
        }
        catch (SandboxViolationException ex)
        {
            return Task.FromResult<(IReadOnlyList<SandboxDirectoryEntry>?, SandboxReadFailure?)>(
                (null, new SandboxReadFailure(SandboxFailureKind.Rejected, ex.Message)));
        }

        if (!Directory.Exists(resolvedPath))
        {
            return Task.FromResult<(IReadOnlyList<SandboxDirectoryEntry>?, SandboxReadFailure?)>(
                (null, new SandboxReadFailure(SandboxFailureKind.NotFound, $"Directory not found: {requestedPath}")));
        }

        try
        {
            ct.ThrowIfCancellationRequested();

            var entries = new List<SandboxDirectoryEntry>();
            var enumOptions = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint,
                ReturnSpecialDirectories = false,
            };

            foreach (var entry in new DirectoryInfo(resolvedPath).EnumerateFileSystemInfos("*", enumOptions))
            {
                ct.ThrowIfCancellationRequested();
                var kind = entry is DirectoryInfo ? SandboxEntryKind.Directory : SandboxEntryKind.File;
                entries.Add(new SandboxDirectoryEntry(entry.Name, kind));
            }

            return Task.FromResult<(IReadOnlyList<SandboxDirectoryEntry>?, SandboxReadFailure?)>(
                (entries, null));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult<(IReadOnlyList<SandboxDirectoryEntry>?, SandboxReadFailure?)>(
                (null, new SandboxReadFailure(SandboxFailureKind.Error, ex.Message)));
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

public enum SandboxEntryKind
{
    File,
    Directory
}

public sealed record SandboxDirectoryEntry(string Name, SandboxEntryKind Kind);

public sealed record ApplyPatchResult(
    bool Success,
    string? Reason,
    IReadOnlyList<HunkResult> Hunks);

public sealed record HunkResult(string Path, bool Success, string? Error);
