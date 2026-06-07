using Scaffolder.Cli.Commands;
using Spectre.Console.Cli;

/// <summary>
/// T052: Scaffolder CLI entry point.
/// Command routing via Spectre.Console.Cli.
/// All commands are thin wrappers over the Scaffolder API (Principle III, IV).
/// </summary>
var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("scaffolder");
    config.SetApplicationVersion("0.1.0");

    config.AddBranch("run", run =>
    {
        run.SetDescription("Manage agent runs");

        // T054: submit a new run
        run.AddCommand<SubmitRunCommand>("submit")
            .WithDescription("Submit a new agent run");

        // T055: check run status
        run.AddCommand<RunStatusCommand>("status")
            .WithDescription("Show the status of a run");

        // T056: stream run events via SSE
        run.AddCommand<WatchRunCommand>("watch")
            .WithDescription("Watch live run events (SSE stream, reconnect-safe)");

        // T057: print the unified diff
        run.AddCommand<DiffCommand>("diff")
            .WithDescription("Print the unified diff for a completed run");

        // T058: submit a review decision
        run.AddCommand<ReviewCommand>("review")
            .WithDescription("Approve or decline a run for merge");
    });
});

return app.Run(args);

