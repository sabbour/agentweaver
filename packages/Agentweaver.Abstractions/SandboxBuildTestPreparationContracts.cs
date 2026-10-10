using System.Security.Cryptography;
using System.Text.Json;

namespace Agentweaver.Abstractions;

public sealed record SandboxBuildTestBindingPreparation(
    string SessionId,
    string ExecutionProfileReference,
    string ProviderOptionsRevision,
    string ExecutionOptionsSha256,
    SandboxBuildTestExpectedBinding ExpectedBinding,
    SandboxBuildTestAcceptedExecutionOptions ExecutionOptions)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public SandboxBuildTestBindingPreparation Validate(
        string sessionId,
        string executionProfileReference)
    {
        ValidateIdentifier(sessionId, nameof(SessionId), 256);
        ValidateIdentifier(executionProfileReference, nameof(ExecutionProfileReference), 128);
        ValidateIdentifier(ProviderOptionsRevision, nameof(ProviderOptionsRevision), 128);
        ArgumentNullException.ThrowIfNull(ExpectedBinding);
        ArgumentNullException.ThrowIfNull(ExecutionOptions);
        _ = ExpectedBinding.Validate();
        ExpectedBinding.ValidateSandboxProviderBinding();
        _ = ExecutionOptions.Validate();
        if (!string.Equals(SessionId, sessionId, StringComparison.Ordinal) ||
            !string.Equals(
                ExecutionProfileReference,
                executionProfileReference,
                StringComparison.Ordinal) ||
            !string.Equals(
                ExecutionOptions.ProfileId,
                executionProfileReference,
                StringComparison.Ordinal) ||
            !string.Equals(
                ExecutionOptionsSha256,
                ComputeExecutionOptionsSha256(ExecutionOptions),
                StringComparison.Ordinal))
            throw new ArgumentException("The BuildTest preparation does not match its session or pinned profile.");
        return this;
    }

    public static string ComputeExecutionOptionsSha256(
        SandboxBuildTestAcceptedExecutionOptions executionOptions)
    {
        ArgumentNullException.ThrowIfNull(executionOptions);
        _ = executionOptions.Validate();
        return Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(executionOptions, JsonOptions))).ToLowerInvariant();
    }

    private static void ValidateIdentifier(string? value, string name, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > maximumLength ||
            value.Any(char.IsControl))
            throw new ArgumentException("A bounded opaque identifier is required.", name);
    }
}
