using Spectre.Console;

namespace Scaffolder.Cli;

public static class GitHubAuthCommands
{
    // scaffolder github sign-in
    // Starts device flow, renders user_code + verification_uri, polls to completion.
    public static async Task<int> SignInAsync(ApiClient api, CancellationToken ct)
    {
        GitHubDeviceFlowResponse flow;
        try { flow = await api.StartGitHubDeviceFlowAsync(ct); }
        catch (ApiException ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to start sign-in:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        AnsiConsole.MarkupLine("[bold]GitHub Sign-In[/]");
        AnsiConsole.MarkupLine($"  Open: [link]{Markup.Escape(flow.VerificationUri)}[/link]");
        AnsiConsole.MarkupLine($"  Enter code: [bold]{Markup.Escape(flow.UserCode)}[/]");
        AnsiConsole.MarkupLine("Waiting for authorization...");

        var interval = TimeSpan.FromSeconds(Math.Max(flow.Interval, 5));
        var expiry = DateTimeOffset.UtcNow.AddSeconds(flow.ExpiresIn);

        while (!ct.IsCancellationRequested && DateTimeOffset.UtcNow < expiry)
        {
            await Task.Delay(interval, ct);
            GitHubPollResponse poll;
            try { poll = await api.PollGitHubAuthAsync(ct); }
            catch (OperationCanceledException) { return 0; }
            catch (ApiException) { continue; /* transient — keep polling */ }

            switch (poll.Status)
            {
                case "success":
                    AnsiConsole.MarkupLine($"[green]Signed in as {Markup.Escape(poll.Login ?? "unknown")}[/]");
                    return 0;
                case "expired":
                    AnsiConsole.MarkupLine("[red]Authorization code expired. Run sign-in again.[/]");
                    return 1;
                case "denied":
                    AnsiConsole.MarkupLine("[red]Authorization denied.[/]");
                    return 1;
                // "pending" — keep polling
            }
        }

        AnsiConsole.MarkupLine("[red]Sign-in timed out.[/]");
        return 1;
    }

    // scaffolder github sign-out
    public static async Task<int> SignOutAsync(ApiClient api, CancellationToken ct)
    {
        await api.SignOutGitHubAsync(ct);
        AnsiConsole.MarkupLine("[green]Signed out of GitHub.[/]");
        return 0;
    }

    // scaffolder github status
    public static async Task<int> StatusAsync(ApiClient api, CancellationToken ct)
    {
        var status = await api.GetGitHubAuthStatusAsync(ct);
        switch (status.Status)
        {
            case "signed_in":
                AnsiConsole.MarkupLine($"[green]Signed in[/] as {Markup.Escape(status.Login ?? "unknown")}");
                break;
            case "signed_out":
                AnsiConsole.MarkupLine("[yellow]Signed out[/] (explicit sign-out; use 'scaffolder github sign-in' to sign back in)");
                break;
            default:
                AnsiConsole.MarkupLine("[grey]Not signed in[/]");
                break;
        }
        return 0;
    }
}
