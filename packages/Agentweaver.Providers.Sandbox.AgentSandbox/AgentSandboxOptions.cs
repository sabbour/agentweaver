using System.Collections.Immutable;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Agentweaver.Abstractions;

namespace Agentweaver.Providers.Sandbox.AgentSandbox;

public sealed record AgentSandboxOptions(
    int OptionsSchemaVersion,
    string OptionsRevision,
    string Namespace,
    string WorkspaceStorageProviderId,
    string ContainerImage,
    string ContainerImagePlatform,
    long ContainerImageCompressedPullBytes,
    string RuntimeClassName,
    string ExpectedRuntimeHandler,
    string CpuRequest,
    string MemoryRequest,
    int ReconciliationTimeoutSeconds,
    int PollIntervalMilliseconds,
    AgentSandboxStartupBudgets StartupBudgets)
{
    public const int CurrentOptionsSchemaVersion = 2;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentSandboxAgentHostProfile? AgentHost { get; init; }

    private static readonly Regex CpuQuantity = new(
        "^(?:[1-9][0-9]*m|[1-9][0-9]*(?:\\.[0-9]+)?)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex MemoryQuantity = new(
        "^[1-9][0-9]*(?:Ki|Mi|Gi|Ti)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ImageReference = new(
        "^.+@sha256:[a-f0-9]{64}$",
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
            ContainerImage.Contains("://", StringComparison.Ordinal) ||
            !ImageReference.IsMatch(ContainerImage))
            throw new ArgumentException("A bounded OCI image reference pinned to a SHA-256 digest is required.",
                nameof(ContainerImage));
        if (!string.Equals(ContainerImagePlatform, "linux/amd64", StringComparison.Ordinal))
            throw new ArgumentException("AgentHost images must be pinned to the linux/amd64 platform.",
                nameof(ContainerImagePlatform));
        if (ContainerImageCompressedPullBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(ContainerImageCompressedPullBytes));
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
        ArgumentNullException.ThrowIfNull(StartupBudgets);
        _ = StartupBudgets.Validate();
        if (AgentHost is { } host)
        {
            ValidateDnsSubdomain(host.ConfigurationMapName, nameof(host.ConfigurationMapName));
            ValidateDnsSubdomain(host.TlsSecretName, nameof(host.TlsSecretName));
        }
        return this;
    }

    public sealed record AgentSandboxAgentHostProfile(string ConfigurationMapName, string TlsSecretName);

    public string ContainerImageDigest => ContainerImage[(ContainerImage.LastIndexOf("@sha256:", StringComparison.Ordinal) + 1)..];

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

public sealed record AgentSandboxStartupBudgets(
    int ScheduledSeconds,
    int ImageReadySeconds,
    int StartedSeconds,
    int ConfiguredSeconds,
    int ReadySeconds,
    int TotalSeconds)
{
    public AgentSandboxStartupBudgets Validate()
    {
        if (ScheduledSeconds <= 0 || ImageReadySeconds <= 0 || StartedSeconds <= 0 ||
            ConfiguredSeconds <= 0 || ReadySeconds <= 0 || TotalSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(AgentSandboxStartupBudgets),
                "Every startup phase and total time budget must be configured with a positive value.");
        return this;
    }

    public int For(SandboxStartupPhase phase) => phase switch
    {
        SandboxStartupPhase.Scheduled => ScheduledSeconds,
        SandboxStartupPhase.ImageReady => ImageReadySeconds,
        SandboxStartupPhase.Started => StartedSeconds,
        SandboxStartupPhase.Configured => ConfiguredSeconds,
        SandboxStartupPhase.Ready => ReadySeconds,
        _ => throw new ArgumentOutOfRangeException(nameof(phase))
    };

    public SandboxStartupTimeBudgets ToContract() => new(
        ScheduledSeconds, ImageReadySeconds, StartedSeconds, ConfiguredSeconds, ReadySeconds, TotalSeconds);
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
