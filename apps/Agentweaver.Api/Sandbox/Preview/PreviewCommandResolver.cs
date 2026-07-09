using System.Text.Json;

namespace Agentweaver.Api.Sandbox.Preview;

/// <summary>Outcome of heuristic run-command resolution.</summary>
public sealed record PreviewCommandResolution(
    bool Resolved,
    string? Command,
    string? Cwd,
    string? Source,
    bool BindUncertain = false)
{
    public static PreviewCommandResolution Unresolved(string cwd) => new(false, null, cwd, "unresolved");
}

/// <summary>
/// Pure, deterministic (Phase 1, heuristic-only) run-command discovery from worktree files
/// (spec-006 decouple-preview §4). No model turn. Produces a <c>(command, cwd)</c> that forces
/// ALL-INTERFACE binding for every known stack (BLOCKER 4): a loopback-only bind passes the pod's
/// 127.0.0.1 health probe but fails Gateway pod-IP reachability, yielding a silent no-URL. Where a
/// bind cannot be forced the resolution is marked <see cref="PreviewCommandResolution.BindUncertain"/>
/// and still attempted (registration is the backstop).
/// </summary>
public sealed class PreviewCommandResolver
{
    /// <summary>Default preview port pinned when a stack lets us choose one.</summary>
    public const int DefaultPort = 3000;

    /// <summary>
    /// Resolves a run command from the files under <paramref name="worktreePath"/>. Never throws:
    /// any IO/parse error degrades to <see cref="PreviewCommandResolution.Unresolved"/>.
    /// </summary>
    public PreviewCommandResolution Resolve(string worktreePath, int port = DefaultPort)
    {
        if (string.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath))
            return PreviewCommandResolution.Unresolved(worktreePath ?? string.Empty);

        try
        {
            // 1. Node package.json scripts (dev > start > preview > serve), with framework binds.
            var packageJson = Path.Combine(worktreePath, "package.json");
            if (File.Exists(packageJson))
            {
                var node = ResolveFromPackageJson(packageJson, worktreePath, port);
                if (node is not null)
                    return node;
            }

            // 2. ASP.NET / .NET single project.
            var csproj = Directory.EnumerateFiles(worktreePath, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (csproj is not null)
            {
                var url = $"http://0.0.0.0:{port}";
                return new PreviewCommandResolution(
                    true,
                    $"ASPNETCORE_URLS={url} dotnet run --urls {url}",
                    worktreePath,
                    "csproj");
            }

            // 3. Dockerfile CMD/ENTRYPOINT (containers bind 0.0.0.0 by convention → bind_uncertain).
            var dockerfile = Path.Combine(worktreePath, "Dockerfile");
            if (File.Exists(dockerfile))
            {
                var cmd = ResolveFromDockerfile(dockerfile);
                if (cmd is not null)
                    return new PreviewCommandResolution(true, cmd, worktreePath, "dockerfile", BindUncertain: true);
            }

            // 4. Makefile run/serve/dev target.
            var makefile = new[] { "Makefile", "makefile" }
                .Select(f => Path.Combine(worktreePath, f))
                .FirstOrDefault(File.Exists);
            if (makefile is not null)
            {
                var target = ResolveMakefileTarget(makefile);
                if (target is not null)
                    return new PreviewCommandResolution(true, $"make {target}", worktreePath, "makefile", BindUncertain: true);
            }

            // 5. Single Python entrypoint.
            if (File.Exists(Path.Combine(worktreePath, "app.py")))
                return new PreviewCommandResolution(
                    true, $"python app.py --host 0.0.0.0 --port {port}", worktreePath, "python-app", BindUncertain: true);
            if (File.Exists(Path.Combine(worktreePath, "main.py")))
                return new PreviewCommandResolution(
                    true, $"python main.py --host 0.0.0.0 --port {port}", worktreePath, "python-main", BindUncertain: true);

            // 6. Single Go entrypoint.
            if (File.Exists(Path.Combine(worktreePath, "main.go")))
                return new PreviewCommandResolution(true, "go run .", worktreePath, "go", BindUncertain: true);

            // 7. Single Node server file.
            if (File.Exists(Path.Combine(worktreePath, "server.js")))
                return new PreviewCommandResolution(
                    true, $"HOST=0.0.0.0 PORT={port} node server.js", worktreePath, "node-server");
            if (File.Exists(Path.Combine(worktreePath, "index.js")))
                return new PreviewCommandResolution(
                    true, $"HOST=0.0.0.0 PORT={port} node index.js", worktreePath, "node-index");

            return PreviewCommandResolution.Unresolved(worktreePath);
        }
        catch
        {
            return PreviewCommandResolution.Unresolved(worktreePath);
        }
    }

