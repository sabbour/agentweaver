using Scaffolder.Cli.Api;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Scaffolder.Cli.Commands;

/// <summary>
/// T057: `run diff` — print the unified diff for a completed run.
/// GET /runs/{runId}/diff; outputs text/plain diff.
/// Displays 409 message if diff is not available.
/// </summary>
public sealed class DiffCommand : AsyncCommand<DiffCommand.Settings>
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
            var diff = await client.GetDiffAsync(settings.RunId);

            if (string.IsNullOrWhiteSpace(diff))
            {
                AnsiConsole.MarkupLine("[dim]No changes in this run.[/]");
                return 0;
            }

            // Print the raw unified diff to stdout (no Spectre.Console markup
            // since the diff may contain characters that need escaping)
            Console.WriteLine(diff);
            return 0;
        }
        catch (ApiException ex) when (ex.StatusCode == 409)
        {
            AnsiConsole.MarkupLine(
                "[yellow]Diff not available:[/] The run is not in a state with an available diff. " +
                "Wait for the run to complete.");
            return 1;
        }
        catch (ApiException ex) when (ex.StatusCode == 404)
        {
            AnsiConsole.MarkupLine("[red]Run not found.[/]");
            return 1;
        }
        catch (ApiException ex)
        {
            AnsiConsole.MarkupLine($"[red]API error {ex.StatusCode}:[/] {ex.Body}");
            return 1;
        }
    }
}



