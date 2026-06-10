using System.ComponentModel;
using Microsoft.Extensions.AI;
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
}
