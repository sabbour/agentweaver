using FluentAssertions;
using Agentweaver.Api.Git;

namespace Agentweaver.Tests.Git;

/// <summary>
/// Unit tests for WorkspaceFileEntryParser.ParseUnifiedDiffEntries.
///
/// Exercises the pure parsing function that extracts file-entry metadata from a
/// stored unified diff for terminal-state runs. No git repository or database
/// setup is required — every test operates on in-memory strings only.
///
/// Constitution coverage:
///   NFR-002 (no emoji in any system output) — FP-07 verifies status strings
///   and file paths produced by the parser contain no emoji characters.
///
/// COMPILE-TIME NOTE: WorkspaceFileEntryParser and WorkspaceFileEntry are types
/// that Tank will add to Agentweaver.Api.Git as part of the artifact-browser
/// feature branch. Tests will not compile until that code lands. This is
/// intentional: the tests document the expected contract.
/// </summary>
public sealed class WorkspaceFileEntryParserTests
{
    // =========================================================================
    // FP-01: empty diff string returns empty list
    // =========================================================================
    [Fact]
    public void Parse_EmptyString_ReturnsEmptyList()
    {
        var entries = WorkspaceFileEntryParser.ParseUnifiedDiffEntries(string.Empty);

        entries.Should().BeEmpty("an empty diff must produce no file entries");
    }

    // =========================================================================
    // FP-02: diff with one new file (--- /dev/null) returns one entry with
    // status "added"
    // =========================================================================
    [Fact]
    public void Parse_SingleAddedFile_ReturnsEntryWithAddedStatus()
    {
        const string diff =
            "diff --git a/hello.txt b/hello.txt\n" +
            "new file mode 100644\n" +
            "index 0000000..e965047\n" +
            "--- /dev/null\n" +
            "+++ b/hello.txt\n" +
            "@@ -0,0 +1 @@\n" +
            "+hello world\n";

        var entries = WorkspaceFileEntryParser.ParseUnifiedDiffEntries(diff);

        entries.Should().HaveCount(1);
        entries[0].Path.Should().Be("hello.txt");
        entries[0].Status.Should().Be("added");
    }

    // =========================================================================
    // FP-03: diff with one modified file (both sides have real paths) returns
    // one entry with status "modified"
    // =========================================================================
    [Fact]
    public void Parse_SingleModifiedFile_ReturnsEntryWithModifiedStatus()
    {
        const string diff =
            "diff --git a/readme.md b/readme.md\n" +
            "index abc1234..def5678 100644\n" +
            "--- a/readme.md\n" +
            "+++ b/readme.md\n" +
            "@@ -1,2 +1,3 @@\n" +
            " # Title\n" +
            "+New line added\n" +
            " Existing line\n";

        var entries = WorkspaceFileEntryParser.ParseUnifiedDiffEntries(diff);

        entries.Should().HaveCount(1);
        entries[0].Path.Should().Be("readme.md");
        entries[0].Status.Should().Be("modified");
    }

    // =========================================================================
    // FP-04: diff with one deleted file (+++ /dev/null) returns one entry with
    // status "deleted"
    // =========================================================================
    [Fact]
    public void Parse_SingleDeletedFile_ReturnsEntryWithDeletedStatus()
    {
        const string diff =
            "diff --git a/old.txt b/old.txt\n" +
            "deleted file mode 100644\n" +
            "index e965047..0000000\n" +
            "--- a/old.txt\n" +
            "+++ /dev/null\n" +
            "@@ -1 +0,0 @@\n" +
            "-old content\n";

        var entries = WorkspaceFileEntryParser.ParseUnifiedDiffEntries(diff);

        entries.Should().HaveCount(1);
        entries[0].Path.Should().Be("old.txt");
        entries[0].Status.Should().Be("deleted");
    }

