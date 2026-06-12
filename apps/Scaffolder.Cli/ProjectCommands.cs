using Spectre.Console;

namespace Scaffolder.Cli;

public static class ProjectCommands
{
    // scaffolder project create --name <name> --dir <path> [--origin blank|github] [--source-repo owner/repo]
    //   [--provider github-copilot|microsoft-foundry] [--model-copilot <id>] [--model-foundry <id>]
    public static async Task<int> CreateAsync(ApiClient api, string[] args, CancellationToken ct)
    {
        var name = GetFlag(args, "--name");
        var dir  = GetFlag(args, "--dir");
        var origin = GetFlag(args, "--origin") ?? "blank";
        var sourceRepo = GetFlag(args, "--source-repo");
        var provider = GetFlag(args, "--provider");
        var modelCopilot = GetFlag(args, "--model-copilot");
        var modelFoundry = GetFlag(args, "--model-foundry");

        if (string.IsNullOrWhiteSpace(name)) { AnsiConsole.MarkupLine("[red]--name is required.[/]"); return 1; }
        if (string.IsNullOrWhiteSpace(dir))  { AnsiConsole.MarkupLine("[red]--dir is required.[/]"); return 1; }
        if (origin == "github" && string.IsNullOrWhiteSpace(sourceRepo))
        { AnsiConsole.MarkupLine("[red]--source-repo is required when --origin is github.[/]"); return 1; }

        var req = new CreateProjectRequest
        {
            Name = name,
            Origin = origin,
            SourceRepository = sourceRepo,
            WorkingDirectory = dir,
            DefaultProvider = provider,
            DefaultModelGitHubCopilot = modelCopilot,
            DefaultModelMicrosoftFoundry = modelFoundry,
        };

        var project = await api.CreateProjectAsync(req, ct);
        AnsiConsole.MarkupLine($"[green]Project created:[/] {Markup.Escape(project.Name)} ({Markup.Escape(project.ProjectId)})");
        AnsiConsole.MarkupLine($"  Working directory: {Markup.Escape(project.WorkingDirectory)}");
        AnsiConsole.MarkupLine($"  Origin: {Markup.Escape(project.Origin)}");
        AnsiConsole.MarkupLine($"  Default branch: {Markup.Escape(project.DefaultBranch)}");
        return 0;
    }

    // scaffolder project list
    public static async Task<int> ListAsync(ApiClient api, CancellationToken ct)
    {
        var projects = await api.ListProjectsAsync(ct);
        if (projects.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No projects found. Create one with: scaffolder project create[/]");
            return 0;
        }
        foreach (var p in projects)
        {
            var avail = p.Available ? "[green]available[/]" : "[yellow]unavailable[/]";
            AnsiConsole.MarkupLine($"  {Markup.Escape(p.ProjectId)}  {Markup.Escape(p.Name)}  {avail}  {Markup.Escape(p.WorkingDirectory)}");
        }
        return 0;
    }

