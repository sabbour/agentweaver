using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Agentweaver.Abstractions;

namespace Agentweaver.Providers.Sandbox.AgentSandbox;

public sealed record AgentSandboxOptions(
    int OptionsSchemaVersion,
    string OptionsRevision,
    string Namespace,
    string WorkspaceStorageProviderId,
    string ContainerImage,
    string RuntimeClassName,
    string ExpectedRuntimeHandler,
    string CpuRequest,
    string MemoryRequest,
    int ReconciliationTimeoutSeconds,
    int PollIntervalMilliseconds)
{
    public const int CurrentOptionsSchemaVersion = 1;

    private static readonly Regex CpuQuantity = new(
        "^(?:[1-9][0-9]*m|[1-9][0-9]*(?:\\.[0-9]+)?)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex MemoryQuantity = new(
        "^[1-9][0-9]*(?:Ki|Mi|Gi|Ti)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public AgentSandboxOptions Validate()
    {
        if (OptionsSchemaVersion != CurrentOptionsSchemaVersion)
            throw new ArgumentException("Agent Sandbox options schema version is unsupported.",
                nameof(OptionsSchemaVersion));
        ValidateOpaque(OptionsRevision, nameof(OptionsRevision));
        ValidateDnsSubdomain(Namespace, nameof(Namespace));
        ValidateProviderId(WorkspaceStorageProviderId);
        if (string.IsNullOrWhiteSpace(ContainerImage) || ContainerImage.Length > 512 ||
            ContainerImage.Any(char.IsControl) ||
            ContainerImage.Any(character => char.IsWhiteSpace(character) || character is '"' or '\'' or '\\') ||
            ContainerImage.Contains("://", StringComparison.Ordinal))
            throw new ArgumentException("A bounded container image reference is required.",
                nameof(ContainerImage));
        ValidateDnsSubdomain(RuntimeClassName, nameof(RuntimeClassName));
        ValidateDnsSubdomain(ExpectedRuntimeHandler, nameof(ExpectedRuntimeHandler));
        if (!CpuQuantity.IsMatch(CpuRequest))
            throw new ArgumentException("The CPU request is not a supported Kubernetes quantity.",
                nameof(CpuRequest));
        if (!MemoryQuantity.IsMatch(MemoryRequest))
            throw new ArgumentException("The memory request is not a supported Kubernetes quantity.",
                nameof(MemoryRequest));
        if (ReconciliationTimeoutSeconds is < 1 or > 900)
            throw new ArgumentOutOfRangeException(nameof(ReconciliationTimeoutSeconds));
        if (PollIntervalMilliseconds is < 1 or > 5000)
            throw new ArgumentOutOfRangeException(nameof(PollIntervalMilliseconds));
        return this;
    }

    private static void ValidateOpaque(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '_' or '-' or ':')))
            throw new ArgumentException("A valid opaque provider options revision is required.", name);
    }

    private static void ValidateProviderId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '_' or '-')))
            throw new ArgumentException("A valid workspace Storage provider ID is required.",
                nameof(WorkspaceStorageProviderId));
    }

    private static void ValidateDnsSubdomain(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 253 ||
            value.Split('.').Any(label =>
                label.Length is < 1 or > 63 ||
                label[0] is not (>= 'a' and <= 'z' or >= '0' and <= '9') ||
                label[^1] is not (>= 'a' and <= 'z' or >= '0' and <= '9') ||
                label.Any(character =>
                    character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-'))))
            throw new ArgumentException("A valid Kubernetes DNS subdomain is required.", name);
    }
}

public static class AgentSandboxProviderMetadata
{
    public const string ProviderId = "agent-sandbox";
    public static Version AdapterVersion { get; } = new(1, 0, 0);

    private static readonly ImmutableHashSet<string> Capabilities =
        ImmutableHashSet.Create(
            StringComparer.Ordinal,
            SandboxCapabilities.VmIsolation,
            SandboxCapabilities.WorkspacePersistentVolumeClaim);

    public static ProviderRegistration CreateRegistration(
        AgentSandboxOptions options,
        bool enabled = true)
    {
        options.Validate();
        return new ProviderRegistration(
            new ProviderDescriptor(
                ProviderSeam.Sandbox,
                ProviderId,
                AdapterVersion,
                AgentSandboxOptions.CurrentOptionsSchemaVersion,
                ProviderHostingPattern.KubernetesController,
                Capabilities),
            enabled,
            options.OptionsRevision,
            options.OptionsSchemaVersion);
    }
}
