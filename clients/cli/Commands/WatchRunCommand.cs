using Scaffolder.Cli.Api;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Scaffolder.Cli.Commands;

/// <summary>
/// T056: `run watch` — connect to the SSE stream and render each event.
/// Reconnects on disconnect using Last-Event-ID.
/// Deduplicates re-delivered events by sequence.
/// Exits on terminal lifecycle event.
/// </summary>
public sealed class WatchRunCommand : AsyncCommand<WatchRunCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<RUN_ID>")]
        public Guid RunId { get; init; }

        [CommandOption("--api <URL>")]
        public string ApiBaseUrl { get; init; } = "http://localhost:3000";

        [CommandOption("--from <SEQUENCE>")]
        public long FromSequence { get; init; } = 0;
    }

    private static readonly HashSet<string> TerminalEventTypes =
    [
        "run.failed", "run.bounded", "review.declined",
        "merge.completed", "merge.failed"
    ];

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        using var client = new ScaffolderApiClient(settings.ApiBaseUrl);
        using var cts = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        AnsiConsole.MarkupLine($"[dim]Watching run {settings.RunId}... (Ctrl+C to stop)[/]");
        AnsiConsole.WriteLine();

        var lastSeen = settings.FromSequence;
        var seen = new HashSet<long>();

        try
        {
            await foreach (var evt in client.StreamRunEventsAsync(
                settings.RunId, lastSeen, cts.Token))
            {
                // Deduplicate at-least-once re-deliveries
                if (!seen.Add(evt.Sequence))
                {
                    continue;
                }

                lastSeen = evt.Sequence;
                RenderEvent(evt);

                if (TerminalEventTypes.Contains(evt.EventType))
                {
                    AnsiConsole.MarkupLine("[dim]Stream closed (terminal event received).[/]");
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[dim]Watch cancelled.[/]");
        }
        catch (ApiException ex)
        {
            AnsiConsole.MarkupLine($"[red]API error {ex.StatusCode}:[/] {ex.Body}");
            return 1;
        }

        return 0;
    }

    private static void RenderEvent(SseEvent evt)
    {
        var label = evt.EventType switch
        {
            "run.started" => "[green]RUN STARTED[/]",
            "run.completed" => "[green]RUN COMPLETED[/]",
            "run.failed" => "[red]RUN FAILED[/]",
            "run.bounded" => "[yellow]RUN BOUNDED[/]",
            "agent.message" => "[cyan]AGENT MSG[/]",
            "tool.call" => "[blue]TOOL CALL[/]",
            "tool.result" => "[blue]TOOL RESULT[/]",
            "tool.rejected" => "[yellow]TOOL REJECTED[/]",
            "tool.error" => "[red]TOOL ERROR[/]",
            "review.requested" => "[magenta]REVIEW REQUESTED[/]",
            "review.approved" => "[green]REVIEW APPROVED[/]",
            "review.declined" => "[red]REVIEW DECLINED[/]",
            "merge.completed" => "[green]MERGE COMPLETED[/]",
            "merge.failed" => "[red]MERGE FAILED[/]",
            _ => $"[dim]{evt.EventType.ToUpperInvariant()}[/]"
        };

        AnsiConsole.MarkupLine($"[dim]{evt.Sequence,4}[/] {label} {evt.Data}");
    }
}



