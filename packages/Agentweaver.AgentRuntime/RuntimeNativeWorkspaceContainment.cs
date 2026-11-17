using System.Text.Json;
using Agentweaver.Identity;
using GitHub.Copilot;

#pragma warning disable GHCP001 // Permission callback types are pinned to SDK 1.0.18.
namespace Agentweaver.AgentRuntime;

internal static class RuntimeNativeWorkspaceContainment
{
    private const string DenialCode = "runtime_tool_workspace_containment_denied";

    private static readonly HashSet<string> FilePathArgumentNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "path", "file_path", "filepath", "file_name", "filename", "directory", "paths"
    };

    private static readonly HashSet<string> OptionalPathArgumentNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cwd", "working_directory", "workingdirectory", "possible_paths", "possiblepaths"
    };

    internal static void RequirePreToolUse(string workspaceRoot, PreToolUseHookInput input) =>
        Guard(() =>
        {
            var root = RequireWorkspaceRoot(workspaceRoot);
            var workingDirectory = RequireContainedPath(root, input.WorkingDirectory);
            RequireToolArguments(root, workingDirectory, input.ToolName, input.ToolArgs);
        });

    internal static void RequirePermissionRequest(string workspaceRoot, PermissionRequest request) =>
        Guard(() =>
        {
            var root = RequireWorkspaceRoot(workspaceRoot);
            switch (request)
            {
                case PermissionRequestHook hook:
                    RequireToolArguments(root, root, hook.ToolName, hook.ToolArgs);
                    break;
                case PermissionRequestRead read:
                    RequireContainedPath(root, read.Path);
                    RequireOptionalPath(root, read.ResolvedPath);
                    RequireSandboxGrantPaths(root, read.SandboxPathGrant);
                    break;
                case PermissionRequestWrite write:
                    RequireContainedPath(root, write.FileName);
                    RequireOptionalPath(root, write.ResolvedPath);
                    RequireSandboxGrantPaths(root, write.SandboxPathGrant);
                    RequireWriteEditPaths(root, write.FileEdits);
                    break;
                case PermissionRequestShell shell:
                    var workingDirectory = RequireContainedPath(root, shell.ResolvedWorkingDirectory);
                    foreach (var path in shell.PossiblePaths ?? [])
                        RequireContainedPath(root, path, workingDirectory);
                    if (shell.ResolvedPaths is not null)
                    {
                        foreach (var (path, resolvedPath) in shell.ResolvedPaths)
                        {
                            RequireContainedPath(root, path, workingDirectory);
                            RequireContainedPath(root, resolvedPath);
                        }
                    }
                    RequireSandboxGrantPaths(root, shell.SandboxPathGrant);
                    break;
                default:
                    throw Denied();
            }
        });

    private static void RequireToolArguments(
        string root,
        string pathBase,
        string? toolName,
        JsonElement? arguments)
    {
        var requiresFilePath = toolName is "view" or "create" or "edit";
        if (arguments is not { ValueKind: JsonValueKind.Object } value)
        {
            if (requiresFilePath)
                throw Denied();
            return;
        }

        var hasFilePath = false;
        foreach (var property in value.EnumerateObject())
        {
            if (FilePathArgumentNames.Contains(property.Name))
            {
                RequireJsonPaths(root, pathBase, property.Value, allowEmptyArray: false);
                hasFilePath = true;
            }
            else if (OptionalPathArgumentNames.Contains(property.Name))
            {
                RequireJsonPaths(root, pathBase, property.Value, allowEmptyArray: true);
            }
        }

        if (requiresFilePath && !hasFilePath)
            throw Denied();
    }

    private static void RequireJsonPaths(string root, string pathBase, JsonElement value, bool allowEmptyArray)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            RequireContainedPath(root, value.GetString(), pathBase);
            return;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            if (!allowEmptyArray && value.GetArrayLength() == 0)
                throw Denied();
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    throw Denied();
                RequireContainedPath(root, item.GetString(), pathBase);
            }
            return;
        }

        throw Denied();
    }

    private static void RequireWriteEditPaths(string root, PermissionWriteFileEdit[]? edits)
    {
        if (edits is null)
            return;

        foreach (var edit in edits)
        {
            if (edit is null)
                throw Denied();

            var hasPath = false;
            if (edit.Before is not null)
            {
                RequireContainedPath(root, edit.Before.Path);
                hasPath = true;
            }
            if (edit.After is not null)
            {
                RequireContainedPath(root, edit.After.Path);
                hasPath = true;
            }
            if (!hasPath)
                throw Denied();
        }
    }

    private static void RequireSandboxGrantPaths(string root, PermissionSandboxPathGrant? grant)
    {
        if (grant is null)
            return;

        RequireOptionalPath(root, grant.Path);
        RequireOptionalPath(root, grant.DeniedPath);
        foreach (var path in grant.RemovedReadonlyPaths ?? [])
            RequireContainedPath(root, path);
    }

    private static void RequireOptionalPath(string root, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
            RequireContainedPath(root, path);
    }

    private static string RequireContainedPath(string root, string? requestedPath, string? pathBase = null)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
            throw Denied();

        var windowsPath = requestedPath.Replace('/', '\\');
        char[] pathSeparators = OperatingSystem.IsWindows() ? ['/', '\\'] : ['/'];
        if (windowsPath.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            windowsPath.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            windowsPath.StartsWith(@"\\", StringComparison.Ordinal) ||
            (!OperatingSystem.IsWindows() && IsDrivePath(windowsPath)) ||
            IsDriveRelativePath(windowsPath) ||
            requestedPath.Split(pathSeparators, StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment == ".."))
            throw Denied();

        if (Path.IsPathRooted(requestedPath) && !Path.IsPathFullyQualified(requestedPath))
            throw Denied();

        var resolved = Path.IsPathFullyQualified(requestedPath)
            ? Path.GetFullPath(requestedPath)
            : Path.GetFullPath(requestedPath, pathBase ?? root);
        if (!IsSameOrDescendant(resolved, root))
            throw Denied();

        var relative = Path.GetRelativePath(root, resolved);
        var current = root;
        if (relative == ".")
            return resolved;

        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "..")
                throw Denied();
            current = Path.Combine(current, segment);
            if (IsReparsePoint(current))
                throw Denied();
        }
        return resolved;
    }

    private static string RequireWorkspaceRoot(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw Denied();

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
        if (!Directory.Exists(root))
            throw Denied();
        var rootPrefix = Path.GetPathRoot(root);
        if (string.IsNullOrEmpty(rootPrefix))
            throw Denied();

        var current = rootPrefix;
        foreach (var segment in root[rootPrefix.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (IsReparsePoint(current))
                throw Denied();
        }
        return root;
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            if (new FileInfo(path).LinkTarget is not null ||
                new DirectoryInfo(path).LinkTarget is not null)
                return true;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }

        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static bool IsSameOrDescendant(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(path, root, comparison))
            return true;

        var rootPrefix = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;
        return path.StartsWith(rootPrefix, comparison);
    }

    private static bool IsDriveRelativePath(string path) =>
        path.Length >= 2 &&
        char.IsLetter(path[0]) &&
        path[1] == ':' &&
        (path.Length == 2 || path[2] is not ('\\' or '/'));

    private static bool IsDrivePath(string path) =>
        path.Length >= 2 &&
        char.IsLetter(path[0]) &&
        path[1] == ':';

    private static void Guard(Action validate)
    {
        try
        {
            validate();
        }
        catch (RuntimeAuthorizationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and not OperationCanceledException)
        {
            throw Denied();
        }
    }

    private static RuntimeAuthorizationException Denied() =>
        new(DenialCode);
}
#pragma warning restore GHCP001
