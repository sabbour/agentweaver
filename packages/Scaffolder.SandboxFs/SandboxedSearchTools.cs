using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.FileSystemGlobbing;

namespace Scaffolder.SandboxFs;

/// <summary>
/// Match produced by <see cref="SandboxedSearchTools.GrepSearchAsync"/>.
/// </summary>
public sealed record GrepMatch(string RelativePath, int LineNumber, string LineContent);

/// <summary>
/// Sandboxed search operations. All enumeration is constrained to the sandbox
/// root and never follows reparse points (symlinks or junctions).
/// </summary>
public sealed class SandboxedSearchTools
{
    private readonly string _sandboxRoot;

    private static readonly HashSet<string> ExcludedDirNames =
        new(StringComparer.OrdinalIgnoreCase) { ".git", "node_modules", "bin", "obj", ".vs" };

    private static readonly EnumerationOptions NoReparseEnumOptions = new()
    {
        RecurseSubdirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false,
    };

    public SandboxedSearchTools(string sandboxRoot)
    {
        _sandboxRoot = Path.GetFullPath(sandboxRoot);

        // Seraph A2: reject if the sandbox root itself is a reparse point
        if (Directory.Exists(_sandboxRoot))
        {
            var rootInfo = new DirectoryInfo(_sandboxRoot);
            if (rootInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new SandboxViolationException(_sandboxRoot, _sandboxRoot,
                    "sandbox root is a symbolic link or junction; refusing to operate");
        }
    }

    /// <summary>
    /// Searches files under the sandbox root for lines matching <paramref name="pattern"/>.
    /// Uses <paramref name="includePattern"/> (glob) to restrict which files are examined.
    /// Results are capped at <paramref name="maxResults"/>.
    /// </summary>
    public async Task<IReadOnlyList<GrepMatch>> GrepSearchAsync(
        string pattern,
        bool isRegex,
        string? includePattern,
        int maxResults,
        bool caseSensitive,
        CancellationToken ct)
    {
        Regex? regex = null;
        if (isRegex)
        {
            var regexOptions = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
            regex = new Regex(pattern, regexOptions, TimeSpan.FromSeconds(5));
        }

        Matcher? nameMatcher = null;
        if (!string.IsNullOrWhiteSpace(includePattern))
        {
            nameMatcher = new Matcher(StringComparison.OrdinalIgnoreCase);
            nameMatcher.AddInclude(includePattern);
        }

        var results = new List<GrepMatch>();
        var queue = new Queue<string>();
        queue.Enqueue(_sandboxRoot);

        while (queue.Count > 0 && results.Count < maxResults)
        {
            ct.ThrowIfCancellationRequested();
            var dir = queue.Dequeue();

            foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", NoReparseEnumOptions))
            {
                ct.ThrowIfCancellationRequested();

                if (entry is DirectoryInfo subDir)
                {
                    if (!ExcludedDirNames.Contains(subDir.Name))
                        queue.Enqueue(subDir.FullName);
                    continue;
                }

                if (entry is not FileInfo file)
                    continue;

                var relativePath = Path.GetRelativePath(_sandboxRoot, file.FullName);
                var normalizedRelative = relativePath.Replace('\\', '/');

                if (nameMatcher is not null && !nameMatcher.Match(normalizedRelative).HasMatches)
                    continue;

                await MatchFileAsync(file.FullName, relativePath, _sandboxRoot, pattern, regex,
                    caseSensitive, maxResults, results, ct);

                if (results.Count >= maxResults)
                    break;
            }
        }

        return results;
    }

    private static async Task MatchFileAsync(
        string absolutePath,
        string relativePath,
        string sandboxRoot,
        string literalPattern,
        Regex? regex,
        bool caseSensitive,
        int maxResults,
        List<GrepMatch> results,
        CancellationToken ct)
    {
        try
        {
            await using var fs = new FileStream(absolutePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 4096, useAsync: true);

            SandboxPathValidator.VerifyOpenedHandle(fs.SafeFileHandle, sandboxRoot, relativePath);

            using var reader = new StreamReader(fs, Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true);

            int lineNumber = 0;
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null && results.Count < maxResults)
            {
                lineNumber++;
                bool matched = regex is not null
                    ? regex.IsMatch(line)
                    : line.Contains(literalPattern,
                        caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

                if (matched)
                    results.Add(new GrepMatch(relativePath, lineNumber, line));
            }
        }
        catch (SandboxViolationException)
        {
            // File was swapped after enumeration — skip without poisoning the entire search.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Skip files that cannot be opened.
        }
    }

    /// <summary>
    /// Returns relative file paths under the sandbox root matching
    /// <paramref name="globPattern"/>. Results are capped at
    /// <paramref name="maxResults"/>. Rejects traversal patterns.
    /// </summary>
    public IReadOnlyList<string> FileSearch(
        string globPattern,
        int maxResults,
        CancellationToken ct)
    {
        if (globPattern.Contains("..", StringComparison.Ordinal) ||
            globPattern.StartsWith("/", StringComparison.Ordinal) ||
            globPattern.StartsWith("\\", StringComparison.Ordinal) ||
            Path.IsPathRooted(globPattern))
        {
            return [];
        }

        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude(globPattern);

        var results = new List<string>();
        var queue = new Queue<string>();
        queue.Enqueue(_sandboxRoot);

        while (queue.Count > 0 && results.Count < maxResults)
        {
            if (ct.IsCancellationRequested)
                break;

            var dir = queue.Dequeue();

            foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", NoReparseEnumOptions))
            {
                if (ct.IsCancellationRequested)
                    break;

                if (entry is DirectoryInfo subDir)
                {
                    if (!ExcludedDirNames.Contains(subDir.Name))
                        queue.Enqueue(subDir.FullName);
                    continue;
                }

                if (entry is not FileInfo file)
                    continue;

                var relativePath = Path.GetRelativePath(_sandboxRoot, file.FullName);
                var normalizedRelative = relativePath.Replace('\\', '/');

                if (matcher.Match(normalizedRelative).HasMatches)
                {
                    results.Add(relativePath);
                    if (results.Count >= maxResults)
                        break;
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Async overload of <see cref="FileSearch"/> for callers that prefer Task-based API.
    /// </summary>
    public Task<IReadOnlyList<string>> FileSearchAsync(
        string globPattern,
        int maxResults,
        CancellationToken ct)
        => Task.FromResult(FileSearch(globPattern, maxResults, ct));
}
