using System.IO;
using System.Text.RegularExpressions;
using Agentweaver.Orchestrator;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class NativeChildRebindSchemaContractTests
{
    [Fact]
    public void NativeChildRebindSchemaContractMatchesEmbeddedMigration()
    {
        const string resourceName = "Agentweaver.Orchestrator.Migrations.051_native_child_rebind.sql";
        using var stream = typeof(CoordinationOwnerMigrator).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing migration resource '{resourceName}'.");
        using var reader = new StreamReader(stream);
        var sql = reader.ReadToEnd();

        foreach (var fragment in NativeChildRebindSchemaContract.RequiredMigrationFragments)
            Assert.Contains(fragment, sql, StringComparison.Ordinal);

        Assert.Equal(NativeChildRebindSchemaContract.UuidRegexOccurrences,
            CountLiteral(sql, NativeChildRebindSchemaContract.UuidRegex));
        Assert.Equal(NativeChildRebindSchemaContract.PositiveIntegerRegexOccurrences,
            CountLiteral(sql, NativeChildRebindSchemaContract.PositiveIntegerRegex));
        Assert.Equal(NativeChildRebindSchemaContract.Sha256RegexOccurrences,
            CountLiteral(sql, NativeChildRebindSchemaContract.Sha256Regex));
        Assert.Equal(NativeChildRebindSchemaContract.AndOperatorOccurrences,
            Regex.Matches(sql, @"\bAND\b", RegexOptions.CultureInvariant).Count);
        Assert.Equal(NativeChildRebindSchemaContract.OrOperatorOccurrences,
            Regex.Matches(sql, @"\bOR\b", RegexOptions.CultureInvariant).Count);
    }

    private static int CountLiteral(string text, string value) =>
        text.Split(value, StringSplitOptions.None).Length - 1;
}
