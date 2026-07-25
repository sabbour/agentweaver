namespace Agentweaver.SandboxFs;

/// <summary>
/// Defense-in-depth guard that scans a shell command's TEXT for absolute paths that
/// reach into a shared, cross-run mount (the Kata AgentHost mounts one RWX
/// <c>/workspace</c> PVC shared by every run across every project) but fall OUTSIDE the
/// current run's own allowed roots.
///
/// <para>
/// This closes the gap called out in #476: <see cref="SandboxPathValidator"/> only
/// validates the DECLARED working directory of a command, while
/// <c>PassthroughExecutor</c> (selected in Kata isolation mode) never consumes the
/// per-run filesystem policy at all. A command such as
/// <c>cat /workspace/&lt;other-project&gt;/secrets.txt</c> or
/// <c>git -C /workspace/&lt;other-project&gt; ...</c> keeps its working directory inside the
/// run's own tree yet reads/writes a sibling project through an absolute path.
/// </para>
///
/// <para>
/// This is a MITIGATION, not a full isolation boundary. A command-text filter cannot
/// see every obfuscation (variable indirection, base64-decoded paths, here-docs), so it
/// is layered on top of — not a replacement for — real per-run volume isolation, which
/// remains the tracked follow-up. It deliberately targets only the shared-mount root so
/// legitimate reads of system paths (<c>/usr</c>, <c>/bin</c>, <c>/etc</c>, the pod-local
/// <c>/local-workspace</c> scratch, <c>$HOME</c>) are never flagged.
/// </para>
/// </summary>
public static class SharedWorkspacePathGuard
{
    /// <summary>
    /// Environment variable that overrides the protected shared-mount roots (comma-,
    /// semicolon-, or whitespace-separated absolute POSIX paths). Defaults to
    /// <see cref="DefaultProtectedRoots"/> when unset/empty.
    /// </summary>
    public const string ProtectedRootsEnvVar = "AGENTWEAVER_PROTECTED_SHARED_ROOTS";

    /// <summary>
    /// The shared RWX workspace PVC mount point used by every AgentHost pod
    /// (k8s/base/sandbox-template-agenthost.yaml). Any absolute reference under this root
    /// that is not the run's own subtree is a cross-run/cross-project access attempt.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultProtectedRoots = new[] { "/workspace" };

    /// <summary>
    /// Inspects <paramref name="commandLine"/> for absolute path tokens that resolve under
    /// a protected shared-mount root but outside every entry in
    /// <paramref name="allowedRoots"/>. Returns <c>(true, null)</c> when the command is
    /// safe, or <c>(false, reason)</c> naming the first offending token.
    /// </summary>
    /// <param name="commandLine">The raw shell command text to scan.</param>
    /// <param name="allowedRoots">
    /// The run's own allowed roots (working directory + filesystem-policy read/write and
    /// read-only roots). Absolute paths inside any of these are permitted even when they
    /// sit under a protected root (e.g. the run's own <c>/workspace/&lt;worktree&gt;</c> in
    /// shared-execution mode).
    /// </param>
    /// <param name="protectedRoots">
    /// Shared-mount roots to protect. When <c>null</c>, resolved from
    /// <see cref="ProtectedRootsEnvVar"/> or <see cref="DefaultProtectedRoots"/>.
    /// </param>
    public static (bool Allowed, string? Reason) Inspect(
        string commandLine,
        IReadOnlyList<string> allowedRoots,
        IReadOnlyList<string>? protectedRoots = null)
    {
        if (string.IsNullOrEmpty(commandLine))
            return (true, null);

        var protectedList = NormalizeRoots(protectedRoots ?? ResolveDefaultProtectedRoots());
        if (protectedList.Count == 0)
            return (true, null);

        var allowedList = NormalizeRoots(allowedRoots);

        foreach (var candidate in ExtractAbsolutePosixTokens(commandLine))
        {
            var normalized = NormalizePosix(candidate);
            if (normalized is null)
                continue;

            if (!IsUnderAny(normalized, protectedList))
                continue;

            if (IsUnderAny(normalized, allowedList))
                continue;

            var offendingRoot = protectedList.First(r => IsUnder(normalized, r));
            return (false,
                $"Command references shared-mount path '{candidate}' (resolves to '{normalized}') " +
                $"under protected root '{offendingRoot}' outside this run's allowed workspace. " +
                "Cross-run/cross-project absolute paths are not permitted.");
        }

        return (true, null);
    }

