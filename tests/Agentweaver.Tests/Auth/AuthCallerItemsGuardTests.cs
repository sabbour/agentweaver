using System.Runtime.CompilerServices;
using FluentAssertions;

namespace Agentweaver.Tests.Auth;

public sealed class AuthCallerItemsGuardTests
{
    [Fact]
    public void AuthenticatedCaller_IsNotReadFromHttpContextItemsOutsideLegacyWriter()
    {
        var sourcePath = SourcePath();
        var repositoryRoot = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(sourcePath)!,
            "..",
            "..",
            ".."));
        var legacyWriter = Path.GetFullPath(Path.Combine(
            repositoryRoot,
            "apps",
            "Agentweaver.Api",
            "Security",
            "ApiKeyAuthMiddleware.cs"));
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
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || string.Equals(Path.GetFullPath(file), legacyWriter, StringComparison.OrdinalIgnoreCase))
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
            "CallerContext must be projected from ClaimsPrincipal; only the legacy middleware may retain the temporary Items write");
    }

    private static string SourcePath([CallerFilePath] string sourcePath = "") => sourcePath;
}
