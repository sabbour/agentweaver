using System.Text;
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
                    .UseConverter(entry => $"[{entry.Status}] {StripTerminalEscapes(entry.Path)}")
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
            // Strip all terminal escape sequences before rendering to prevent escape injection.
            var line = StripTerminalEscapes(rawLine);
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

    // Strips all terminal escape sequences to prevent terminal escape injection.
    // Handles ESC-based sequences (CSI, OSC, DCS, APC, PM, SOS, Fe two-char),
    // 8-bit C1 controls (U+0080-U+009F), C0 controls (except tab/LF/CR), and DEL.
    private static string StripTerminalEscapes(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        var sb = new StringBuilder(input.Length);
        int i = 0;
        while (i < input.Length)
        {
            char c = input[i];

            if (c == '\x1B')
            {
                // ESC — consume the escape sequence without appending anything.
                i++;
                if (i >= input.Length)
                    break;

                char next = input[i];

                if (next == '[')
                {
                    // CSI: ESC [ ... final-byte (0x40-0x7E)
                    i++;
                    while (i < input.Length && (input[i] < 0x40 || input[i] > 0x7E))
                        i++;
                    if (i < input.Length)
                        i++; // consume final byte
                }
                else if (next == ']' || next == 'P' || next == 'X' || next == '^' || next == '_')
                {
                    // OSC / DCS / SOS / PM / APC: ESC introducer ... ST or BEL
                    i++;
                    while (i < input.Length)
                    {
                        char sc = input[i];
                        if (sc == '\x07')
                        {
                            // BEL terminates
                            i++;
                            break;
                        }
                        if (sc == '\x1B' && i + 1 < input.Length && input[i + 1] == '\\')
                        {
                            // ST = ESC \ terminates
                            i += 2;
                            break;
                        }
                        i++;
                    }
                }
                else if (next >= 0x40 && next <= 0x5F)
                {
                    // Fe two-char sequence: ESC + byte in 0x40-0x5F (e.g., ESC c, ESC \)
                    i++;
                }
                else
                {
                    // Unknown ESC sequence — skip the following character.
                    i++;
                }
            }
            else if (c >= '\x80' && c <= '\x9F')
            {
                // 8-bit C1 control — skip.
                i++;
            }
            else if (c == '\x7F')
            {
                // DEL — skip.
                i++;
            }
            else if (c < '\x20' && c != '\x09' && c != '\x0A' && c != '\x0D')
            {
                // C0 control other than tab, line feed, carriage return — skip.
                i++;
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }

        return sb.ToString();
    }

    private static void PrintApiError(ApiException ex)
    {
        AnsiConsole.MarkupLine(
            $"[red]API error {ex.StatusCode}:[/] {Markup.Escape(ex.Body)}");
    }
}