    // scaffolder project show <project-id>
    public static async Task<int> ShowAsync(ApiClient api, string projectId, CancellationToken ct)
    {
        var p = await api.GetProjectAsync(projectId, ct);
        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(p.Name)}[/] ({Markup.Escape(p.ProjectId)})");
        AnsiConsole.MarkupLine($"  Origin:            {Markup.Escape(p.Origin)}");
        if (!string.IsNullOrWhiteSpace(p.SourceRepository))
            AnsiConsole.MarkupLine($"  Source repository: {Markup.Escape(p.SourceRepository)}");
        AnsiConsole.MarkupLine($"  Working directory: {Markup.Escape(p.WorkingDirectory)}");
        AnsiConsole.MarkupLine($"  Default branch:    {Markup.Escape(p.DefaultBranch)}");
        AnsiConsole.MarkupLine($"  Owner:             {Markup.Escape(p.Owner)}");
        AnsiConsole.MarkupLine($"  Default provider:  {Markup.Escape(p.DefaultProvider)}");
        if (!string.IsNullOrWhiteSpace(p.DefaultModelGitHubCopilot))
            AnsiConsole.MarkupLine($"  Copilot model:     {Markup.Escape(p.DefaultModelGitHubCopilot)}");
        if (!string.IsNullOrWhiteSpace(p.DefaultModelMicrosoftFoundry))
            AnsiConsole.MarkupLine($"  Foundry model:     {Markup.Escape(p.DefaultModelMicrosoftFoundry)}");
        AnsiConsole.MarkupLine($"  Available:         {(p.Available ? "yes" : "no")}");
        AnsiConsole.MarkupLine($"  State:             {Markup.Escape(p.State)}");
        AnsiConsole.MarkupLine($"  Created:           {p.CreatedAt:u}");
        return 0;
    }

    // scaffolder project configure <project-id> [--provider ...] [--model-copilot ...] [--model-foundry ...]
    public static async Task<int> ConfigureAsync(ApiClient api, string projectId, string[] args, CancellationToken ct)
    {
        var provider = GetFlag(args, "--provider");
        var modelCopilot = GetFlag(args, "--model-copilot");
        var modelFoundry = GetFlag(args, "--model-foundry");
        var req = new UpdateProjectProviderSettingsRequest
        {
            DefaultProvider = provider,
            DefaultModelGitHubCopilot = modelCopilot,
            DefaultModelMicrosoftFoundry = modelFoundry,
        };
        await api.UpdateProjectProviderSettingsAsync(projectId, req, ct);
        AnsiConsole.MarkupLine("[green]Provider settings updated.[/]");
        return 0;
    }

    // scaffolder project rename <project-id> --name <new-name>
    public static async Task<int> RenameAsync(ApiClient api, string projectId, string[] args, CancellationToken ct)
    {
        var name = GetFlag(args, "--name");
        if (string.IsNullOrWhiteSpace(name)) { AnsiConsole.MarkupLine("[red]--name is required.[/]"); return 1; }
        await api.RenameProjectAsync(projectId, name!, ct);
        AnsiConsole.MarkupLine("[green]Project renamed.[/]");
        return 0;
    }

    // scaffolder project relink <project-id> --dir <new-path>
    public static async Task<int> RelinkAsync(ApiClient api, string projectId, string[] args, CancellationToken ct)
    {
        var dir = GetFlag(args, "--dir");
        if (string.IsNullOrWhiteSpace(dir)) { AnsiConsole.MarkupLine("[red]--dir is required.[/]"); return 1; }
        await api.RelinkProjectAsync(projectId, dir!, ct);
        AnsiConsole.MarkupLine("[green]Project relinked.[/]");
        return 0;
    }

    // scaffolder project delete <project-id> --confirm
    public static async Task<int> DeleteAsync(ApiClient api, string projectId, string[] args, CancellationToken ct)
    {
        var confirm = Array.Exists(args, a => string.Equals(a, "--confirm", StringComparison.OrdinalIgnoreCase));
        if (!confirm)
        {
            AnsiConsole.MarkupLine("[red]--confirm flag is required to delete a project.[/]");
            AnsiConsole.MarkupLine("This is a destructive operation. Add --confirm to proceed.");
            return 1;
        }
        await api.DeleteProjectAsync(projectId, ct);
        AnsiConsole.MarkupLine("[green]Project deleted.[/]");
        return 0;
    }

    // scaffolder project run <project-id> --task <text> [--provider ...] [--model <id>] [--base-branch <b>]
    public static async Task<int> RunAsync(ApiClient api, string projectId, string[] args, CancellationToken ct)
    {
        var task = GetFlag(args, "--task");
        var provider = GetFlag(args, "--provider");
        var model = GetFlag(args, "--model");
        var baseBranch = GetFlag(args, "--base-branch");
        if (string.IsNullOrWhiteSpace(task)) { AnsiConsole.MarkupLine("[red]--task is required.[/]"); return 1; }
        var req = new CreateProjectRunRequest
        {
            Task = task,
            ModelSource = provider,
            ModelId = model,
            BaseBranch = baseBranch,
        };
        var response = await api.StartProjectRunAsync(projectId, req, ct);
        AnsiConsole.MarkupLine($"[green]Run started:[/] {Markup.Escape(response.RunId)}");
        AnsiConsole.MarkupLine($"Watch: scaffolder run watch {Markup.Escape(response.RunId)}");
        return 0;
    }

    // scaffolder project runs <project-id>
    public static async Task<int> RunsAsync(ApiClient api, string projectId, CancellationToken ct)
    {
        var runs = await api.ListProjectRunsAsync(projectId, ct);
        if (runs.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No runs for this project.[/]");
            return 0;
        }
        foreach (var r in runs)
        {
            AnsiConsole.MarkupLine($"  {Markup.Escape(r.RunId)}  {Markup.Escape(r.Status)}  {r.StartedAt:u}  {Markup.Escape(r.Task ?? string.Empty)}");
        }
        return 0;
    }

    private static string? GetFlag(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }
}
