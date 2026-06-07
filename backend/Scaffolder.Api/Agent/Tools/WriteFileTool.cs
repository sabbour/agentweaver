namespace Scaffolder.Api.Agent.Tools;

/// <summary>
/// Sandboxed write_file tool. Validates path via SandboxPathResolver before any file I/O.
/// Creates intermediate directories if needed.
///
/// Success: writes file content, returns tool.result.
/// PATH_ESCAPE: returns tool.rejected with PATH_ESCAPE code (no I/O performed).
/// </summary>
public sealed class WriteFileTool
{
    public const string ToolName = "write_file";

    private readonly SandboxPathResolver _resolver;
    private readonly ILogger<WriteFileTool> _logger;

    public WriteFileTool(SandboxPathResolver resolver, ILogger<WriteFileTool> logger)
    {
        _resolver = resolver;
        _logger = logger;
    }

    /// <summary>
    /// Writes text content to a file within the artifact directory sandbox.
    /// Creates intermediate directories as needed.
    /// </summary>
    /// <param name="requestedPath">Relative path requested by the agent.</param>
    /// <param name="content">Text content to write.</param>
    /// <param name="artifactDir">Absolute path to the sandbox root (artifact directory).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A ToolInvocationResult indicating success or failure.</returns>
    public async Task<ToolInvocationResult> InvokeAsync(
        string requestedPath,
        string content,
        string artifactDir,
        CancellationToken ct = default)
    {
        var resolution = _resolver.Resolve(requestedPath, artifactDir);

        if (!resolution.IsSuccess)
        {
            _logger.LogWarning(
                "WriteFileTool PATH_ESCAPE rejected: {RequestedPath}", requestedPath);

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

        try
        {
            // Create intermediate directories within the sandbox
            var directory = Path.GetDirectoryName(resolvedPath);
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(resolvedPath, content, ct);

            _logger.LogInformation(
                "WriteFileTool wrote {ByteCount} chars to {ResolvedPath}",
                content.Length, resolvedPath);

            return new ToolInvocationResult { IsSuccess = true };
        }
        catch (UnauthorizedAccessException)
        {
            return new ToolInvocationResult
            {
                IsSuccess = false,
                Error = new ToolInvocationError
                {
                    Code = "PERMISSION",
                    Message = $"Permission denied writing: {requestedPath}"
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WriteFileTool unexpected error writing {Path}", requestedPath);
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
