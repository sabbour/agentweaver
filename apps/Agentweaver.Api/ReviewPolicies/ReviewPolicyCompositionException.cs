namespace Agentweaver.Api.ReviewPolicies;

/// <summary>
/// Thrown when a review policy cannot be safely composed onto the live run workflow. This is distinct
/// from YAML validation: the policy may be schema-valid but still require a runtime gate binding that
/// is not available in this build.
/// </summary>
public sealed class ReviewPolicyCompositionException : Exception
{
    public ReviewPolicyCompositionException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
