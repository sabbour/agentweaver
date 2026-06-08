using System.Diagnostics;
using AgentGovernance.Policy;

namespace Scaffolder.SandboxFs;

/// <summary>
/// AGT external policy backend that performs filesystem-aware sandbox
/// containment validation. Plugs into PolicyEngine.AddExternalBackend().
/// </summary>
public sealed class SandboxPolicyBackend : IExternalPolicyBackend
{
    private readonly string _sandboxRoot;

    /// <summary>Known file-tool names that require path validation.</summary>
    private static readonly HashSet<string> KnownFileTools = new(StringComparer.Ordinal)
    {
        "read_file", "write_file", "edit_file", "list_directory"
    };

    /// <summary>
    /// Known argument keys that may carry a filesystem path.
    /// Design rule (Seraph Y-2): ALL sandboxed file-tool functions MUST use
    /// one of these keys.
    /// </summary>
    private static readonly string[] PathArgumentKeys = ["path", "file_path", "directory"];

    public SandboxPolicyBackend(string sandboxRoot)
    {
        _sandboxRoot = Path.GetFullPath(sandboxRoot);
    }

    public string Name => "sandbox-path-containment";

    public ExternalPolicyDecision Evaluate(IReadOnlyDictionary<string, object> context)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var toolName = context.TryGetValue("tool_name", out var tn)
                ? tn?.ToString() : null;

            // Seraph Y-1: unrecognized/null tool names → denied
            if (toolName is null || !KnownFileTools.Contains(toolName))
            {
                return new ExternalPolicyDecision
                {
                    Backend = Name,
                    Allowed = false,
                    Reason = $"Unrecognized or null tool name '{toolName}'; denied by sandbox backend.",
                    EvaluationMs = sw.Elapsed.TotalMilliseconds,
                };
            }

            // Seraph Y-2: resolve path from known argument keys
            string? path = null;
            foreach (var key in PathArgumentKeys)
            {
                if (context.TryGetValue(key, out var p) && p is string s
                    && !string.IsNullOrWhiteSpace(s))
                {
                    path = s;
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                return new ExternalPolicyDecision
                {
                    Backend = Name,
                    Allowed = false,
                    Reason = "No path argument found in known keys; denied.",
                    EvaluationMs = sw.Elapsed.TotalMilliseconds,
                };
            }

            // Dispatch to correct validator based on whether path is absolute
            string resolved;
            if (Path.IsPathRooted(path))
            {
                resolved = SandboxPathValidator.ValidateAbsoluteContained(path, _sandboxRoot);
            }
            else
            {
                resolved = SandboxPathValidator.ValidateAndResolve(path, _sandboxRoot);
            }

            // FR-033: record resolved path in metadata for audit
            return new ExternalPolicyDecision
            {
                Backend = Name,
                Allowed = true,
                Reason = "Path is within sandbox boundary.",
                EvaluationMs = sw.Elapsed.TotalMilliseconds,
                Metadata = new Dictionary<string, object>
                {
                    ["resolved_path"] = resolved,
                },
            };
        }
        catch (SandboxViolationException ex)
        {
            return new ExternalPolicyDecision
            {
                Backend = Name,
                Allowed = false,
                Reason = ex.Message,
                EvaluationMs = sw.Elapsed.TotalMilliseconds,
            };
        }
        catch (Exception ex)
        {
            // Seraph Finding 3: fail-closed on ANY internal exception
            return new ExternalPolicyDecision
            {
                Backend = Name,
                Allowed = false,
                Reason = $"Internal error (fail-closed): {ex.GetType().Name}",
                EvaluationMs = sw.Elapsed.TotalMilliseconds,
            };
        }
    }

    public Task<ExternalPolicyDecision> EvaluateAsync(
        IReadOnlyDictionary<string, object> context, CancellationToken ct)
        => Task.FromResult(Evaluate(context));
}
