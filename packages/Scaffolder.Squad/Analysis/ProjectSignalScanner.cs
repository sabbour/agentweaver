using System.Text;
using System.Text.Json;
using Scaffolder.SandboxFs;

namespace Scaffolder.Squad.Analysis;

/// <summary>
/// Scans a project working directory for detectable signals: languages, frameworks,
/// tests, docs, and structure. Produces a text summary only - never sends raw source.
/// All path access is validated through SandboxPathValidator.
/// </summary>
public sealed class ProjectSignalScanner
{
    private static readonly string[] ExcludedDirs =
        [".git", "node_modules", "bin", "obj", ".next", "dist", "build",
         "__pycache__", ".venv", "vendor", ".cache", "coverage"];

    private static readonly string[] ExcludedFileGlobs =
        [".env", ".env.local", ".env.production", "*.pem", "*.key", "*.pfx",
         "id_rsa", "id_ed25519", "credentials", "secrets.json", "*.secret"];

    private const int MaxFilesToScan = 500;

    private static readonly Dictionary<string, string> LanguageByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "C#",
        [".ts"] = "TypeScript",
        [".tsx"] = "TypeScript",
        [".js"] = "JavaScript",
        [".jsx"] = "JavaScript",
        [".py"] = "Python",
        [".go"] = "Go",
        [".rs"] = "Rust",
        [".java"] = "Java",
        [".rb"] = "Ruby",
    };

    public ProjectSignals Scan(string workingDirectory)
    {
        var languages = new SortedSet<string>(StringComparer.Ordinal);
        var frameworks = new SortedSet<string>(StringComparer.Ordinal);
        bool hasTests = false;
        bool hasDocs = false;
        bool hasCi = false;
        int fileCount = 0;

        var relativeFiles = EnumerateRelativeFiles(workingDirectory);

        foreach (var relativePath in relativeFiles)
        {
            if (fileCount >= MaxFilesToScan) break;
            fileCount++;

            var fileName = Path.GetFileName(relativePath);
            var ext = Path.GetExtension(relativePath);
            var normalized = relativePath.Replace('\\', '/');

            if (LanguageByExtension.TryGetValue(ext, out var lang))
                languages.Add(lang);

            if (normalized.Contains("/tests/", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("tests/", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("/__tests__/", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("__tests__/", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("/spec/", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("spec/", StringComparison.OrdinalIgnoreCase) ||
                fileName.Contains(".test.", StringComparison.OrdinalIgnoreCase) ||
                fileName.Contains(".spec.", StringComparison.OrdinalIgnoreCase))
                hasTests = true;

            if (fileName.Equals("README.md", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("docs/", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("/docs/", StringComparison.OrdinalIgnoreCase))
                hasDocs = true;

            if (normalized.StartsWith(".github/workflows/", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals(".gitlab-ci.yml", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("azure-pipelines.yml", StringComparison.OrdinalIgnoreCase))
                hasCi = true;

            DetectFrameworks(workingDirectory, relativePath, fileName, frameworks);
        }

        var sizeEstimate = fileCount switch
        {
            < 50 => "small",
            < 250 => "medium",
            _ => "large",
        };

        bool hasNoSignals =
            languages.Count == 0 &&
            frameworks.Count == 0 &&
            !hasTests && !hasDocs && !hasCi;

        return new ProjectSignals(
            Languages: languages.ToList(),
            Frameworks: frameworks.ToList(),
            HasTests: hasTests,
            HasDocs: hasDocs,
            HasCi: hasCi,
            SizeEstimate: sizeEstimate,
            HasNoSignals: hasNoSignals);
    }

    public string Summarize(ProjectSignals signals)
    {
        if (signals.HasNoSignals)
            return "No detectable signals found (the project appears empty).";

        var parts = new List<string>();

        parts.Add(signals.Languages.Count > 0
            ? "Languages: " + string.Join(", ", signals.Languages) + "."
            : "Languages: none detected.");

        if (signals.Frameworks.Count > 0)
            parts.Add("Frameworks: " + string.Join(", ", signals.Frameworks) + ".");

        parts.Add(signals.HasTests ? "Tests: test files or directories present." : "Tests: none detected.");
        parts.Add(signals.HasDocs ? "Docs: README or docs directory present." : "Docs: none detected.");
        parts.Add(signals.HasCi ? "CI: continuous integration configuration present." : "CI: none detected.");
        parts.Add("Project size estimate: " + signals.SizeEstimate + ".");

        return string.Join(" ", parts);
    }

    private static IEnumerable<string> EnumerateRelativeFiles(string workingDirectory)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
            return results;

        var rootFull = Path.GetFullPath(workingDirectory);
        var pending = new Stack<string>();
        pending.Push(rootFull);

        while (pending.Count > 0 && results.Count < MaxFilesToScan)
        {
            var dir = pending.Pop();

            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(dir);
            }
            catch
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (results.Count >= MaxFilesToScan) break;

                var name = Path.GetFileName(entry);

                // Skip symlinks, junctions, and all reparse points to prevent escaping the sandbox.
                FileAttributes attrs;
                try { attrs = File.GetAttributes(entry); }
                catch { continue; }
                if ((attrs & FileAttributes.ReparsePoint) != 0) continue;

                bool isDir = (attrs & FileAttributes.Directory) != 0;

                if (isDir)
                {
                    if (ExcludedDirs.Contains(name, StringComparer.OrdinalIgnoreCase))
                        continue;
                    pending.Push(entry);
                }
                else
                {
                    if (IsExcludedFile(name))
                        continue;

                    var relative = Path.GetRelativePath(rootFull, entry);
                    results.Add(relative);
                }
            }
        }

        return results;
    }

    private static bool IsExcludedFile(string fileName)
    {
        foreach (var pattern in ExcludedFileGlobs)
        {
            if (pattern.StartsWith("*.", StringComparison.Ordinal))
            {
                var ext = pattern[1..];
                if (fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if (fileName.Equals(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static void DetectFrameworks(
        string workingDirectory, string relativePath, string fileName, SortedSet<string> frameworks)
    {
        try
        {
            if (fileName.Equals("package.json", StringComparison.OrdinalIgnoreCase))
            {
                var content = ReadValidated(relativePath, workingDirectory);
                if (content is null) return;
                DetectFromPackageJson(content, frameworks);
            }
            else if (fileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                var content = ReadValidated(relativePath, workingDirectory);
                if (content is null) return;
                if (content.Contains("AspNetCore", StringComparison.OrdinalIgnoreCase))
                    frameworks.Add("ASP.NET Core");
                if (content.Contains("Blazor", StringComparison.OrdinalIgnoreCase))
                    frameworks.Add("Blazor");
            }
            else if (fileName.Equals("requirements.txt", StringComparison.OrdinalIgnoreCase) ||
                     fileName.Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase))
            {
                var content = ReadValidated(relativePath, workingDirectory);
                if (content is null) return;
                if (content.Contains("django", StringComparison.OrdinalIgnoreCase))
                    frameworks.Add("Django");
                if (content.Contains("flask", StringComparison.OrdinalIgnoreCase))
                    frameworks.Add("Flask");
                if (content.Contains("fastapi", StringComparison.OrdinalIgnoreCase))
                    frameworks.Add("FastAPI");
            }
        }
        catch
        {
            // Never throw on a single file - signal detection is best-effort.
        }
    }

    private static void DetectFromPackageJson(string content, SortedSet<string> frameworks)
    {
        var deps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(content);
            foreach (var section in new[] { "dependencies", "devDependencies", "peerDependencies" })
            {
                if (doc.RootElement.TryGetProperty(section, out var obj) &&
                    obj.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in obj.EnumerateObject())
                        deps.Add(prop.Name);
                }
            }
        }
        catch
        {
            // Malformed package.json - fall back to substring matching below.
        }

        bool Has(string name) =>
            deps.Contains(name) || content.Contains("\"" + name + "\"", StringComparison.OrdinalIgnoreCase);

        if (Has("react")) frameworks.Add("React");
        if (Has("vue")) frameworks.Add("Vue");
        if (Has("@angular/core") || Has("angular")) frameworks.Add("Angular");
        if (Has("next")) frameworks.Add("Next.js");
        if (Has("svelte")) frameworks.Add("Svelte");
    }

    private static string? ReadValidated(string relativePath, string workingDirectory)
    {
        try
        {
            var fullPath = SandboxPathValidator.ValidateAndResolve(relativePath, workingDirectory);
            if (!File.Exists(fullPath)) return null;
            return File.ReadAllText(fullPath, Encoding.UTF8);
        }
        catch
        {
            return null;
        }
    }
}

public sealed record ProjectSignals(
    IReadOnlyList<string> Languages,
    IReadOnlyList<string> Frameworks,
    bool HasTests,
    bool HasDocs,
    bool HasCi,
    string SizeEstimate,
    bool HasNoSignals);
