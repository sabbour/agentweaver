using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Scaffolder.Cli;

/// <summary>Implements the run submit, watch, review, and show commands.</summary>
public static class RunCommands
{
    private static readonly string[] ModelSources =
    {
        "github-copilot",
        "microsoft-foundry"
    };

    public static async Task<int> SubmitAsync(ApiClient api, CancellationToken ct)
    {
        var repositoryPath = Path.GetFullPath(AnsiConsole.Ask<string>("Repository path:"));
        var originatingBranch = AnsiConsole.Ask<string>("Originating branch:");

        AnsiConsole.MarkupLine("[grey]Task description (end with an empty line):[/]");
        var task = ReadMultiLine();
        if (string.IsNullOrWhiteSpace(task))
        {
            AnsiConsole.MarkupLine("[red]Task description must not be empty.[/]");
            return 1;
        }

        var modelSource = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Model source:")
                .AddChoices(ModelSources));

        var request = new SubmitRunRequest
        {
            RepositoryPath = repositoryPath,
            OriginatingBranch = originatingBranch,
            Task = task,
            ModelSource = modelSource
        };

        SubmitRunResponse response;
        try
        {
            response = await api.SubmitRunAsync(request, ct);
        }
        catch (ApiException ex)
        {
            PrintApiError(ex);
            return 1;
        }

        AnsiConsole.MarkupLine(
            $"[green]Run submitted.[/] id: {Markup.Escape(response.RunId)} status: {Markup.Escape(response.Status)}");
        AnsiConsole.WriteLine();