    // =========================================================================
    // FP-05: diff with three files (added, modified, deleted) returns all
    // three entries with their correct status values
    // =========================================================================
    [Fact]
    public void Parse_MultipleFiles_ReturnsAllEntriesWithCorrectStatuses()
    {
        const string diff =
            "diff --git a/added.txt b/added.txt\n" +
            "new file mode 100644\n" +
            "index 0000000..e965047\n" +
            "--- /dev/null\n" +
            "+++ b/added.txt\n" +
            "@@ -0,0 +1 @@\n" +
            "+new content\n" +
            "diff --git a/modified.txt b/modified.txt\n" +
            "index abc1234..def5678 100644\n" +
            "--- a/modified.txt\n" +
            "+++ b/modified.txt\n" +
            "@@ -1 +1 @@\n" +
            "-old line\n" +
            "+new line\n" +
            "diff --git a/deleted.txt b/deleted.txt\n" +
            "deleted file mode 100644\n" +
            "index e965047..0000000\n" +
            "--- a/deleted.txt\n" +
            "+++ /dev/null\n" +
            "@@ -1 +0,0 @@\n" +
            "-removed content\n";

        var entries = WorkspaceFileEntryParser.ParseUnifiedDiffEntries(diff);

        entries.Should().HaveCount(3);
        entries.Should().ContainSingle(e => e.Path == "added.txt"   && e.Status == "added");
        entries.Should().ContainSingle(e => e.Path == "modified.txt" && e.Status == "modified");
        entries.Should().ContainSingle(e => e.Path == "deleted.txt"  && e.Status == "deleted");
    }

    // =========================================================================
    // FP-06: renamed file header (diff --git a/old-name.txt b/new-name.txt)
    // uses the destination path and reports status "modified". If the renamed
    // file was sourced from /dev/null it is "added" instead; this case tests
    // a plain similarity-100% rename with no content change.
    // =========================================================================
    [Fact]
    public void Parse_RenamedFile_UsesDestinationPathWithModifiedStatus()
    {
        const string diff =
            "diff --git a/old-name.txt b/new-name.txt\n" +
            "similarity index 100%\n" +
            "rename from old-name.txt\n" +
            "rename to new-name.txt\n";

        var entries = WorkspaceFileEntryParser.ParseUnifiedDiffEntries(diff);

        entries.Should().HaveCount(1);
        entries[0].Path.Should().Be("new-name.txt",
            "the destination path must be used for renamed files");
        entries[0].Status.Should().Be("modified",
            "a rename with real source and destination is classified as modified");
    }

    // =========================================================================
    // FP-07 (NFR-002 / Principle VII): status strings and paths produced by the
    // parser must never contain emoji. Covers SC-009 traceability requirement
    // that no non-text characters enter any system-controlled field.
    // =========================================================================
    [Fact]
    public void Parse_ResultEntries_ContainNoEmojiInStatusOrPath()
    {
        const string diff =
            "diff --git a/src/app.ts b/src/app.ts\n" +
            "new file mode 100644\n" +
            "index 0000000..1234567\n" +
            "--- /dev/null\n" +
            "+++ b/src/app.ts\n" +
            "@@ -0,0 +1 @@\n" +
            "+export {};\n" +
            "diff --git a/src/util.ts b/src/util.ts\n" +
            "index abc1234..def5678 100644\n" +
            "--- a/src/util.ts\n" +
            "+++ b/src/util.ts\n" +
            "@@ -1 +1 @@\n" +
            "-old\n" +
            "+new\n";

        var entries = WorkspaceFileEntryParser.ParseUnifiedDiffEntries(diff);

        entries.Should().NotBeEmpty();
        foreach (var entry in entries)
        {
            ContainsEmoji(entry.Status).Should().BeFalse(
                $"status '{entry.Status}' must not contain emoji (NFR-002)");
            ContainsEmoji(entry.Path).Should().BeFalse(
                $"path '{entry.Path}' must not contain emoji (NFR-002)");
        }
    }

    // Returns true when the string contains any emoji or symbol-range Unicode code point.
    private static bool ContainsEmoji(string value) =>
        value.EnumerateRunes().Any(r =>
            (r.Value >= 0x1F300 && r.Value <= 0x1FAFF) ||  // Misc/supplemental emoji
            (r.Value >= 0x2600  && r.Value <= 0x27BF));     // Misc symbols
}
