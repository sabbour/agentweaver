using System.Text.RegularExpressions;
using Agentweaver.EventsAndSessions;
using Xunit;

namespace Agentweaver.EventsAndSessions.Tests;

public sealed class RowLockRecordGuardContractTests
{
    [Fact]
    public async Task ForwardMigrationGuardsOnlyTheThreeAppendOnlySessionTables()
    {
        await using var resource = typeof(EventsAndSessionsMigrator).Assembly.GetManifestResourceStream(
            "Agentweaver.EventsAndSessions.Migrations.008_row_lock_record_updates.sql");
        Assert.NotNull(resource);
        using var reader = new StreamReader(resource);
        var sql = await reader.ReadToEndAsync();
        var tables = Regex.Matches(sql, @"BEFORE UPDATE ON \{schema\}\.([a-z_]+)")
            .Select(match => match.Groups[1].Value).ToArray();
        Assert.Equal(new[] { "sessions", "session_provider_bindings", "session_fork_lineage" }, tables);
        Assert.Equal(3, Regex.Matches(sql, "FOR EACH ROW EXECUTE FUNCTION").Count);
        Assert.Contains("RAISE EXCEPTION", sql);
        Assert.Contains("USING ERRCODE = '55000'", sql);
        Assert.Contains("REVOKE ALL ON FUNCTION {schema}.reject_row_lock_record_update() FROM PUBLIC", sql);
        Assert.DoesNotContain("DELETE", sql);
        Assert.DoesNotContain("TRUNCATE", sql);
        Assert.DoesNotMatch(@"\bRETURN\b", sql);
        Assert.DoesNotContain("INSERT INTO", sql);
        Assert.DoesNotContain("DROP ", sql);
    }
}
