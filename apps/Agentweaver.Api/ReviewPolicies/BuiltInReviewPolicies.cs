namespace Agentweaver.Api.ReviewPolicies;

/// <summary>
/// Provides the built-in predefined review policy that ships with the API (Feature 010, FR-032). The
/// canonical source is the code-embedded <see cref="DefaultReviewPolicyTemplate"/> (NOT a checked-in
/// repo file): it is parsed through the SAME real loader as any project-authored policy (no
/// mocks/placeholders, Principle VII), which doubles as the strongest correctness test of the schema.
/// A project with no materialized <c>.agentweaver/review-policies/</c> falls back to this default so
/// runs receive the safe RAI + human-review gates already baked into the default workflow.
/// </summary>
public static class BuiltInReviewPolicies
{
    public const string DefaultPolicyName = DefaultReviewPolicyTemplate.DefaultPolicyName;

    private static readonly Lazy<ReviewPolicyLoadResult> _default = new(LoadDefault);

    /// <summary>The validated built-in default review policy. Throws if the code-embedded template ever
    /// fails to validate (a programming error, covered by the build/tests).</summary>
    public static ReviewPolicyLoadResult Default => _default.Value;

    private static ReviewPolicyLoadResult LoadDefault()
    {
        var result = ReviewPolicyLoader.Load(DefaultReviewPolicyTemplate.Yaml, "built-in", isBuiltIn: true);
        if (!result.IsValid)
            throw new InvalidOperationException(
                $"The built-in default review policy failed validation: {result.Error}");
        return result;
    }
}
