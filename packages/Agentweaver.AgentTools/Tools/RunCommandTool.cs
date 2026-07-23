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

                var commandHash = ComputeCommandHash(command);

                // HITL gate: destructive commands require operator approval before execution.
                if (ctx.Options.RequireApprovalForAllShell || destructive)
                {
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

                var scratchDirectory = ResolveScratchDirectory(ctx);
                var fsPolicy = SandboxFsPolicyBuilder.Build(
                    ctx.SandboxRoot,
                    ctx.Options.AllowedRepositoryRoots,
                    additionalReadWriteRoots: string.IsNullOrWhiteSpace(scratchDirectory)
                        ? null
                        : [scratchDirectory]);

                // Pass the run's own allowed roots (RW + RO) so the validator's shared-mount
                // guard (#476) permits this run's own /workspace subtree in shared-execution
                // mode while still rejecting absolute paths into sibling runs/projects.
                var allowedRoots = fsPolicy.ReadWritePaths
                    .Concat(fsPolicy.ReadOnlyPaths)
                    .ToArray();
                var (validatorAllowed, validatorReason) = ShellCommandValidator.Validate(
                    command, ctx.WorkingDirectory, ctx.SandboxRoot, allowedRoots);
                if (!validatorAllowed)
                    return $"Command rejected by shell validator: {validatorReason}";

                var timeout = timeout_ms ?? ctx.Options.DefaultTimeoutMs;
                if (timeout <= 0)
                    timeout = ctx.Options.DefaultTimeoutMs;
                // #313: floor a sub-minimum caller timeout up to the policy floor BEFORE capping,
                // so an optimistically short model timeout_ms (e.g. 3 min) can't set a window that
                // kills a legitimate long Build/Test command under scheduling contention.
                if (ctx.Options.MinimumTimeoutMs > 0 && timeout < ctx.Options.MinimumTimeoutMs)
                    timeout = ctx.Options.MinimumTimeoutMs;
                if (ctx.Options.MaximumTimeoutMs > 0)
                    timeout = Math.Min(timeout, ctx.Options.MaximumTimeoutMs);
                var cmd = new SandboxCommand(
                    command,
                    ctx.WorkingDirectory,
                    BuildCommandEnvironment(scratchDirectory),
                    fsPolicy,
                    timeout,
                    NetworkEnabled: ctx.Options.NetworkEnabled,
                    AgentweaverRunId: string.IsNullOrEmpty(ctx.RunId) ? null : ctx.RunId);

                IDisposable? executionLease = null;
                SandboxExecResult result;
                try
                {
                    if (ctx.ShellExecutionTracker is not null)
                    {
                        // #313: the watchdog deadline is the executor's own timeout PLUS a grace
                        // margin, so the executor's CancelAfter fires first (graceful timed_out:true)
                        // and the watchdog only backstops a hung/unkillable process. Arming both at
                        // the same value made the watchdog win the race and fatally abort the turn.
                        executionLease = await ctx.ShellExecutionTracker.EnterAsync(
                            commandHash,
                            TimeSpan.FromMilliseconds(timeout) + SandboxToolOptions.WatchdogTimeoutGrace,
                            ct).ConfigureAwait(false);
                    }
                    result = await ctx.Executor.ExecuteAsync(cmd, ct).ConfigureAwait(false);
                }
                finally
                {
                    executionLease?.Dispose();
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

    private static string? ResolveScratchDirectory(SandboxToolContext ctx)
    {
        if (!string.IsNullOrWhiteSpace(ctx.ScratchDirectory))
            return ctx.ScratchDirectory;

        return Environment.GetEnvironmentVariable("AGENTWEAVER_SCRATCH")
            ?? Environment.GetEnvironmentVariable("AGENTWEAVER_SCRATCH_DIR");
    }

    private static IReadOnlyDictionary<string, string>? BuildCommandEnvironment(string? scratchDirectory)
    {
        if (string.IsNullOrWhiteSpace(scratchDirectory))
            return null;

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AGENTWEAVER_SCRATCH"] = scratchDirectory,
            ["AGENTWEAVER_SCRATCH_DIR"] = scratchDirectory,
            ["TMPDIR"] = scratchDirectory,
            ["TMP"] = scratchDirectory,
            ["TEMP"] = scratchDirectory,
        };
    }

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
