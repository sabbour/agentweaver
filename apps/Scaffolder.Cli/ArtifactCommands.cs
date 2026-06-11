using System.Text.RegularExpressions;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Scaffolder.Cli;

/// <summary>Implements the run artifacts interactive browser command.</summary>
public static class ArtifactCommands
{
    private static readonly string[] FilterChoices =
    {
        "All",
        "Committed",
        "Uncommitted",
        "Last commit"
    };

    private static readonly Dictionary<string, string> FilterValues = new(StringComparer.Ordinal)
    {
        ["All"] = "all",
        ["Committed"] = "committed",
        ["Uncommitted"] = "uncommitted",
        ["Last commit"] = "last-commit"
    };

    // Strips ANSI/CSI escape sequences to prevent terminal escape injection.
    private static readonly Regex AnsiEscapePattern =
        new(@"\x1B\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);

    public static async Task<int> ArtifactsAsync(ApiClient api, string runId, CancellationToken ct)
    {
        RunDetail run;
        try
        {
            run = await api.GetRunAsync(runId, ct);
        }
        catch (ApiException ex)
        {
            PrintApiError(ex);
            return 1;
        }

        if (string.Equals(run.Status, "in_progress", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine("[grey]This run is still in progress. File list reflects current state.[/]");
        }

        // Initial fetch to check for any files
        IReadOnlyList<WorkspaceFileEntry> initialFiles;
        try
        {
            initialFiles = await api.GetRunFilesAsync(runId, "all", ct);
        }
        catch (ApiException ex)
        {
            PrintApiError(ex);
            return 1;
        }

        if (initialFiles.Count == 0)
        {
            AnsiConsole.MarkupLine("No changes in this run.");
            return 0;
        }

        // Select filter
        var filterChoice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Filter:")
                .AddChoices(FilterChoices));

        var filterValue = FilterValues[filterChoice];

        // Fetch with selected filter
        IReadOnlyList<WorkspaceFileEntry> files;
        try
        {
            files = await api.GetRunFilesAsync(runId, filterValue, ct);
        }
        catch (ApiException ex)
        {
            PrintApiError(ex);
            return 1;
        }

        if (files.Count == 0)
        {
            AnsiConsole.MarkupLine("No changes match the selected filter.");
            return 0;
        }

        // File selection loop
        while (true)
        {
            var selected = AnsiConsole.Prompt(
                new SelectionPrompt<WorkspaceFileEntry>()
                    .Title("Select a file:")
                    .UseConverter(entry => $"[{entry.Status}] {entry.Path}")
                    .AddChoices(files));

            WorkspaceFileDiff fileDiff;
            try
            {
                fileDiff = await api.GetRunFileDiffAsync(runId, selected.Path, ct);
            }
            catch (ApiException ex)
            {
                PrintApiError(ex);
                return 1;
            }

            if (fileDiff.IsBinary)
            {
                AnsiConsole.MarkupLine("Binary file — diff not available.");
            }
            else if (string.IsNullOrEmpty(fileDiff.Diff))
            {
                AnsiConsole.MarkupLine("[grey]No diff content.[/]");
            }
            else
            {
                RenderDiff(fileDiff.Diff);
            }

            AnsiConsole.WriteLine();
            var viewAnother = AnsiConsole.Confirm("View another file?", defaultValue: false);
            if (!viewAnother)
            {
                break;
            }
        }

        return 0;
    }

    private static void RenderDiff(string diff)
    {
        var rows = new List<IRenderable>();
        foreach (var rawLine in diff.Replace("\r\n", "\n").Split('\n'))
        {
            // Strip ANSI escape sequences before rendering to prevent escape injection.
            var line = AnsiEscapePattern.Replace(rawLine, string.Empty);
            var escaped = Markup.Escape(line);

            string markup;
            if (line.StartsWith("+++", StringComparison.Ordinal) ||
                line.StartsWith("---", StringComparison.Ordinal) ||
                line.StartsWith("diff --git", StringComparison.Ordinal) ||
                line.StartsWith("index ", StringComparison.Ordinal))
            {
                markup = $"[dim]{escaped}[/]";
            }
            else if (line.StartsWith('+'))
            {
                markup = $"[green]{escaped}[/]";
            }
            else if (line.StartsWith('-'))
            {
                markup = $"[red]{escaped}[/]";
            }
            else if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                markup = $"[dim italic]{escaped}[/]";
            }
            else
            {
                markup = escaped;
            }

            rows.Add(new Markup(markup));
        }

        AnsiConsole.Write(new Panel(new Rows(rows)).Border(BoxBorder.Rounded));
    }

    private static void PrintApiError(ApiException ex)
    {
        AnsiConsole.MarkupLine(
            $"[red]API error {ex.StatusCode}:[/] {Markup.Escape(ex.Body)}");
    }
}