        return await WatchAsync(api, response.RunId, interactive: true, ct);
    }

    public static async Task<int> WatchAsync(
        ApiClient api, string runId, bool interactive, CancellationToken ct)
    {
        var sse = api.CreateSseClient();
        var seen = new HashSet<int>();

        AnsiConsole.MarkupLine($"[grey]Watching run {Markup.Escape(runId)}[/]");

        try
        {
            await foreach (var frame in sse.StreamAsync(api.StreamUrl(runId), ct))
            {
                RunEvent? evt;
                try
                {
                    evt = JsonSerializer.Deserialize<RunEvent>(frame.Data, JsonConfig.Options);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (evt is null || string.IsNullOrEmpty(evt.Type))
                {
                    continue;
                }

                if (!seen.Add(evt.Sequence))
                {
                    continue;
                }

                AnsiConsole.MarkupLine(EventRenderer.Format(evt));

                if (evt.Type == "review.requested" && interactive)
                {
                    await PromptPendingReviewAsync(api, runId, ct);
                }

                if (IsClientTerminal(evt.Type))
                {
                    break;
                }
            }
        }
        catch (ApiException ex)
        {
            PrintApiError(ex);
            return 1;
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[grey]Stopped watching.[/]");
            return 0;
        }

        return 0;
    }

    public static async Task<int> ReviewAsync(ApiClient api, string runId, CancellationToken ct)
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

        if (string.IsNullOrEmpty(run.Diff))
        {
            AnsiConsole.MarkupLine(
                $"[yellow]No diff available for run {Markup.Escape(runId)} (status: {Markup.Escape(run.Status)}).[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[grey]Proposed changes:[/]");
            RenderDiff(run.Diff);
            AnsiConsole.WriteLine();
        }

        var approve = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Decision:")
                .AddChoices("Approve", "Decline"));
        var approved = approve == "Approve";

        ReviewSubmitResponse result;
        try
        {
            result = await api.SubmitReviewAsync(runId, approved, ct);
        }
        catch (RetriableReviewException ex)
        {
            AnsiConsole.MarkupLine($"[yellow]Approve could not complete:[/] {Markup.Escape(ex.ServerMessage)}");
            AnsiConsole.MarkupLine("[grey]Fix the issue above and run approve again.[/]");
            return 2;
        }
        catch (ApiException ex)
        {
            PrintApiError(ex);
            return 1;
        }

        if (string.Equals(result.Status, "merge_failed", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine("[red]Merge failed.[/]");
            if (!string.IsNullOrEmpty(result.MergeResult))
                AnsiConsole.MarkupLine($"Reason: {Markup.Escape(result.MergeResult)}");
            AnsiConsole.MarkupLine("[grey]The worktree has been preserved for manual resolution.[/]");
            return 1;
        }

        if (string.Equals(result.Status, "merged", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine("[green]Merged successfully.[/]");
            if (!string.IsNullOrEmpty(result.MergeResult))
                AnsiConsole.MarkupLine($"merge result: {Markup.Escape(result.MergeResult)}");
        }
        else
        {
            AnsiConsole.MarkupLine(
                $"[green]Review submitted.[/] status: {Markup.Escape(result.Status)}");
            if (!string.IsNullOrEmpty(result.MergeResult))
                AnsiConsole.MarkupLine($"merge result: {Markup.Escape(result.MergeResult)}");
        }

        return 0;
    }

    public static async Task<int> ShowAsync(ApiClient api, string runId, CancellationToken ct)
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

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Field");
        table.AddColumn("Value");
        table.AddRow("Run id", Markup.Escape(run.RunId));
        table.AddRow("Status", Markup.Escape(run.Status));
        table.AddRow("Model source", Markup.Escape(run.ModelSource));
        table.AddRow("Started at", Markup.Escape(run.StartedAt));
        table.AddRow("Ended at", Markup.Escape(run.EndedAt ?? "-"));
        table.AddRow("Step count", run.StepCount.ToString());
        table.AddRow("Has diff", string.IsNullOrEmpty(run.Diff) ? "no" : "yes");

        AnsiConsole.Write(table);
        return 0;
    }

    /// <summary>
    /// Checks the run's current state and, if it is still awaiting a decision,
    /// prompts for approval and submits it. The decision is recorded by the API.
    /// </summary>
    private static async Task PromptPendingReviewAsync(ApiClient api, string runId, CancellationToken ct)
    {
        RunDetail run;
        try
        {
            run = await api.GetRunAsync(runId, ct);
        }
        catch (ApiException)
        {
            return;
        }

        if (!string.Equals(run.Status, "awaiting_review", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var approved = AnsiConsole.Confirm("Approve changes?", defaultValue: false);

        try
        {
            var result = await api.SubmitReviewAsync(runId, approved, ct);
            if (string.Equals(result.Status, "merge_failed", StringComparison.OrdinalIgnoreCase))
            {
                AnsiConsole.MarkupLine("[red]Merge failed.[/]");
                if (!string.IsNullOrEmpty(result.MergeResult))
                    AnsiConsole.MarkupLine($"[grey]Reason: {Markup.Escape(result.MergeResult)}[/]");
                AnsiConsole.MarkupLine("[grey]The worktree has been preserved for manual resolution.[/]");
            }
            else
            {
                AnsiConsole.MarkupLine(
                    $"[grey]Review recorded:[/] {Markup.Escape(result.Status)}");
                if (!string.IsNullOrEmpty(result.MergeResult))
                    AnsiConsole.MarkupLine($"[grey]merge result:[/] {Markup.Escape(result.MergeResult)}");
            }
        }
        catch (RetriableReviewException ex)
        {
            AnsiConsole.MarkupLine($"[yellow]Approve could not complete:[/] {Markup.Escape(ex.ServerMessage)}");
            AnsiConsole.MarkupLine("[grey]Fix the issue above and run approve again.[/]");
        }
        catch (ApiException ex)
        {
            PrintApiError(ex);
        }
    }

    private static void RenderDiff(string diff)
    {
        var rows = new List<IRenderable>();
        foreach (var line in diff.Replace("\r\n", "\n").Split('\n'))
        {
            var escaped = Markup.Escape(line);
            string markup;
            if (line.StartsWith("+++", StringComparison.Ordinal) ||
                line.StartsWith("---", StringComparison.Ordinal))
            {
                markup = $"[bold]{escaped}[/]";
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
                markup = $"[cyan]{escaped}[/]";
            }
            else
            {
                markup = escaped;
            }

            rows.Add(new Markup(markup));
        }

        AnsiConsole.Write(new Panel(new Rows(rows)).Border(BoxBorder.Rounded));
    }

    private static string ReadMultiLine()
    {
        var lines = new List<string>();
        while (true)
        {
            var line = Console.ReadLine();
            if (string.IsNullOrEmpty(line))
            {
                break;
            }

            lines.Add(line);
        }

        return string.Join("\n", lines);
    }

    private static bool IsClientTerminal(string type) =>
        type is "run.failed"
            or "run.bounded"
            or "merge.completed"
            or "merge.failed"
            or "review.declined";

    private static void PrintApiError(ApiException ex)
    {
        AnsiConsole.MarkupLine(
            $"[red]API error {ex.StatusCode}:[/] {Markup.Escape(ex.Body)}");
    }
}
