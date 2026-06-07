using Scaffolder.Cli;
using Spectre.Console;

return await CliEntryPoint.RunAsync(args);

internal static class CliEntryPoint
{
    public static async Task<int> RunAsync(string[] args)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };

        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        if (!string.Equals(args[0], "run", StringComparison.Ordinal))
        {
            AnsiConsole.MarkupLine($"[red]Unknown command:[/] {Markup.Escape(args[0])}");
            PrintUsage();
            return 1;
        }

        if (args.Length < 2)
        {
            AnsiConsole.MarkupLine("[red]Missing subcommand for 'run'.[/]");
            PrintUsage();
            return 1;
        }

        CliConfig config;
        try
        {
            config = CliConfig.Load();
        }
        catch (ConfigException ex)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        var api = new ApiClient(config);
        var subcommand = args[1];

        try
        {
            switch (subcommand)
            {
                case "submit":
                    return await RunCommands.SubmitAsync(api, cts.Token);

                case "watch":
                    if (!TryGetRunId(args, out var watchId))
                    {
                        return MissingRunId("watch");
                    }
                    return await RunCommands.WatchAsync(api, watchId, interactive: true, cts.Token);

                case "review":
                    if (!TryGetRunId(args, out var reviewId))
                    {
                        return MissingRunId("review");
                    }
                    return await RunCommands.ReviewAsync(api, reviewId, cts.Token);

                case "show":
                    if (!TryGetRunId(args, out var showId))
                    {
                        return MissingRunId("show");
                    }
                    return await RunCommands.ShowAsync(api, showId, cts.Token);

                default:
                    AnsiConsole.MarkupLine($"[red]Unknown subcommand:[/] {Markup.Escape(subcommand)}");
                    PrintUsage();
                    return 1;
            }
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (HttpRequestException ex)
        {
            AnsiConsole.MarkupLine(
                $"[red]Network error reaching {Markup.Escape(api.ApiUrl)}:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Unexpected error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }

    private static bool TryGetRunId(string[] args, out string runId)
    {
        if (args.Length >= 3 && !string.IsNullOrWhiteSpace(args[2]))
        {
            runId = args[2];
            return true;
        }

        runId = string.Empty;
        return false;
    }

    private static int MissingRunId(string subcommand)
    {
        AnsiConsole.MarkupLine($"[red]Missing run id for 'run {subcommand}'.[/]");
        AnsiConsole.MarkupLine($"Usage: scaffolder run {subcommand} <run-id>");
        return 1;
    }

    private static bool IsHelp(string arg) =>
        arg is "-h" or "--help" or "help";

    private static void PrintUsage()
    {
        AnsiConsole.WriteLine("Scaffolder CLI");
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine("Usage:");
        AnsiConsole.WriteLine("  scaffolder run submit            Submit a new run and watch it");
        AnsiConsole.WriteLine("  scaffolder run watch <run-id>    Stream events for a run");
        AnsiConsole.WriteLine("  scaffolder run review <run-id>   Review a run's diff and approve or decline");
        AnsiConsole.WriteLine("  scaffolder run show <run-id>     Show run details");
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine("Environment:");
        AnsiConsole.WriteLine("  SCAFFOLDER_API_URL   API base URL (default: http://localhost:5000)");
        AnsiConsole.WriteLine("  SCAFFOLDER_API_KEY   API bearer key (required)");
    }
}
