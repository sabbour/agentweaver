using System.Text.RegularExpressions;
using Xunit;

namespace Scaffolder.Api.Tests.Unit;

/// <summary>
/// T051: NFR-002 — No-emoji enforcement.
///
/// Scans all source files that produce system output (backend C# source,
/// CLI commands, Web UI TypeScript) for any Unicode emoji codepoint.
/// Fails the build on any match so that no emoji ever appears in:
///   - Event log payload serializations
///   - API response bodies
///   - CLI output helpers
///   - Web UI string constants
///
/// Principle VII: No emojis in any product output, code, logs, or generated docs.
/// </summary>
public sealed class NoEmojiAuditTests
{
    // Unicode ranges that cover all commonly used emoji codepoints.
    // Matches: Emoticons, Misc Symbols, Transport, Supplemental, Dingbats,
    //          Enclosed Alphanumeric Supplement, Misc Symbols And Pictographs,
    //          Symbols and Pictographs Extended-A, Flags, Regional Indicators.
    private static readonly Regex EmojiPattern = new(
        @"[\u2600-\u26FF]|[\u2700-\u27BF]|[\uD83C][\uDF00-\uDFFF]|" +
        @"[\uD83D][\uDC00-\uDE4F]|[\uD83D][\uDE80-\uDEFF]|" +
        @"[\uD83E][\uDD00-\uDDFF]|[\uD83E][\uDE00-\uDEFF]|" +
        @"[\u231A-\u231B]|[\u23E9-\u23F3]|[\u25AA-\u25AB]|[\u25B6]|[\u25C0]|" +
        @"[\u25FB-\u25FE]|[\u2614-\u2615]|[\u2648-\u2653]|[\u267F]|[\u2693]|" +
        @"[\u26A1]|[\u26AA-\u26AB]|[\u26BD-\u26BE]|[\u26C4-\u26C5]|[\u26CE]|" +
        @"[\u26D4]|[\u26EA]|[\u26F2-\u26F3]|[\u26F5]|[\u26FA]|[\u26FD]|[\u2702]|" +
        @"[\u2705]|[\u2708-\u270D]|[\u270F]|[\u2712]|[\u2714]|[\u2716]|[\u271D]|" +
        @"[\u2721]|[\u2728]|[\u2733-\u2734]|[\u2744]|[\u2747]|[\u274C]|[\u274E]|" +
        @"[\u2753-\u2755]|[\u2757]|[\u2763-\u2764]|[\u2795-\u2797]|[\u27A1]|" +
        @"[\u27B0]|[\u27BF]|[\u2934-\u2935]|[\u2B05-\u2B07]|[\u2B1B-\u2B1C]|" +
        @"[\u2B50]|[\u2B55]|[\u3030]|[\u303D]|[\u3297]|[\u3299]",
        RegexOptions.Compiled);

    private static readonly string RepoRoot = FindRepoRoot();

    [Fact]
    public void BackendCSharpFiles_ContainNoEmoji()
    {
        var backendRoot = Path.Combine(RepoRoot, "backend");
        AssertNoEmojiInDirectory(backendRoot, "*.cs");
    }

    [Fact]
    public void CliCSharpFiles_ContainNoEmoji()
    {
        var cliRoot = Path.Combine(RepoRoot, "clients", "cli");
        AssertNoEmojiInDirectory(cliRoot, "*.cs");
    }

    [Fact]
    public void WebTypeScriptFiles_ContainNoEmoji()
    {
        var webSrc = Path.Combine(RepoRoot, "clients", "web", "src");
        if (!Directory.Exists(webSrc))
        {
            // Web UI not yet scaffolded — skip gracefully
            return;
        }

        AssertNoEmojiInDirectory(webSrc, "*.tsx");
        AssertNoEmojiInDirectory(webSrc, "*.ts");
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private void AssertNoEmojiInDirectory(string directory, string pattern)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories))
        {
            // Skip build output
            if (file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                || file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            {
                continue;
            }

            var content = File.ReadAllText(file);
            var matches = EmojiPattern.Matches(content);

            foreach (Match match in matches)
            {
                var lineNumber = content[..match.Index].Count(c => c == '\n') + 1;
                violations.Add($"{file}:{lineNumber} — emoji codepoint U+{(int)match.Value[0]:X4} '{match.Value}'");
            }
        }

        if (violations.Count > 0)
        {
            var report = string.Join(Environment.NewLine, violations);
            throw new Xunit.Sdk.XunitException(
                $"NFR-002 violation: {violations.Count} emoji(s) found in source files " +
                $"(Principle VII — no emojis in product output, code, logs, or docs):{Environment.NewLine}{report}");
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Scaffolder.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Cannot locate repo root (Scaffolder.sln not found in any parent directory).");
    }
}
