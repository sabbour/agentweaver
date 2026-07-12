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
/// (spec-006 decouple-preview §4 + preview-forwarder). No model turn. Produces a <c>(command, cwd)</c>
/// that forces ALL-INTERFACE binding *hints* for every known stack (a loopback-only bind is still made
/// reachable by the pod-local TCP forwarder, but the hint is free and harmless). The platform NEVER
/// pins the app's port: the app uses its own framework default, the AgentHost discovers the actual
/// bound port, and a pod-IP-reachable public port is chosen dynamically by the forwarder. This removes
/// the old hardcoded <c>PORT=3000</c> injection so a busy 3000 can never break preview.
/// </summary>
public sealed class PreviewCommandResolver
{
    /// <summary>
    /// Maps a command-discovery cwd from the API-visible source tree into the equivalent directory
    /// in the retained pod-local checkout. Returns <see langword="null"/> for escapes/cross-volume
    /// paths instead of executing preview outside the verified checkout.
    /// </summary>
    public static string? MapExecutionCwd(
        string sourceTreeRoot,
        string resolvedSourceCwd,
        string executionTreeRoot)
    {
        if (string.IsNullOrWhiteSpace(sourceTreeRoot)
            || string.IsNullOrWhiteSpace(resolvedSourceCwd)
            || string.IsNullOrWhiteSpace(executionTreeRoot))
            return null;

        try
        {
            var sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceTreeRoot));
            var sourceCwd = Path.TrimEndingDirectorySeparator(Path.GetFullPath(resolvedSourceCwd));
            var relative = Path.GetRelativePath(sourceRoot, sourceCwd);
            if (Path.IsPathRooted(relative)
                || relative.Equals("..", StringComparison.Ordinal)
                || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
                return null;

            return Path.GetFullPath(Path.Combine(executionTreeRoot, relative));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves a run command from the files under <paramref name="worktreePath"/>. Never throws:
    /// any IO/parse error degrades to <see cref="PreviewCommandResolution.Unresolved"/>.
    /// </summary>
    public PreviewCommandResolution Resolve(string worktreePath)
    {
        if (string.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath))
            return PreviewCommandResolution.Unresolved(worktreePath ?? string.Empty);

        try
        {
            var rootResult = TryResolveDirectory(worktreePath);
            if (rootResult is not null)
                return rootResult;

            foreach (var rel in new[] { "client", "app/client", "frontend", "web", "app", "src/client" })
            {
                var candidate = Path.Combine(worktreePath, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(candidate))
                    continue;

                var nested = TryResolveDirectory(candidate);
                if (nested is not null)
                    return nested;
            }

            return PreviewCommandResolution.Unresolved(worktreePath);
        }
        catch
        {
            return PreviewCommandResolution.Unresolved(worktreePath);
        }
    }

    private static PreviewCommandResolution? TryResolveDirectory(string dir)
    {
        try
        {
            // 1. Node package.json scripts (dev > start > preview > serve), with framework binds.
            var packageJson = Path.Combine(dir, "package.json");
            if (File.Exists(packageJson))
            {
                var node = ResolveFromPackageJson(packageJson, dir);
                if (node is not null)
                    return node;
            }

            // 2. ASP.NET / .NET single project. Bind all-interfaces on an OS-assigned port (":0");
            //    Kestrel logs the actual port, which the AgentHost observes.
            var csproj = Directory.EnumerateFiles(dir, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (csproj is not null)
            {
                const string url = "http://0.0.0.0:0";
                return new PreviewCommandResolution(
                    true,
                    $"ASPNETCORE_URLS={url} dotnet run --urls {url}",
                    dir,
                    "csproj");
            }

            // 3. Dockerfile CMD/ENTRYPOINT (containers bind 0.0.0.0 by convention → bind_uncertain).
            var dockerfile = Path.Combine(dir, "Dockerfile");
            if (File.Exists(dockerfile))
            {
                var cmd = ResolveFromDockerfile(dockerfile);
                if (cmd is not null)
                    return new PreviewCommandResolution(true, cmd, dir, "dockerfile", BindUncertain: true);
            }

            // 4. Makefile run/serve/dev target.
            var makefile = new[] { "Makefile", "makefile" }
                .Select(f => Path.Combine(dir, f))
                .FirstOrDefault(File.Exists);
            if (makefile is not null)
            {
                var target = ResolveMakefileTarget(makefile);
                if (target is not null)
                    return new PreviewCommandResolution(true, $"make {target}", dir, "makefile", BindUncertain: true);
            }

            // 5. Single Python entrypoint.
            if (File.Exists(Path.Combine(dir, "app.py")))
                return new PreviewCommandResolution(
                    true, "python app.py --host 0.0.0.0", dir, "python-app", BindUncertain: true);
            if (File.Exists(Path.Combine(dir, "main.py")))
                return new PreviewCommandResolution(
                    true, "python main.py --host 0.0.0.0", dir, "python-main", BindUncertain: true);

            // 6. Single Go entrypoint.
            if (File.Exists(Path.Combine(dir, "main.go")))
                return new PreviewCommandResolution(true, "go run .", dir, "go", BindUncertain: true);

            // 7. Single Node server file.
            if (File.Exists(Path.Combine(dir, "server.js")))
                return new PreviewCommandResolution(
                    true, "HOST=0.0.0.0 node server.js", dir, "node-server");
            if (File.Exists(Path.Combine(dir, "index.js")))
                return new PreviewCommandResolution(
                    true, "HOST=0.0.0.0 node index.js", dir, "node-index");

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static PreviewCommandResolution? ResolveFromPackageJson(string packageJson, string cwd)
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

            var (bindArgs, env) = FrameworkBind(scriptBody);
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
    /// all-interface bind for the detected framework in <paramref name="scriptBody"/>. The port is
    /// never pinned — the app keeps its framework default and the platform discovers/forwards it.
    /// </summary>
    private static (string BindArgs, string Env) FrameworkBind(string scriptBody)
    {
        var body = scriptBody.ToLowerInvariant();

        // Vite: --host 0.0.0.0.
        if (body.Contains("vite"))
            return ("--host 0.0.0.0", "");

        // Next.js: -H 0.0.0.0.
        if (body.Contains("next"))
            return ("-H 0.0.0.0", "");

        // react-scripts / CRA and generic Node servers read HOST from env.
        if (body.Contains("react-scripts") || body.Contains("react-app-rewired"))
            return ("", "HOST=0.0.0.0");

        // Angular CLI.
        if (body.Contains("ng serve") || body.Contains("angular"))
            return ("--host 0.0.0.0", "");

        // Everything else: set HOST env — many servers honor process.env.HOST.
        return ("", "HOST=0.0.0.0");
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
