namespace Agentweaver.Api.Workflows;

/// <summary>
/// Provides the built-in predefined workflow that ships with the API (Feature 010, FR-005/FR-008).
/// The canonical source is the code-embedded <see cref="DefaultWorkflowTemplate"/> (NOT a checked-in
/// repo file): it is parsed through the SAME real loader as any project-authored workflow (no
/// mocks/placeholders, Principle VII), which doubles as the strongest correctness test of the schema
/// (FR-008). A project with no materialized <c>.scaffolders/workflows/</c> falls back to this default
/// so runs still execute and existing projects keep working without a migration.
/// </summary>
public static class BuiltInWorkflows
{
    public const string DefaultWorkflowId = "default";

    private static readonly Lazy<WorkflowLoadResult> _default = new(LoadDefault);

    /// <summary>The validated built-in default workflow. Throws if the code-embedded template ever
    /// fails to validate (a programming error, covered by the build/tests).</summary>
    public static WorkflowLoadResult Default => _default.Value;

    private static WorkflowLoadResult LoadDefault()
    {
        var result = WorkflowDefinitionLoader.Load(DefaultWorkflowTemplate.Yaml, "built-in", isBuiltIn: true);
        if (!result.IsValid)
            throw new InvalidOperationException(
                $"The built-in default workflow failed validation: {result.Error}");
        return result;
    }
}
