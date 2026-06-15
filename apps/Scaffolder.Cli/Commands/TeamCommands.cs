using Spectre.Console;

namespace Scaffolder.Cli.Commands;

public static class TeamCommands
{
    // scaffolder team templates
    public static async Task<int> ScenariosAsync(ApiClient api, CancellationToken ct)
    {
        var templates = await api.ListScenariosAsync(ct);
        if (templates.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No team templates found in the catalog.[/]");
            return 0;
        }

        var table = new Table().Border(TableBorder.Simple);
        table.AddColumn("ID");
        table.AddColumn("Title");
        table.AddColumn("Description");
        table.AddColumn("Roles");

        foreach (var g in templates)
        {
            table.AddRow(
                Markup.Escape(g.Id),
                Markup.Escape(g.Title),
                Markup.Escape(g.Description),
                Markup.Escape(string.Join(", ", g.Roles.Select(r => r.Title))));
        }

        AnsiConsole.Write(table);
        return 0;
    }

    // scaffolder team cast --project-id <id> --scenario <template-id> [--universe <name>]
    public static async Task<int> CastScenarioAsync(
        ApiClient api, string projectId, string scenarioId, string? universe, CancellationToken ct)
    {
        var req = new CreateProposalRequest
        {
            Mode = "scenario",
            TemplateId = scenarioId,
            Universe = universe,
        };

        var proposal = await api.CreateProposalAsync(projectId, req, ct);
        AnsiConsole.MarkupLine($"[green]Proposal created:[/] {Markup.Escape(proposal.ProposalId)}");
        AnsiConsole.MarkupLine($"  Universe: {Markup.Escape(proposal.Universe)}");
        AnsiConsole.MarkupLine($"  Members:  {proposal.Members.Count}");

        if (proposal.ExistingTeamPresent)
            AnsiConsole.MarkupLine("[yellow]  An existing team was detected. Provide --intent when confirming (new, augment, or recast).[/]");

        var table = new Table().Border(TableBorder.Simple);
        table.AddColumn("Name");
        table.AddColumn("Role");
        table.AddColumn("Named");

        foreach (var m in proposal.Members)
        {
            table.AddRow(
                Markup.Escape(m.ProposedName),
                Markup.Escape(m.Role.Title),
                m.IsNamed ? "yes" : "no");
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"\nConfirm with:  scaffolder team proposal confirm {Markup.Escape(proposal.ProposalId)} --project-id {Markup.Escape(projectId)}");
        AnsiConsole.MarkupLine($"Reject with:   scaffolder team proposal reject {Markup.Escape(proposal.ProposalId)} --project-id {Markup.Escape(projectId)}");
        return 0;
    }

    // scaffolder team show --project-id <id>
    public static async Task<int> ShowTeamAsync(ApiClient api, string projectId, CancellationToken ct)
    {
        var team = await api.GetTeamAsync(projectId, ct);

        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(team.ProjectName)}[/]  Universe: {Markup.Escape(team.Universe)}  Layout: {Markup.Escape(team.Layout)}");

        var table = new Table().Border(TableBorder.Simple);
        table.AddColumn("Name");
        table.AddColumn("Role");
        table.AddColumn("Status");
        table.AddColumn("Model");

        foreach (var m in team.Members)
        {
            var statusMarkup = m.Status == "active" ? "[green]active[/]" : "[grey]retired[/]";
            table.AddRow(
                Markup.Escape(m.Name),
                Markup.Escape(m.RoleTitle),
                statusMarkup,
                Markup.Escape(m.DefaultModel));
        }

