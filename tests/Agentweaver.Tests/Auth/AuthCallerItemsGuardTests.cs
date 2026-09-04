using System.Runtime.CompilerServices;
using FluentAssertions;

namespace Agentweaver.Tests.Auth;

public sealed class AuthCallerItemsGuardTests
{
    [Fact]
    public void AuthenticatedCaller_IsNeverReadFromHttpContextItems()
    {
        var sourcePath = SourcePath();
        var repositoryRoot = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(sourcePath)!,
            "..",
            "..",
            ".."));
        var forbiddenSymbol = string.Concat("Caller", "ItemKey");
        var forbiddenLiteral = string.Concat("\"agentweaver.", "caller\"");

        var violations = new List<string>();
        foreach (var root in new[] { "apps", "tests" })
        {
            foreach (var file in Directory.EnumerateFiles(
                         Path.Combine(repositoryRoot, root),
                         "*.cs",
                         SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                var source = File.ReadAllText(file);
                if (source.Contains(forbiddenSymbol, StringComparison.Ordinal)
                    || source.Contains(forbiddenLiteral, StringComparison.Ordinal))
                {
                    violations.Add(Path.GetRelativePath(repositoryRoot, file));
                }
            }
        }

        violations.Should().BeEmpty(
            "CallerContext must be projected from ClaimsPrincipal through ICallerContextAccessor");
    }

    private static string SourcePath([CallerFilePath] string sourcePath = "") => sourcePath;
}
