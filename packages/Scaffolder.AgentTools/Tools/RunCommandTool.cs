using System.ComponentModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Scaffolder.SandboxExec;

namespace Scaffolder.AgentTools.Tools;

internal sealed class RunCommandTool : ISandboxTool
{
    public string Name => "run_command";

    public AIFunction CreateFunction(SandboxToolContext ctx) =>
        AIFunctionFactory.Create(
            async (
                [Description("Shell command to execute inside the sandbox.")] string command,
                [Description("Timeout in milliseconds (default 30000).")] int? timeout_ms,
                CancellationToken ct = default) =>
            {
                var govArgs = new Dictionary<string, object>
                {
                    ["command"] = command,
                    ["directory"] = ctx.WorkingDirectory,
                    ["tool_name"] = Name,
                };
                var (allowed, reason) = ctx.EvaluateToolCall(Name, govArgs);
                if (!allowed) return $"Error: {reason}";

                // HITL gate: destructive commands require operator approval before execution.
                // TODO(T017-api): implement POST /api/runs/{id}/shell-approvals to expose
                // approval/denial to operators and unblock the pending model turn.
                if (ctx.Options.RequireApprovalForAllShell || IsDestructivePattern(command, ctx.Options.DestructiveCommandPatterns))
                {
                    var requestId = Guid.NewGuid().ToString("n")[..8];
                    var commandHash = Convert.ToHexString(
                        System.Security.Cryptography.SHA256.HashData(
                            System.Text.Encoding.UTF8.GetBytes(command)))[..16];

                    ctx.Logger.LogWarning(
                        "Shell HITL approval required — requestId={RequestId} commandLength={Length} commandHash={Hash}",
                        requestId, command.Length, commandHash);

                    ctx.EmitEvent?.Invoke("shell.approval_required", new
                    {
                        requestId,
                        commandLength = command.Length,
                        commandHash,
                        message = "Shell command requires operator approval before execution.",
                    });

                    return $"This command requires operator approval before it can execute " +
                           $"(request ID: {requestId}). The operator has been notified. " +
                           $"Please retry after approval is granted.";
                }

                var fsPolicy = SandboxFsPolicyBuilder.Build(ctx.SandboxRoot, ctx.Options.AllowedRepositoryRoots);
                var cmd = new SandboxCommand(command, ctx.WorkingDirectory, null, fsPolicy, timeout_ms ?? ctx.Options.DefaultTimeoutMs);
                var result = await ctx.Executor.ExecuteAsync(cmd, ct);
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
}
