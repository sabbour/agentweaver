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
                if (!TryCreateRepositoryCredentialCommand(
                        command,
                        ctx.Options.RepositoryAccessToken,
                        out var directExecution,
                        out var credentialError))
                    return credentialError!;
                var environment = BuildCommandEnvironment(ctx.WorkingDirectory, scratchDirectory);

                var cmd = new SandboxCommand(
                    command,
                    ctx.WorkingDirectory,
                    environment,
                    fsPolicy,
                    timeout,
                    NetworkEnabled: ctx.Options.NetworkEnabled,
                    AgentweaverRunId: string.IsNullOrEmpty(ctx.RunId) ? null : ctx.RunId,
                    DirectExecution: directExecution);

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

                var stdout = RedactOutput(result.Stdout, ctx);
                var stderr = RedactOutput(result.Stderr, ctx);
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

    private static Dictionary<string, string> BuildCommandEnvironment(
        string workingDirectory,
        string? scratchDirectory)
    {
        const string sandboxHome = ".agentweaver-home";
        var sandboxHomePath = Path.Combine(workingDirectory, sandboxHome);
        Directory.CreateDirectory(Path.Combine(sandboxHomePath, ".cache"));
        Directory.CreateDirectory(Path.Combine(sandboxHomePath, ".local", "share"));
        Directory.CreateDirectory(Path.Combine(sandboxHomePath, ".config"));

        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HOME"] = sandboxHome,
            ["DOTNET_CLI_HOME"] = sandboxHome,
            ["XDG_CACHE_HOME"] = $"{sandboxHome}/.cache",
            ["XDG_DATA_HOME"] = $"{sandboxHome}/.local/share",
            ["XDG_CONFIG_HOME"] = $"{sandboxHome}/.config",
        };

        if (!string.IsNullOrWhiteSpace(scratchDirectory))
        {
            environment["AGENTWEAVER_SCRATCH"] = scratchDirectory;
            environment["AGENTWEAVER_SCRATCH_DIR"] = scratchDirectory;
            environment["TMPDIR"] = scratchDirectory;
            environment["TMP"] = scratchDirectory;
            environment["TEMP"] = scratchDirectory;
        }

        return environment;
    }

    private static readonly HashSet<string> BuiltInGitCommands = new(StringComparer.Ordinal)
    {
        "add", "apply", "blame", "branch", "checkout", "clean", "clone", "commit",
        "diff", "fetch", "grep", "log", "ls-files", "ls-remote", "ls-tree", "merge",
        "merge-base", "mv", "pull", "push", "rebase", "remote", "reset", "restore",
        "revert", "rm", "show", "show-ref", "sparse-checkout", "stash", "status",
        "switch", "tag", "worktree",
    };

    private static readonly HashSet<string> BuiltInGhCommands = new(StringComparer.Ordinal)
    {
        "api", "attestation", "auth", "cache", "codespace", "completion", "config",
        "gist", "gpg-key", "issue", "label", "org", "pr", "project", "release", "repo",
        "ruleset", "search", "secret", "ssh-key", "status", "variable", "workflow",
    };

    private static bool TryCreateRepositoryCredentialCommand(
        string command,
        string? accessToken,
        out SandboxDirectExecution? directExecution,
        out string? error)
    {
        directExecution = null;
        error = null;
        if (string.IsNullOrWhiteSpace(accessToken))
            return true;

        if (!TryParseCommand(command, out var arguments, out error))
            return false;
        if (arguments.Count == 0 ||
            (arguments[0] != "git" && arguments[0] != "gh"))
            return true;

        if (arguments[0] == "git")
        {
            if (!TryValidateGitArguments(arguments, out error))
                return false;

            var basicAuthorization = Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes($"x-access-token:{accessToken}"));
            var gitArguments = new List<string>
            {
                "--no-pager",
                "-c", "credential.helper=",
                "-c", "core.hooksPath=/dev/null",
                "-c", "protocol.allow=never",
                "-c", "protocol.https.allow=always",
                "-c", $"http.https://github.com/.extraheader=AUTHORIZATION: basic {basicAuthorization}",
            };
            gitArguments.AddRange(arguments.Skip(1));
            directExecution = new SandboxDirectExecution("git", gitArguments);
            return true;
        }

        if (!TryValidateGhArguments(arguments, out error))
            return false;

        directExecution = new SandboxDirectExecution(
            "gh",
            arguments.Skip(1).ToArray(),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["GH_TOKEN"] = accessToken,
                ["GH_PROMPT_DISABLED"] = "1",
            });
        return true;
    }

    private static string RedactOutput(string value, SandboxToolContext ctx)
    {
        var redacted = ctx.Redactor.Redact(value);
        if (string.IsNullOrWhiteSpace(ctx.Options.RepositoryAccessToken))
            return redacted;

        var basicAuthorization = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes($"x-access-token:{ctx.Options.RepositoryAccessToken}"));
        return redacted
            .Replace(ctx.Options.RepositoryAccessToken, "***", StringComparison.Ordinal)
            .Replace(basicAuthorization, "***", StringComparison.Ordinal);
    }

    private static bool TryValidateGitArguments(
        IReadOnlyList<string> arguments,
        out string? error)
    {
        error = null;
        if (arguments.Count < 2 || !BuiltInGitCommands.Contains(arguments[1]))
        {
            error = "Command rejected: repository credentials require a built-in git command.";
            return false;
        }

        foreach (var argument in arguments.Skip(2))
        {
            if (!IsUnsafeGitArgument(argument))
                continue;

            error = "Command rejected: git configuration, aliases, helpers, and alternate worktrees are not allowed with repository credentials.";
            return false;
        }

        return true;
    }

    private static bool IsUnsafeGitArgument(string argument) =>
        argument == "-c" ||
        argument.StartsWith("-c", StringComparison.Ordinal) ||
        argument == "-C" ||
        argument.StartsWith("--config", StringComparison.Ordinal) ||
        argument.StartsWith("--exec-path", StringComparison.Ordinal) ||
        argument.StartsWith("--git-dir", StringComparison.Ordinal) ||
        argument.StartsWith("--work-tree", StringComparison.Ordinal) ||
        argument.StartsWith("--namespace", StringComparison.Ordinal) ||
        argument.StartsWith("--upload-pack", StringComparison.Ordinal) ||
        argument.StartsWith("--receive-pack", StringComparison.Ordinal) ||
        argument.StartsWith("--git-upload-pack", StringComparison.Ordinal) ||
        argument.StartsWith("--git-receive-pack", StringComparison.Ordinal) ||
        argument.StartsWith("--paginate", StringComparison.Ordinal) ||
        argument == "-p" ||
        argument.StartsWith("ext::", StringComparison.OrdinalIgnoreCase);

    private static bool TryValidateGhArguments(
        IReadOnlyList<string> arguments,
        out string? error)
    {
        error = null;
        if (arguments.Count < 2 || !BuiltInGhCommands.Contains(arguments[1]))
        {
            error = "Command rejected: repository credentials require a built-in gh command.";
            return false;
        }

        var hasNestedCommand = (string topLevel, string nested) =>
            arguments[1] == topLevel &&
            arguments.Skip(2).Any(argument => argument == nested);
        if (arguments[1] == "codespace" ||
            arguments.Skip(2).Any(argument =>
                argument is "--web" or "--browser" or "--editor") ||
            hasNestedCommand("auth", "setup-git") ||
            hasNestedCommand("repo", "clone") ||
            hasNestedCommand("pr", "checkout"))
        {
            error = "Command rejected: gh commands that start another executable are not allowed with repository credentials.";
            return false;
        }

        return true;
    }

    private static bool TryParseCommand(
        string command,
        out IReadOnlyList<string> arguments,
        out string? error)
    {
        arguments = [];
        error = null;
        var parsed = new List<string>();
        var current = new System.Text.StringBuilder();
        var quote = '\0';
        var escaping = false;

        foreach (var character in command)
        {
            if (character is '\r' or '\n' or '\0' or ';' or '|' or '&' or '`' or '$' or
                '<' or '>' or '(' or ')' or '{' or '}' or '[' or ']' or '*' or '?' or '!' or '~')
            {
                error = "Command rejected: GitHub credentials require one direct git or gh command without shell metacharacters.";
                return false;
            }

            if (escaping)
            {
                current.Append(character);
                escaping = false;
                continue;
            }

            if (character == '\\' && quote != '\'')
            {
                escaping = true;
                continue;
            }

            if (character is '\'' or '"')
            {
                if (quote == '\0')
                    quote = character;
                else if (quote == character)
                    quote = '\0';
                else
                    current.Append(character);
                continue;
            }

            if (char.IsWhiteSpace(character) && quote == '\0')
            {
                if (current.Length > 0)
                {
                    parsed.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            current.Append(character);
        }

        if (escaping || quote != '\0')
        {
            error = "Command rejected: GitHub credentials require balanced, literal arguments.";
            return false;
        }
        if (current.Length > 0)
            parsed.Add(current.ToString());

        arguments = parsed;
        return true;
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
