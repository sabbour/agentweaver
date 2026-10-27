using System.Text.RegularExpressions;

namespace Agentweaver.Abstractions;

/// <summary>Exact P1 invocation identity required by an Environment operation flagged as P1-specific.</summary>
public sealed record P1InvocationBindingExpectation(
    Guid CoreInvocationId,
    Guid SetupInvocationId,
    string SetupReceiptHash,
    string RootSessionId,
    string AcceptedSelectionHash,
    long ExecutionFence)
{
    private static readonly Regex HashPattern = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    /// <summary>Rejects incomplete or unsupported caller expectations before Environment effects.</summary>
    public P1InvocationBindingExpectation Validate()
    {
        if (CoreInvocationId == Guid.Empty || SetupInvocationId == Guid.Empty ||
            ExecutionFence < 1 ||
            string.IsNullOrWhiteSpace(RootSessionId) ||
            RootSessionId.Length > 256 ||
            RootSessionId.Any(char.IsControl) ||
            !HashPattern.IsMatch(SetupReceiptHash ?? string.Empty) ||
            !HashPattern.IsMatch(AcceptedSelectionHash ?? string.Empty))
            throw new ArgumentException("The P1 invocation binding expectation is invalid.");
        return this;
    }
}