    private static IReadOnlyList<string> ResolveDefaultProtectedRoots()
    {
        var raw = Environment.GetEnvironmentVariable(ProtectedRootsEnvVar);
        if (string.IsNullOrWhiteSpace(raw))
            return DefaultProtectedRoots;

        var parsed = raw
            .Split(new[] { ',', ';', ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.StartsWith('/'))
            .ToArray();

        return parsed.Length == 0 ? DefaultProtectedRoots : parsed;
    }

    /// <summary>
    /// Normalizes a set of roots to canonical POSIX form, keeping only absolute POSIX
    /// paths (the protected roots are POSIX mount points inside the Linux pod). Windows
    /// roots supplied on a dev machine are ignored because they can never collide with a
    /// POSIX shared-mount root.
    /// </summary>
    private static List<string> NormalizeRoots(IReadOnlyList<string> roots)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;
            var posix = ToPosix(root);
            if (!posix.StartsWith('/'))
                continue;
            var normalized = NormalizePosix(posix);
            if (normalized is not null && seen.Add(normalized))
                result.Add(normalized);
        }
        return result;
    }

    private static string ToPosix(string path) => path.Replace('\\', '/');

    private static bool IsUnderAny(string normalizedPath, IReadOnlyList<string> roots) =>
        roots.Any(r => IsUnder(normalizedPath, r));

    private static bool IsUnder(string normalizedPath, string normalizedRoot)
    {
        if (string.Equals(normalizedPath, normalizedRoot, StringComparison.Ordinal))
            return true;
        var prefix = normalizedRoot.EndsWith('/') ? normalizedRoot : normalizedRoot + "/";
        return normalizedPath.StartsWith(prefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Lexically normalizes an absolute POSIX path, collapsing <c>.</c> and <c>..</c>
    /// segments so obfuscation via traversal (e.g. <c>/local-workspace/../workspace/x</c>
    /// or <c>/workspace/./other</c>) is resolved before the containment check. Returns
    /// <c>null</c> for non-absolute input.
    /// </summary>
    private static string? NormalizePosix(string path)
    {
        var posix = ToPosix(path);
        if (!posix.StartsWith('/'))
            return null;

        var stack = new List<string>();
        foreach (var segment in posix.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
                continue;
            if (segment == "..")
            {
                if (stack.Count > 0)
                    stack.RemoveAt(stack.Count - 1);
                continue;
            }
            stack.Add(segment);
        }

        return "/" + string.Join('/', stack);
    }

    /// <summary>
    /// Extracts candidate absolute POSIX path tokens (substrings starting with <c>/</c>)
    /// from a shell command, stopping each token at whitespace or a shell metacharacter so
    /// quoting (<c>"/workspace/x"</c>), flag assignment (<c>--git-dir=/workspace/x</c>), and
    /// PATH-style colon lists (<c>/a:/workspace/x</c>) all surface the embedded path.
    /// </summary>
    private static IEnumerable<string> ExtractAbsolutePosixTokens(string commandLine)
    {
        var start = -1;
        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            if (c == '/')
            {
                if (start < 0)
                    start = i;
                continue;
            }

            if (start >= 0 && IsPathTerminator(c))
            {
                if (i > start)
                    yield return commandLine.Substring(start, i - start);
                start = -1;
            }
        }

        if (start >= 0 && commandLine.Length > start)
            yield return commandLine.Substring(start);
    }

    private static bool IsPathTerminator(char c) =>
        char.IsWhiteSpace(c) ||
        c is '\'' or '"' or '`' or ':' or ';' or ',' or '|' or '&' or
             '(' or ')' or '<' or '>' or '=' or '$' or '*' or '?' or '\\' or '\0';
}
