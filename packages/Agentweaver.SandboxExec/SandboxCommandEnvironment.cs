using System.Diagnostics;
using System.Text;

namespace Agentweaver.SandboxExec;

internal static class SandboxCommandEnvironment
{
    public static void ApplyToProcessStartInfo(
        ProcessStartInfo startInfo,
        IReadOnlyDictionary<string, string>? environment)
    {
        if (environment is null || environment.Count == 0)
            return;

        foreach (var (key, value) in environment)
            startInfo.Environment[key] = value;
    }

    public static void RemoveInheritedCommandHelperVariables(ProcessStartInfo startInfo)
    {
        foreach (var key in startInfo.Environment.Keys.ToArray())
        {
            if (key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("GH_", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("GITHUB_", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("PAGER", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("EDITOR", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("VISUAL", StringComparison.OrdinalIgnoreCase))
            {
                startInfo.Environment.Remove(key);
            }
        }
    }

    public static string PrefixPosixExports(
        string commandLine,
        IReadOnlyDictionary<string, string>? environment)
    {
        if (environment is null || environment.Count == 0)
            return commandLine;

        var sb = new StringBuilder();
        foreach (var (key, value) in environment.OrderBy(
            static pair => pair.Key,
            StringComparer.Ordinal))
        {
            sb.Append("export ")
                .Append(key)
                .Append('=')
                .Append(ShellSingleQuote(value))
                .AppendLine();
        }

        sb.Append(commandLine);
        return sb.ToString();
    }

    private static string ShellSingleQuote(string value) =>
        "'" + value.Replace("'", "'\\''") + "'";
}
