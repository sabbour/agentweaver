using Scaffolder.Cli;
using Scaffolder.Cli.Commands;
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

        var topCommand = args[0];

        if (!string.Equals(topCommand, "run", StringComparison.Ordinal) &&
            !string.Equals(topCommand, "sandbox-policy", StringComparison.Ordinal) &&
            !string.Equals(topCommand, "project", StringComparison.Ordinal) &&
            !string.Equals(topCommand, "github", StringComparison.Ordinal) &&
            !string.Equals(topCommand, "team", StringComparison.Ordinal))
        {
            AnsiConsole.MarkupLine($"[red]Unknown command:[/] {Markup.Escape(args[0])}");
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
        var subcommand = args.Length >= 2 ? args[1] : string.Empty;

        try
        {
            if (string.Equals(topCommand, "project", StringComparison.Ordinal))
            {
                return await HandleProjectAsync(api, subcommand, args, cts.Token);
            }

            if (string.Equals(topCommand, "team", StringComparison.Ordinal))
            {
                return await HandleTeamAsync(api, subcommand, args, cts.Token);
            }

            if (string.Equals(topCommand, "github", StringComparison.Ordinal))
            {
                return await HandleGitHubAsync(api, subcommand, args, cts.Token);
            }

            if (string.Equals(topCommand, "sandbox-policy", StringComparison.Ordinal))
            {
                return await HandleSandboxPolicyAsync(api, subcommand, args, cts.Token);
            }

            // run subcommands
            if (string.IsNullOrEmpty(subcommand))
            {
                AnsiConsole.MarkupLine("[red]Missing subcommand for 'run'.[/]");
                PrintUsage();
                return 1;
            }

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

                case "request-changes":
                    if (!TryGetRunId(args, out var requestChangesId))
                    {
                        return MissingRunId("request-changes");
                    }
                    var comment = GetFlag(args, "--comment") ?? GetFlag(args, "-m");
                    return await RunCommands.RequestChangesAsync(api, requestChangesId, comment, cts.Token);

                case "show":
                    if (!TryGetRunId(args, out var showId))
                    {
                        return MissingRunId("show");
                    }
                    return await RunCommands.ShowAsync(api, showId, cts.Token);

                case "artifacts":
                    if (!TryGetRunId(args, out var artifactsId))
                    {
                        return MissingRunId("artifacts");
                    }
                    return await ArtifactCommands.ArtifactsAsync(api, artifactsId, cts.Token);

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

    private static async Task<int> HandleSandboxPolicyAsync(
        ApiClient api, string subcommand, string[] args, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(subcommand) || IsHelp(subcommand))
        {
            AnsiConsole.MarkupLine("[red]Missing subcommand for 'sandbox-policy'.[/]");
            AnsiConsole.MarkupLine("Usage:");
            AnsiConsole.MarkupLine("  scaffolder sandbox-policy get --repository-path <path>");
            AnsiConsole.MarkupLine("  scaffolder sandbox-policy set --repository-path <path> --shell-enabled true|false");
            return 1;
        }

        var repoPath = GetFlag(args, "--repository-path");
        if (string.IsNullOrEmpty(repoPath))
        {
            AnsiConsole.MarkupLine("[red]Missing required flag --repository-path.[/]");
            return 1;
        }

        switch (subcommand)
        {
            case "get":
                return await SandboxPolicyCommands.GetAsync(api, Path.GetFullPath(repoPath), ct);

            case "set":
            {
                var shellEnabledStr = GetFlag(args, "--shell-enabled");
                if (string.IsNullOrEmpty(shellEnabledStr))
                {
                    AnsiConsole.MarkupLine("[red]Missing required flag --shell-enabled (true|false).[/]");
                    return 1;
                }

                if (!bool.TryParse(shellEnabledStr, out var shellEnabled))
                {
                    AnsiConsole.MarkupLine($"[red]Invalid value for --shell-enabled:[/] {Markup.Escape(shellEnabledStr)} (expected true or false)");
                    return 1;
                }

                return await SandboxPolicyCommands.SetAsync(api, Path.GetFullPath(repoPath), shellEnabled, ct);
            }

            default:
                AnsiConsole.MarkupLine($"[red]Unknown subcommand for 'sandbox-policy':[/] {Markup.Escape(subcommand)}");
                return 1;
        }
    }

    private static string? GetFlag(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
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
        AnsiConsole.WriteLine("  scaffolder run submit                Submit a new run and watch it");
        AnsiConsole.WriteLine("  scaffolder run watch <run-id>        Stream events for a run");
        AnsiConsole.WriteLine("  scaffolder run review <run-id>       Review a run's diff and approve or decline");
        AnsiConsole.WriteLine("  scaffolder run request-changes <run-id> [--comment|-m <text>]  Request changes from the agent");
        AnsiConsole.WriteLine("  scaffolder run show <run-id>          Show run details");
        AnsiConsole.WriteLine("  scaffolder run artifacts <run-id>    Browse run artifact files and diffs");
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine("  scaffolder sandbox-policy get --repository-path <path>");
        AnsiConsole.WriteLine("  scaffolder sandbox-policy set --repository-path <path> --shell-enabled true|false");
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine("  scaffolder project create --name <name> --dir <path> [--origin blank|github] ...");
        AnsiConsole.WriteLine("  scaffolder project list");
        AnsiConsole.WriteLine("  scaffolder project show <project-id>");
        AnsiConsole.WriteLine("  scaffolder project run <project-id> --task <text>");
        AnsiConsole.WriteLine("  (Run 'scaffolder project help' for full project usage)");
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine("  scaffolder github sign-in");
        AnsiConsole.WriteLine("  scaffolder github sign-out");
        AnsiConsole.WriteLine("  scaffolder github status");
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine("  scaffolder team scenarios");
        AnsiConsole.WriteLine("  scaffolder team cast --project-id <id> --scenario <template-id>");
        AnsiConsole.WriteLine("  scaffolder team show --project-id <id>");
        AnsiConsole.WriteLine("  scaffolder team proposal show <proposalId> --project-id <id>");
        AnsiConsole.WriteLine("  scaffolder team proposal confirm <proposalId> --project-id <id> [--intent new|augment|recast]");
        AnsiConsole.WriteLine("  scaffolder team proposal reject <proposalId> --project-id <id>");
        AnsiConsole.WriteLine("  scaffolder team charter show <name> --project-id <id>");
        AnsiConsole.WriteLine("  scaffolder team charter edit <name> --project-id <id>");
        AnsiConsole.WriteLine("  scaffolder team member add --project-id <id> --role-id <id>");
        AnsiConsole.WriteLine("  scaffolder team member remove <name> --project-id <id>");
        AnsiConsole.WriteLine("  scaffolder team member rerole <name> --project-id <id> --role-id <id>");
        AnsiConsole.WriteLine("  scaffolder team sync status --project-id <id>");
        AnsiConsole.WriteLine("  scaffolder team sync commit --project-id <id> [--message <msg>]");
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine("Environment:");
        AnsiConsole.WriteLine("  SCAFFOLDER_API_URL   API base URL (default: http://localhost:5000)");
        AnsiConsole.WriteLine("  SCAFFOLDER_API_KEY   API bearer key (required)");
    }

    private static async Task<int> HandleTeamAsync(
        ApiClient api, string subcommand, string[] args, CancellationToken ct)
    {
        var projectId = GetFlag(args, "--project-id") ?? string.Empty;

        switch (subcommand)
        {
            case "scenarios":
                return await TeamCommands.ScenariosAsync(api, ct);

            case "cast":
            {
                if (string.IsNullOrWhiteSpace(projectId)) { AnsiConsole.MarkupLine("[red]--project-id is required.[/]"); return 1; }
                var scenarioId = GetFlag(args, "--scenario");
                if (string.IsNullOrWhiteSpace(scenarioId))
                {
                    AnsiConsole.MarkupLine("[red]--scenario is required for scenario casting. Use --goal or --analyze for other modes.[/]");
                    return 1;
                }
                var universe = GetFlag(args, "--universe");
                return await TeamCommands.CastScenarioAsync(api, projectId, scenarioId, universe, ct);
            }

            case "show":
                if (string.IsNullOrWhiteSpace(projectId)) { AnsiConsole.MarkupLine("[red]--project-id is required.[/]"); return 1; }
                return await TeamCommands.ShowTeamAsync(api, projectId, ct);

            case "proposal":
            {
                var prop = args.Length >= 3 ? args[2] : string.Empty;
                var proposalId = args.Length >= 4 && !args[3].StartsWith('-') ? args[3] : GetFlag(args, "--proposal-id") ?? string.Empty;

                // Derive proposalId: for "proposal show <id>", "proposal confirm <id>", "proposal reject <id>"
                // args[2] = "proposal", args[3] = subcommand, args[4] = proposalId
                var propSubcommand = args.Length >= 4 ? args[3] : string.Empty;
                var propArg = args.Length >= 5 && !args[4].StartsWith('-') ? args[4] : GetFlag(args, "--proposal-id") ?? string.Empty;

                switch (propSubcommand)
                {
                    case "show":
                        if (string.IsNullOrWhiteSpace(projectId)) { AnsiConsole.MarkupLine("[red]--project-id is required.[/]"); return 1; }
                        if (string.IsNullOrWhiteSpace(propArg)) { AnsiConsole.MarkupLine("[red]proposal-id is required.[/]"); return 1; }
                        return await TeamCommands.ProposalShowAsync(api, projectId, propArg, ct);

                    case "confirm":
                        if (string.IsNullOrWhiteSpace(projectId)) { AnsiConsole.MarkupLine("[red]--project-id is required.[/]"); return 1; }
                        if (string.IsNullOrWhiteSpace(propArg)) { AnsiConsole.MarkupLine("[red]proposal-id is required.[/]"); return 1; }
                        var intent = GetFlag(args, "--intent");
                        return await TeamCommands.ProposalConfirmAsync(api, projectId, propArg, intent, ct);

                    case "reject":
                        if (string.IsNullOrWhiteSpace(projectId)) { AnsiConsole.MarkupLine("[red]--project-id is required.[/]"); return 1; }
                        if (string.IsNullOrWhiteSpace(propArg)) { AnsiConsole.MarkupLine("[red]proposal-id is required.[/]"); return 1; }
                        return await TeamCommands.ProposalRejectAsync(api, projectId, propArg, ct);

                    default:
                        AnsiConsole.MarkupLine($"[red]Unknown proposal subcommand:[/] {Markup.Escape(propSubcommand)}");
                        AnsiConsole.MarkupLine("Usage: scaffolder team proposal show|confirm|reject <proposalId> --project-id <id>");
                        return 1;
                }
            }

            case "charter":
            {
                var charterSubcommand = args.Length >= 4 ? args[3] : string.Empty;
                var memberName = args.Length >= 5 && !args[4].StartsWith('-') ? args[4] : GetFlag(args, "--member") ?? string.Empty;

                switch (charterSubcommand)
                {
                    case "show":
                        if (string.IsNullOrWhiteSpace(projectId)) { AnsiConsole.MarkupLine("[red]--project-id is required.[/]"); return 1; }
                        if (string.IsNullOrWhiteSpace(memberName)) { AnsiConsole.MarkupLine("[red]member name is required.[/]"); return 1; }
                        return await TeamCommands.CharterShowAsync(api, projectId, memberName, ct);

                    case "edit":
                        if (string.IsNullOrWhiteSpace(projectId)) { AnsiConsole.MarkupLine("[red]--project-id is required.[/]"); return 1; }
                        if (string.IsNullOrWhiteSpace(memberName)) { AnsiConsole.MarkupLine("[red]member name is required.[/]"); return 1; }
                        return await TeamCommands.CharterEditAsync(api, projectId, memberName, ct);

                    default:
                        AnsiConsole.MarkupLine($"[red]Unknown charter subcommand:[/] {Markup.Escape(charterSubcommand)}");
                        AnsiConsole.MarkupLine("Usage: scaffolder team charter show|edit <name> --project-id <id>");
                        return 1;
                }
            }

            case "member":
            {
                var memberSubcommand = args.Length >= 4 ? args[3] : string.Empty;
                var memberArg = args.Length >= 5 && !args[4].StartsWith('-') ? args[4] : string.Empty;

                switch (memberSubcommand)
                {
                    case "add":
                        if (string.IsNullOrWhiteSpace(projectId)) { AnsiConsole.MarkupLine("[red]--project-id is required.[/]"); return 1; }
                        return await TeamCommands.MemberAddAsync(api, args, projectId, ct);

                    case "remove":
                        if (string.IsNullOrWhiteSpace(projectId)) { AnsiConsole.MarkupLine("[red]--project-id is required.[/]"); return 1; }
                        if (string.IsNullOrWhiteSpace(memberArg)) { AnsiConsole.MarkupLine("[red]member name is required.[/]"); return 1; }
                        return await TeamCommands.MemberRemoveAsync(api, projectId, memberArg, ct);

                    case "rerole":
                        if (string.IsNullOrWhiteSpace(projectId)) { AnsiConsole.MarkupLine("[red]--project-id is required.[/]"); return 1; }
                        if (string.IsNullOrWhiteSpace(memberArg)) { AnsiConsole.MarkupLine("[red]member name is required.[/]"); return 1; }
                        return await TeamCommands.MemberReroleAsync(api, args, projectId, memberArg, ct);

                    default:
                        AnsiConsole.MarkupLine($"[red]Unknown member subcommand:[/] {Markup.Escape(memberSubcommand)}");
                        AnsiConsole.MarkupLine("Usage: scaffolder team member add|remove|rerole ...");
                        return 1;
                }
            }

            case "sync":
            {
                if (string.IsNullOrWhiteSpace(projectId)) { AnsiConsole.MarkupLine("[red]--project-id is required.[/]"); return 1; }
                var syncSubcommand = args.Length >= 4 ? args[3] : string.Empty;

                switch (syncSubcommand)
                {
                    case "status":
                        return await TeamCommands.SyncStatusAsync(api, projectId, ct);

                    case "commit":
                        var messageArg = GetFlag(args, "--message");
                        return await TeamCommands.SyncCommitAsync(api, projectId, messageArg, ct);

                    default:
                        AnsiConsole.MarkupLine($"[red]Unknown sync subcommand:[/] {Markup.Escape(syncSubcommand)}");
                        AnsiConsole.MarkupLine("Usage: scaffolder team sync status|commit --project-id <id>");
                        return 1;
                }
            }

            default:
                AnsiConsole.MarkupLine($"[red]Unknown subcommand for 'team':[/] {Markup.Escape(subcommand)}");
                AnsiConsole.MarkupLine("Run 'scaffolder team --help' for usage.");
                return 1;
        }
    }

    private static async Task<int> HandleProjectAsync(
        ApiClient api, string subcommand, string[] args, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(subcommand) || IsHelp(subcommand))
        {
            PrintProjectUsage();
            return subcommand == string.Empty ? 1 : 0;
        }
        var projectId = args.Length >= 3 ? args[2] : string.Empty;

        switch (subcommand)
        {
            case "create":   return await ProjectCommands.CreateAsync(api, args, ct);
            case "list":     return await ProjectCommands.ListAsync(api, ct);
            case "show":
                if (string.IsNullOrWhiteSpace(projectId)) { AnsiConsole.MarkupLine("[red]Missing project-id.[/]"); return 1; }
                return await ProjectCommands.ShowAsync(api, projectId, ct);
            case "configure":
                if (string.IsNullOrWhiteSpace(projectId)) { AnsiConsole.MarkupLine("[red]Missing project-id.[/]"); return 1; }
                return await ProjectCommands.ConfigureAsync(api, projectId, args, ct);
            case "rename":
                if (string.IsNullOrWhiteSpace(projectId)) { AnsiConsole.MarkupLine("[red]Missing project-id.[/]"); return 1; }
                return await ProjectCommands.RenameAsync(api, projectId, args, ct);
            case "relink":
                if (string.IsNullOrWhiteSpace(projectId)) { AnsiConsole.MarkupLine("[red]Missing project-id.[/]"); return 1; }
                return await ProjectCommands.RelinkAsync(api, projectId, args, ct);
            case "delete":
                if (string.IsNullOrWhiteSpace(projectId)) { AnsiConsole.MarkupLine("[red]Missing project-id.[/]"); return 1; }
                return await ProjectCommands.DeleteAsync(api, projectId, args, ct);
            case "run":
                if (string.IsNullOrWhiteSpace(projectId)) { AnsiConsole.MarkupLine("[red]Missing project-id.[/]"); return 1; }
                return await ProjectCommands.RunAsync(api, projectId, args, ct);
            case "runs":
                if (string.IsNullOrWhiteSpace(projectId)) { AnsiConsole.MarkupLine("[red]Missing project-id.[/]"); return 1; }
                return await ProjectCommands.RunsAsync(api, projectId, ct);
            default:
                AnsiConsole.MarkupLine($"[red]Unknown subcommand for 'project':[/] {Markup.Escape(subcommand)}");
                PrintProjectUsage();
                return 1;
        }
    }

    private static async Task<int> HandleGitHubAsync(
        ApiClient api, string subcommand, string[] args, CancellationToken ct)
    {
        switch (subcommand)
        {
            case "sign-in":  return await GitHubAuthCommands.SignInAsync(api, ct);
            case "sign-out": return await GitHubAuthCommands.SignOutAsync(api, ct);
            case "status":   return await GitHubAuthCommands.StatusAsync(api, ct);
            default:
                AnsiConsole.MarkupLine($"[red]Unknown subcommand for 'github':[/] {Markup.Escape(subcommand)}");
                AnsiConsole.MarkupLine("Usage:");
                AnsiConsole.MarkupLine("  scaffolder github sign-in");
                AnsiConsole.MarkupLine("  scaffolder github sign-out");
                AnsiConsole.MarkupLine("  scaffolder github status");
                return 1;
        }
    }

    private static void PrintProjectUsage()
    {
        AnsiConsole.WriteLine("Usage:");
        AnsiConsole.WriteLine("  scaffolder project create --name <name> --dir <path> [--origin blank|github] [--source-repo owner/repo]");
        AnsiConsole.WriteLine("  scaffolder project list");
        AnsiConsole.WriteLine("  scaffolder project show <project-id>");
        AnsiConsole.WriteLine("  scaffolder project configure <project-id> [--provider github-copilot|microsoft-foundry] [--model-copilot <id>] [--model-foundry <id>]");
        AnsiConsole.WriteLine("  scaffolder project rename <project-id> --name <new-name>");
        AnsiConsole.WriteLine("  scaffolder project relink <project-id> --dir <new-path>");
        AnsiConsole.WriteLine("  scaffolder project delete <project-id> --confirm");
        AnsiConsole.WriteLine("  scaffolder project run <project-id> --task <text> [--provider ...] [--model <id>] [--base-branch <b>]");
        AnsiConsole.WriteLine("  scaffolder project runs <project-id>");
    }
}
