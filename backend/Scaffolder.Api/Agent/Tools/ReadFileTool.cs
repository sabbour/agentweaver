namespace Scaffolder.Api.Agent.Tools;

/// <summary>
/// Result of a ReadFileTool invocation.
/// </summary>
public sealed class ToolInvocationResult
{
    public bool IsSuccess { get; init; }
    public string? Content { get; init; }
    public ToolInvocationError? Error { get; init; }
}

public sealed class ToolInvocationError
{
    public required string Code { get; init; }  // PATH_ESCAPE, NOT_FOUND, PERMISSION, UNKNOWN
    public required string Message { get; init; }
}

/// <summary>
/// Sandboxed read_file tool. Validates path via SandboxPathResolver before any file I/O.
/// 
/// Success: returns file content.
/// PATH_ESCAPE: returns tool.rejected with PATH_ESCAPE code (no I/O performed).
/// NOT_FOUND: returns tool.error with NOT_FOUND code.
/// </summary>
public sealed class ReadFileTool
{
    public const string ToolName = "read_file";

    private readonly SandboxPathResolver _resolver;
    private readonly ILogger<ReadFileTool> _logger;

    public ReadFileTool(SandboxPathResolver resolver, ILogger<ReadFileTool> logger)
    {
        _resolver = resolver;
        _logger = logger;
    }

    /// <summary>
    /// Reads a file from within the artifact directory sandbox.
    /// </summary>
    /// <param name="requestedPath">Relative path requested by the agent.</param>
    /// <param name="artifactDir">Absolute path to the sandbox root (artifact directory).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A ToolInvocationResult with the file content or an error.</returns>
    public async Task<ToolInvocationResult> InvokeAsync(
        string requestedPath,
        string artifactDir,
        CancellationToken ct = default)
    {
        var resolution = _resolver.Resolve(requestedPath, artifactDir);

        if (!resolution.IsSuccess)
        {
            _logger.LogWarning(
                "ReadFileTool PATH_ESCAPE rejected: {RequestedPath}", requestedPath);

            return new ToolInvocationResult
            {
                IsSuccess = false,
                Error = new ToolInvocationError
                {
                    Code = "PATH_ESCAPE",
                    Message = resolution.ErrorMessage ?? "Path escape rejected."
                }
            };
        }

        var resolvedPath = resolution.ResolvedPath!;

        if (!File.Exists(resolvedPath))
        {
            return new ToolInvocationResult
            {
                IsSuccess = false,
                Error = new ToolInvocationError
                {
                    Code = "NOT_FOUND",
                    Message = $"File not found: {requestedPath}"
                }
            };
        }

        try
        {
            var content = await File.ReadAllTextAsync(resolvedPath, ct);
            return new ToolInvocationResult { IsSuccess = true, Content = content };
        }
        catch (UnauthorizedAccessException)
        {
            return new ToolInvocationResult
            {
                IsSuccess = false,
                Error = new ToolInvocationError
                {
                    Code = "PERMISSION",
                    Message = $"Permission denied reading: {requestedPath}"
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReadFileTool unexpected error reading {Path}", requestedPath);
            return new ToolInvocationResult
            {
                IsSuccess = false,
                Error = new ToolInvocationError
                {
                    Code = "UNKNOWN",
                    Message = $"Unexpected error: {ex.Message}"
                }
            };
        }
    }
}
