using Spectre.Console;

namespace Scaffolder.Cli;

/// <summary>Implements the sandbox-policy get and set commands.</summary>
public static class SandboxPolicyCommands
{
    public static async Task<int> GetAsync(ApiClient api, string repositoryPath, CancellationToken ct)
    {
        SandboxPolicy policy;
        try
        {
            policy = await api.GetSandboxPolicyAsync(repositoryPath, ct);
        }
        catch (ApiException ex)
        {
            AnsiConsole.MarkupLine($"[red]API error {ex.StatusCode}:[/] {Markup.Escape(ex.Body)}");
            return 1;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Field");
        table.AddColumn("Value");
        table.AddRow("Repository path", Markup.Escape(policy.RepositoryPath));
        table.AddRow("Shell enabled", policy.ShellEnabled ? "[green]true[/]" : "[yellow]false[/]");
        AnsiConsole.Write(table);
        return 0;
    }

    public static async Task<int> SetAsync(
        ApiClient api, string repositoryPath, bool shellEnabled, CancellationToken ct)
    {
        SandboxPolicy policy;
        try
        {
            policy = await api.SetSandboxPolicyAsync(
                new SetSandboxPolicyRequest
                {
                    RepositoryPath = repositoryPath,
                    ShellEnabled = shellEnabled
                }, ct);
        }
        catch (ApiException ex)
        {
            AnsiConsole.MarkupLine($"[red]API error {ex.StatusCode}:[/] {Markup.Escape(ex.Body)}");
            return 1;
        }

        AnsiConsole.MarkupLine(
            $"[green]Sandbox policy updated.[/] shell_enabled: {(policy.ShellEnabled ? "[green]true[/]" : "[yellow]false[/]")}");
        return 0;
    }
}
