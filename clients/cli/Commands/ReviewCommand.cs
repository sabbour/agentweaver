using Scaffolder.Cli.Api;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Scaffolder.Cli.Commands;

/// <summary>
/// T058: `run review` — approve or decline a completed run.
/// Prompts for decision and optional comment.
/// POST /runs/{runId}/review; displays resulting status and merge outcome.
/// </summary>
public sealed class ReviewCommand : AsyncCommand<ReviewCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<RUN_ID>")]
        public Guid RunId { get; init; }

        [CommandOption("--decision <DECISION>")]
        public string? Decision { get; init; }

        [CommandOption("--reviewer <NAME>")]
        public string? Reviewer { get; init; }

        [CommandOption("--comment <COMMENT>")]
        public string? Comment { get; init; }

        [CommandOption("--api <URL>")]
        public string ApiBaseUrl { get; init; } = "http://localhost:3000";
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var decision = settings.Decision
            ?? AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title($"Decision for run {settings.RunId}:")
                    .AddChoices("approve", "decline"));

        var reviewer = settings.Reviewer
            ?? AnsiConsole.Ask<string>("Reviewer name:");

        var comment = settings.Comment;
        if (comment is null && AnsiConsole.Confirm("Add a comment?", defaultValue: false))
        {
            comment = AnsiConsole.Ask<string>("Comment:");
        }

        using var client = new ScaffolderApiClient(settings.ApiBaseUrl);

        try
        {
            var run = await client.ReviewRunAsync(
                settings.RunId,
                new ReviewDecisionRequest
                {
                    Decision = decision,
                    Reviewer = reviewer,
                    Comment = comment
                });

            if (run is null)
            {
                AnsiConsole.MarkupLine("[red]Error: empty response from server.[/]");
                return 1;
            }

            var statusColor = run.Status switch
            {
                "Merged" => "green",
                "Declined" => "yellow",
                "MergeConflict" => "red",
                _ => "white"
            };

            AnsiConsole.MarkupLine(
                $"Review submitted. Run status: [{statusColor}]{run.Status}[/]");

            if (run.FailureReason is not null)
            {
                AnsiConsole.MarkupLine($"[dim]Reason: {run.FailureReason}[/]");
            }

            return 0;
        }
        catch (ApiException ex) when (ex.StatusCode == 409)
        {
            AnsiConsole.MarkupLine(
                "[red]Error:[/] Run is not in the AwaitingReview state. " +
                "Check run status first.");
            return 1;
        }
        catch (ApiException ex)
        {
            AnsiConsole.MarkupLine($"[red]API error {ex.StatusCode}:[/] {ex.Body}");
            return 1;
        }
    }
}