    private static PreviewCommandResolution? ResolveFromPackageJson(string packageJson, string cwd, int port)
    {
        Dictionary<string, string>? scripts = null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(packageJson));
            if (doc.RootElement.TryGetProperty("scripts", out var s) && s.ValueKind == JsonValueKind.Object)
            {
                scripts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in s.EnumerateObject())
                    if (prop.Value.ValueKind == JsonValueKind.String)
                        scripts[prop.Name] = prop.Value.GetString() ?? "";
            }
        }
        catch
        {
            return null;
        }

        if (scripts is null || scripts.Count == 0)
            return null;

        // Prefer a script name in priority order.
        foreach (var name in new[] { "dev", "start", "preview", "serve" })
        {
            if (!scripts.TryGetValue(name, out var scriptBody))
                continue;

            var (bindArgs, env) = FrameworkBind(scriptBody, port);
            // `npm run <name>` — pass bind flags after `--` so they reach the underlying tool.
            var runner = $"npm run {name}";
            if (!string.IsNullOrEmpty(bindArgs))
                runner += $" -- {bindArgs}";
            var command = string.IsNullOrEmpty(env) ? runner : $"{env} {runner}";
            return new PreviewCommandResolution(true, command, cwd, $"package.json:{name}");
        }

        return null;
    }

    /// <summary>
    /// Returns extra CLI args (appended after <c>--</c>) and/or leading env vars that force an
    /// all-interface bind for the detected framework in <paramref name="scriptBody"/>.
    /// </summary>
    private static (string BindArgs, string Env) FrameworkBind(string scriptBody, int port)
    {
        var body = scriptBody.ToLowerInvariant();

        // Vite: --host 0.0.0.0 (+ pinned port).
        if (body.Contains("vite"))
            return ($"--host 0.0.0.0 --port {port}", "");

        // Next.js: -H 0.0.0.0 -p <port>.
        if (body.Contains("next"))
            return ($"-H 0.0.0.0 -p {port}", "");

        // react-scripts / CRA and generic Node servers read HOST/PORT from env.
        if (body.Contains("react-scripts") || body.Contains("react-app-rewired"))
            return ("", $"HOST=0.0.0.0 PORT={port}");

        // Angular CLI.
        if (body.Contains("ng serve") || body.Contains("angular"))
            return ($"--host 0.0.0.0 --port {port}", "");

        // Everything else: set HOST/PORT env — many servers honor process.env.HOST.
        return ("", $"HOST=0.0.0.0 PORT={port}");
    }

    private static string? ResolveFromDockerfile(string dockerfile)
    {
        string[] lines;
        try { lines = File.ReadAllLines(dockerfile); }
        catch { return null; }

        // Prefer the LAST CMD, else the LAST ENTRYPOINT.
        string? cmd = null;
        string? entrypoint = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("CMD ", StringComparison.OrdinalIgnoreCase))
                cmd = ParseDockerInstruction(line[4..]);
            else if (line.StartsWith("ENTRYPOINT ", StringComparison.OrdinalIgnoreCase))
                entrypoint = ParseDockerInstruction(line[11..]);
        }

        return cmd ?? entrypoint;
    }

    private static string? ParseDockerInstruction(string rest)
    {
        rest = rest.Trim();
        if (rest.Length == 0)
            return null;

        // Exec form: ["node","server.js"] → node server.js
        if (rest.StartsWith('['))
        {
            try
            {
                var parts = JsonSerializer.Deserialize<string[]>(rest);
                if (parts is { Length: > 0 })
                    return string.Join(' ', parts);
            }
            catch
            {
                return null;
            }
        }

        // Shell form: used as-is.
        return rest;
    }

    private static string? ResolveMakefileTarget(string makefile)
    {
        string[] lines;
        try { lines = File.ReadAllLines(makefile); }
        catch { return null; }

        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0 || line[0] == '\t' || line[0] == '#')
                continue;
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            var name = line[..colon].Trim();
            if (name.Length > 0 && !name.Contains(' '))
                targets.Add(name);
        }

        foreach (var candidate in new[] { "run", "serve", "dev", "start" })
            if (targets.Contains(candidate))
                return candidate;

        return null;
    }
}