        AnsiConsole.Write(table);
        return 0;
    }

    // scaffolder team proposal show <proposalId> --project-id <id>
    public static async Task<int> ProposalShowAsync(
        ApiClient api, string projectId, string proposalId, CancellationToken ct)
    {
        var proposal = await api.GetProposalAsync(projectId, proposalId, ct);
        AnsiConsole.MarkupLine($"[bold]Proposal {Markup.Escape(proposal.ProposalId)}[/]");
        AnsiConsole.MarkupLine($"  Mode:     {Markup.Escape(proposal.Mode)}");
        AnsiConsole.MarkupLine($"  Universe: {Markup.Escape(proposal.Universe)}");
        AnsiConsole.MarkupLine($"  Existing team: {(proposal.ExistingTeamPresent ? "yes" : "no")}");

        if (proposal.Warnings.Count > 0)
        {
            foreach (var w in proposal.Warnings)
                AnsiConsole.MarkupLine($"  [yellow]Warning:[/] {Markup.Escape(w)}");
        }

        var table = new Table().Border(TableBorder.Simple);
        table.AddColumn("Name");
        table.AddColumn("Role");
        table.AddColumn("Named");

        foreach (var m in proposal.Members)
        {
            table.AddRow(
                Markup.Escape(m.ProposedName),
                Markup.Escape(m.Role.Title),
                m.IsNamed ? "yes" : "no");
        }

        AnsiConsole.Write(table);
        return 0;
    }

    // scaffolder team proposal confirm <proposalId> --project-id <id> [--intent new|augment|recast]
    public static async Task<int> ProposalConfirmAsync(
        ApiClient api, string projectId, string proposalId, string? intent, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(intent))
        {
            var proposal = await api.GetProposalAsync(projectId, proposalId, ct);
            if (proposal.ExistingTeamPresent)
            {
                intent = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("An existing team is present. How should the cast be applied?")
                        .AddChoices("new", "augment", "recast"));
            }
        }

        var req = new ConfirmProposalRequest { Intent = intent };
        var team = await api.ConfirmProposalAsync(projectId, proposalId, req, ct);

        AnsiConsole.MarkupLine($"[green]Team confirmed and written.[/]  Universe: {Markup.Escape(team.Universe)}");

        var table = new Table().Border(TableBorder.Simple);
        table.AddColumn("Name");
        table.AddColumn("Role");
        table.AddColumn("Status");

        foreach (var m in team.Members)
        {
            var statusMarkup = m.Status == "active" ? "[green]active[/]" : "[grey]retired[/]";
            table.AddRow(
                Markup.Escape(m.Name),
                Markup.Escape(m.RoleTitle),
                statusMarkup);
        }

        AnsiConsole.Write(table);
        return 0;
    }

    // scaffolder team proposal reject <proposalId> --project-id <id>
    public static async Task<int> ProposalRejectAsync(
        ApiClient api, string projectId, string proposalId, CancellationToken ct)
    {
        await api.RejectProposalAsync(projectId, proposalId, ct);
        AnsiConsole.MarkupLine("[green]Proposal rejected.[/]");
        return 0;
    }

    // scaffolder team charter show <name> --project-id <id>
    public static async Task<int> CharterShowAsync(
        ApiClient api, string projectId, string memberName, CancellationToken ct)
    {
        var charter = await api.GetCharterAsync(projectId, memberName, ct);
        AnsiConsole.MarkupLine($"[bold]Charter: {Markup.Escape(charter.MemberName)}[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine(charter.Content);
        return 0;
    }

    // scaffolder team charter edit <name> --project-id <id>
    public static async Task<int> CharterEditAsync(
        ApiClient api, string projectId, string memberName, CancellationToken ct)
    {
        var existing = await api.GetCharterAsync(projectId, memberName, ct);

        AnsiConsole.MarkupLine("[grey]Enter new charter content. Type a line with a single period (.) to finish:[/]");
        var lines = new List<string>();
        string? line;
        while ((line = Console.ReadLine()) != null && line != ".")
            lines.Add(line);

        var content = string.Join("\n", lines);
        if (string.IsNullOrWhiteSpace(content))
        {
            AnsiConsole.MarkupLine("[yellow]No content entered. Charter unchanged.[/]");
            return 0;
        }

        var req = new UpdateCharterRequest { Content = content };
        await api.UpdateCharterAsync(projectId, memberName, req, ct);
        AnsiConsole.MarkupLine("[green]Charter updated.[/]");
        return 0;
    }

    // scaffolder team member add --project-id <id> --role-id <id> [--custom-title <title>] [--model <id>]
    public static async Task<int> MemberAddAsync(
        ApiClient api, string[] args, string projectId, CancellationToken ct)
    {
        var roleId = GetFlag(args, "--role-id");
        if (string.IsNullOrWhiteSpace(roleId))
        {
            AnsiConsole.MarkupLine("[red]--role-id is required.[/]");
            return 1;
        }

        var customTitle = GetFlag(args, "--custom-title");
        var modelId = GetFlag(args, "--model");

        var req = new AddMemberRequest
        {
            RoleId = roleId,
            CustomRoleTitle = customTitle,
            ModelId = modelId,
        };

        var member = await api.AddMemberAsync(projectId, req, ct);
        AnsiConsole.MarkupLine($"[green]Member added:[/] {Markup.Escape(member.Name)}  Role: {Markup.Escape(member.RoleTitle)}");
        return 0;
    }

    // scaffolder team member remove <name> --project-id <id>
    public static async Task<int> MemberRemoveAsync(
        ApiClient api, string projectId, string memberName, CancellationToken ct)
    {
        await api.RemoveMemberAsync(projectId, memberName, ct);
        AnsiConsole.MarkupLine($"[green]Member {Markup.Escape(memberName)} retired.[/]");
        return 0;
    }

    // scaffolder team member rerole <name> --project-id <id> --role-id <id> [--custom-title <title>]
    public static async Task<int> MemberReroleAsync(
        ApiClient api, string[] args, string projectId, string memberName, CancellationToken ct)
    {
        var roleId = GetFlag(args, "--role-id");
        if (string.IsNullOrWhiteSpace(roleId))
        {
            AnsiConsole.MarkupLine("[red]--role-id is required.[/]");
            return 1;
        }

        var customTitle = GetFlag(args, "--custom-title");
        var req = new ReroleRequest { NewRoleId = roleId, CustomRoleTitle = customTitle };

        var member = await api.ReroleMemberAsync(projectId, memberName, req, ct);
        AnsiConsole.MarkupLine($"[green]Member {Markup.Escape(member.Name)} re-roled to:[/] {Markup.Escape(member.RoleTitle)}");
        return 0;
    }

    private static string? GetFlag(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    // scaffolder team sync status --project-id <id>
    public static async Task<int> SyncStatusAsync(ApiClient api, string projectId, CancellationToken ct)
    {
        var status = await api.GetSyncStatusAsync(projectId, ct);

        if (status.NothingToSync)
        {
            AnsiConsole.WriteLine("Nothing to sync.");
            return 0;
        }

        var table = new Table().Border(TableBorder.Simple);
        table.AddColumn("Path");
        table.AddColumn("Kind");

        foreach (var change in status.Changes)
        {
            table.AddRow(Markup.Escape(change.Path), Markup.Escape(change.Kind));
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine($"change_set_hash: {status.ChangeSetHash}");
        return 0;
    }

    // scaffolder team sync commit --project-id <id> [--message <msg>]
    public static async Task<int> SyncCommitAsync(ApiClient api, string projectId, string? messageFlag, CancellationToken ct)
    {
        var status = await api.GetSyncStatusAsync(projectId, ct);

        if (status.NothingToSync)
        {
            AnsiConsole.WriteLine("Nothing to sync.");
            return 0;
        }

        var table = new Table().Border(TableBorder.Simple);
        table.AddColumn("Path");
        table.AddColumn("Kind");

        foreach (var change in status.Changes)
        {
            table.AddRow(Markup.Escape(change.Path), Markup.Escape(change.Kind));
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine($"change_set_hash: {status.ChangeSetHash}");

        string? commitMessage = messageFlag;
        if (string.IsNullOrWhiteSpace(commitMessage))
        {
            commitMessage = AnsiConsole.Ask<string>(
                "[grey]Commit message (leave blank for default):[/]", string.Empty);
            if (string.IsNullOrWhiteSpace(commitMessage))
                commitMessage = null;
        }

        var request = new SyncCommitRequest
        {
            ExpectedChangeSetHash = status.ChangeSetHash,
            Message = commitMessage,
        };

        try
        {
            var result = await api.CommitSyncAsync(projectId, request, ct);
            AnsiConsole.MarkupLine($"[green]Committed:[/] {Markup.Escape(result.CommitId)}");
            return 0;
        }
        catch (SyncStateChangedException)
        {
            AnsiConsole.MarkupLine("[red]The team changed since you last reviewed. Run 'team sync status' again.[/]");
            return 1;
        }
    }
}
