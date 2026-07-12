using System.ComponentModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Agentweaver.SandboxExec;

namespace Agentweaver.AgentTools.Tools;

internal sealed class RunCommandTool : ISandboxTool
{
    public string Name => "run_command";

    public AIFunction CreateFunction(SandboxToolContext ctx) =>
        AIFunctionFactory.Create(
            async (
                [Description("Shell command to execute inside the sandbox.")] string command,
                [Description("Timeout in milliseconds (bounded by the runtime policy).")] int? timeout_ms = null,
                CancellationToken ct = default) =>
            {
                if (ctx.Options.RejectBackgroundCommands && ContainsBackgrounding(command))
                    return "Command rejected: background/detached shell execution is not allowed.";

                var destructive = IsDestructivePattern(command, ctx.Options.DestructiveCommandPatterns);
                if (ctx.Options.RejectDestructiveCommands && destructive)
                    return "Command rejected: destructive shell commands are not allowed in the Build/Test gate.";

                // HITL gate: destructive commands require operator approval before execution.
                if (ctx.Options.RequireApprovalForAllShell || destructive)
                {
                    var commandHash = ComputeCommandHash(command);
                    var requestId = commandHash[..8]; // stable prefix — same command → same requestId

                    // If the operator already denied this command, refuse immediately.
                    if (ctx.IsCommandDenied?.Invoke(commandHash) == true)
                    {
                        ctx.Logger.LogWarning(
                            "Shell command denied by operator — requestId={RequestId} commandHash={Hash}",
                            requestId, commandHash);
                        return $"This command was denied by the operator (request ID: {requestId}). " +
                               "Do not retry this command.";
                    }

                    if (ctx.IsCommandApproved?.Invoke(commandHash) == true)
                    {
                        // Approved — fall through to execution below.
                        ctx.Logger.LogInformation(
                            "Shell command approved — requestId={RequestId} commandHash={Hash}",
                            requestId, commandHash);
                    }
                    else
                    {
                        ctx.Logger.LogWarning(
                            "Shell HITL approval required — requestId={RequestId} commandLength={Length} commandHash={Hash}",
                            requestId, command.Length, commandHash);

                        ctx.EmitEvent?.Invoke("shell.approval_required", new
                        {
                            requestId,
                            commandLength = command.Length,
                            commandHash,
                            command,
                            message = "Shell command requires operator approval before execution.",
                        });

                        return $"This command requires operator approval before it can execute " +
                               $"(request ID: {requestId}). " +
                               $"The operator can approve it via: POST /api/runs/{ctx.RunId}/shell-approvals " +
                               $"with body {{\"command_hash\":\"{commandHash}\"}}. " +
                               $"After approval, retry this command.";
                    }
                }

                var (validatorAllowed, validatorReason) = ShellCommandValidator.Validate(
                    command, ctx.WorkingDirectory, ctx.SandboxRoot);
                if (!validatorAllowed)
                    return $"Command rejected by shell validator: {validatorReason}";

                var fsPolicy = SandboxFsPolicyBuilder.Build(ctx.SandboxRoot, ctx.Options.AllowedRepositoryRoots);
                var timeout = timeout_ms ?? ctx.Options.DefaultTimeoutMs;
                if (timeout <= 0)
                    timeout = ctx.Options.DefaultTimeoutMs;
                if (ctx.Options.MaximumTimeoutMs > 0)
                    timeout = Math.Min(timeout, ctx.Options.MaximumTimeoutMs);
                var cmd = new SandboxCommand(command, ctx.WorkingDirectory, null, fsPolicy,
                    timeout,
                    NetworkEnabled: ctx.Options.NetworkEnabled,
                    AgentweaverRunId: string.IsNullOrEmpty(ctx.RunId) ? null : ctx.RunId);

                if (ctx.ShellSemaphore is not null)
                    await ctx.ShellSemaphore.WaitAsync(ct).ConfigureAwait(false);

                SandboxExecResult result;
                try
                {
                    result = await ctx.Executor.ExecuteAsync(cmd, ct).ConfigureAwait(false);
                }
                finally
                {
                    ctx.ShellSemaphore?.Release();
                }

                var stdout = ctx.Redactor.Redact(result.Stdout);
                var stderr = ctx.Redactor.Redact(result.Stderr);
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(stdout)) parts.Add($"stdout:\n{stdout}");
                if (!string.IsNullOrWhiteSpace(stderr)) parts.Add($"stderr:\n{stderr}");
                parts.Add($"exit_code: {result.ExitCode}");
                if (result.TimedOut) parts.Add("timed_out: true");
                if (result.OutputTruncated) parts.Add("output_truncated: true");
                return string.Join("\n", parts);
            },
            Name, "Run a shell command inside the sandbox.");

    private static string ComputeCommandHash(string command) =>
        Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(command)))[..16].ToLowerInvariant();

    private static bool IsDestructivePattern(string command, string[] patterns)
    {
        if (patterns.Length == 0) return false;

        // Normalize whitespace before matching so simple bypass variants
        // (double spaces, split flags) are caught. The mxc filesystem policy
        // remains the primary enforcement layer.
        var normalized = System.Text.RegularExpressions.Regex.Replace(
            command.Trim(), @"\s+", " ",
            System.Text.RegularExpressions.RegexOptions.None,
            TimeSpan.FromSeconds(1))
            .ToLowerInvariant();

        return patterns.Any(p =>
        {
            var np = System.Text.RegularExpressions.Regex.Replace(
                p.Trim(), @"\s+", " ").ToLowerInvariant();
            return normalized.Contains(np, StringComparison.Ordinal);
        });
    }

    internal static bool ContainsBackgrounding(string command)
    {
        var normalized = command.Trim();
        if (System.Text.RegularExpressions.Regex.IsMatch(
                normalized,
                @"(^|[\s;|])(?:nohup|disown|setsid)(?=\s|$)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
                    | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1)))
            return true;

        var quote = '\0';
        var escaped = false;
        for (var i = 0; i < normalized.Length; i++)
        {
            var current = normalized[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (current == '\\' && quote != '\'')
            {
                escaped = true;
                continue;
            }

            if (current is '\'' or '"')
            {
                if (quote == '\0')
                    quote = current;
                else if (quote == current)
                    quote = '\0';
                continue;
            }

            if (current != '&' || quote != '\0')
                continue;

            var previous = i > 0 ? normalized[i - 1] : '\0';
            var next = i + 1 < normalized.Length ? normalized[i + 1] : '\0';
            if (previous == '&' || next == '&' || previous == '>' || next == '>')
                continue;

            return true;
        }

        return false;
    }
}
