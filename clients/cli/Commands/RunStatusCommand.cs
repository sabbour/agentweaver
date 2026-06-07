using Scaffolder.Cli.Api;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Scaffolder.Cli.Commands;

/// <summary>
/// T055: `run status` — display the current status of a run.
/// GET /runs/{runId}; renders status, timestamps, and submittedBy.
/// </summary>
public sealed class RunStatusCommand : AsyncCommand<RunStatusCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<RUN_ID>")]
        public Guid RunId { get; init; }

        [CommandOption("--api <URL>")]
        public string ApiBaseUrl { get; init; } = "http://localhost:3000";
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        using var client = new ScaffolderApiClient(settings.ApiBaseUrl);

        try
        {
            var run = await client.GetRunAsync(settings.RunId);
            if (run is null)
            {
                AnsiConsole.MarkupLine("[red]Run not found.[/]");
                return 1;
            }

            var table = new Table()
                .Title($"Run {run.Id}")
                .AddColumn("Field")
                .AddColumn("Value")
                .AddRow("Status", run.Status)
                .AddRow("Branch", run.OriginatingBranch)
                .AddRow("Model Source", run.ModelSource)
                .AddRow("Submitted By", run.SubmittedBy)
                .AddRow("Created At", run.CreatedAt.ToString("u"))
                .AddRow("Started At", run.StartedAt?.ToString("u") ?? "-")
                .AddRow("Completed At", run.CompletedAt?.ToString("u") ?? "-")
                .AddRow("Max Steps", run.MaxSteps.ToString())
                .AddRow("Max Duration (s)", run.MaxDurationSeconds.ToString());

            if (run.FailureReason is not null)
            {
                table.AddRow("Failure Reason", run.FailureReason);
            }

            AnsiConsole.Write(table);
            return 0;
        }
        catch (ApiException ex)
        {
            AnsiConsole.MarkupLine($"[red]API error {ex.StatusCode}:[/] {ex.Body}");
            return 1;
        }
    }
}



