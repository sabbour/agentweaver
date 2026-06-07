using Scaffolder.Cli.Api;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Scaffolder.Cli.Commands;

/// <summary>
/// T054: `run submit` — submit a new agent run.
/// Prompts for originating branch, task prompt, model source, and optional bounds.
/// POST /runs; displays returned runId and initial status.
/// </summary>
public sealed class SubmitRunCommand : AsyncCommand<SubmitRunCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--branch <BRANCH>")]
        public string? OriginatingBranch { get; init; }

        [CommandOption("--prompt <PROMPT>")]
        public string? TaskPrompt { get; init; }

        [CommandOption("--model <MODEL>")]
        public string? ModelSource { get; init; }

        [CommandOption("--max-steps <N>")]
        public int? MaxSteps { get; init; }

        [CommandOption("--max-duration <SECONDS>")]
        public int? MaxDurationSeconds { get; init; }

        [CommandOption("--api <URL>")]
        public string ApiBaseUrl { get; init; } = "http://localhost:3000";

        [CommandOption("--submitted-by <NAME>")]
        public string? SubmittedBy { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var branch = settings.OriginatingBranch
            ?? AnsiConsole.Ask<string>("Originating branch:");

        var prompt = settings.TaskPrompt
            ?? AnsiConsole.Ask<string>("Task prompt:");

        var model = settings.ModelSource
            ?? AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Model source:")
                    .AddChoices("CopilotSdk", "MicrosoftFoundry"));

        using var client = new ScaffolderApiClient(settings.ApiBaseUrl);

        var request = new CreateRunRequest
        {
            OriginatingBranch = branch,
            ModelSource = model,
            TaskPrompt = prompt,
            MaxSteps = settings.MaxSteps,
            MaxDurationSeconds = settings.MaxDurationSeconds
        };

        try
        {
            var run = await client.CreateRunAsync(request, settings.SubmittedBy);
            if (run is null)
            {
                AnsiConsole.MarkupLine("[red]Error: empty response from server.[/]");
                return 1;
            }

            AnsiConsole.MarkupLine("[green]Run submitted successfully.[/]");

            var table = new Table()
                .AddColumn("Field")
                .AddColumn("Value")
                .AddRow("Run ID", run.Id.ToString())
                .AddRow("Status", run.Status)
                .AddRow("Branch", run.OriginatingBranch)
                .AddRow("Model", run.ModelSource)
                .AddRow("Submitted By", run.SubmittedBy)
                .AddRow("Max Steps", run.MaxSteps.ToString())
                .AddRow("Max Duration (s)", run.MaxDurationSeconds.ToString());

            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine($"[dim]Watch progress: scaffolder run watch {run.Id}[/]");
            return 0;
        }
        catch (ApiException ex)
        {
            AnsiConsole.MarkupLine($"[red]API error {ex.StatusCode}:[/] {ex.Body}");
            return 1;
        }
    }
}



