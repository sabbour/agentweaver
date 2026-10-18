using System.Collections.Immutable;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Agentweaver.Abstractions;

namespace Agentweaver.Providers.Sandbox.AgentSandbox;

/// <summary>
/// Validated configuration for the Kubernetes-backed Agent Sandbox adapter.
/// It pins the runtime image and Kubernetes resources used by provision and recovery calls.
/// </summary>
/// <param name="OptionsSchemaVersion">Options contract version; the adapter supports version 2.</param>
/// <param name="OptionsRevision">Opaque revision used to match this configuration with its provider registration.</param>
/// <param name="Namespace">Kubernetes namespace used to create the Sandbox resources.</param>
/// <param name="WorkspaceStorageProviderId">Storage provider identifier used for persistent workspace volumes.</param>
/// <param name="ContainerImage">OCI image reference pinned to a SHA-256 digest.</param>
/// <param name="ContainerImagePlatform">Image platform; this adapter requires <c>linux/amd64</c>.</param>
/// <param name="ContainerImageCompressedPullBytes">Positive compressed image size used when evaluating startup progress.</param>
/// <param name="RuntimeClassName">Kubernetes RuntimeClass required for the Sandbox.</param>
/// <param name="ExpectedRuntimeHandler">Runtime handler expected from the selected RuntimeClass.</param>
/// <param name="CpuRequest">Kubernetes CPU request in the accepted integer, decimal, or millicore form.</param>
/// <param name="MemoryRequest">Kubernetes memory request in a positive Ki, Mi, Gi, or Ti quantity.</param>
/// <param name="ReconciliationTimeoutSeconds">Provisioning and recovery reconciliation timeout, from 1 through 900 seconds.</param>
/// <param name="PollIntervalMilliseconds">Reconciliation poll interval, from 1 through 5000 milliseconds.</param>
/// <param name="StartupBudgets">Positive per-phase and total startup deadlines.</param>
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
    /// <summary>The options contract version accepted by this adapter.</summary>
    public const int CurrentOptionsSchemaVersion = 2;

    /// <summary>Optional immutable configuration for the AgentHost workload profile.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentSandboxAgentHostProfile? AgentHost { get; init; }

    /// <summary>Optional accepted BuildTest profile that enables the command-Pod capability.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SandboxBuildTestAcceptedExecutionOptions? AcceptedBuildTestProfile { get; init; }

    private static readonly Regex CpuQuantity = new(
        "^(?:[1-9][0-9]*m|[1-9][0-9]*(?:\\.[0-9]+)?)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex MemoryQuantity = new(
        "^[1-9][0-9]*(?:Ki|Mi|Gi|Ti)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ImageReference = new(
        "^.+@sha256:[a-f0-9]{64}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Requires schema version 2, valid Kubernetes names, a digest-pinned linux/amd64 image,
    /// supported CPU and memory quantity forms, bounded reconciliation timings, and valid
    /// startup and optional nested profiles.
    /// </summary>
    /// <returns>This options instance when valid.</returns>
    /// <exception cref="ArgumentException">A schema version, identifier, image, or quantity is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A size or timeout is outside its supported range.</exception>
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
        if (AcceptedBuildTestProfile is { } buildTest)
            _ = buildTest.Validate();
        return this;
    }

    /// <summary>Names the immutable Kubernetes configuration and TLS secret mounted by AgentHost.</summary>
    public sealed record AgentSandboxAgentHostProfile(string ConfigurationMapName, string TlsSecretName);

    /// <summary>Gets the digest portion of the required OCI image reference.</summary>
    public string ContainerImageDigest => ContainerImage[(ContainerImage.LastIndexOf("@sha256:", StringComparison.Ordinal) + 1)..];

    /// <summary>Validates the bounded opaque revision string used to pin provider configuration.</summary>
    private static void ValidateOpaque(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '_' or '-' or ':')))
            throw new ArgumentException("A valid opaque provider options revision is required.", name);
    }

    /// <summary>Checks the restricted syntax accepted for a workspace Storage provider identifier.</summary>
    private static void ValidateProviderId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '_' or '-')))
            throw new ArgumentException("A valid workspace Storage provider ID is required.",
                nameof(WorkspaceStorageProviderId));
    }

    /// <summary>Checks that a value is a Kubernetes DNS subdomain.</summary>
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

/// <summary>
/// Per-phase and total startup deadlines used when evaluating Sandbox startup progress.
/// </summary>
/// <param name="ScheduledSeconds">Deadline for scheduling the Sandbox workload.</param>
/// <param name="ImageReadySeconds">Deadline for the pinned container image to become ready.</param>
/// <param name="StartedSeconds">Deadline for the workload to start.</param>
/// <param name="ConfiguredSeconds">Deadline for runtime configuration to complete.</param>
/// <param name="ReadySeconds">Deadline for the Sandbox to become ready.</param>
/// <param name="TotalSeconds">Overall startup deadline used in addition to the phase deadlines.</param>
public sealed record AgentSandboxStartupBudgets(
    int ScheduledSeconds,
    int ImageReadySeconds,
    int StartedSeconds,
    int ConfiguredSeconds,
    int ReadySeconds,
    int TotalSeconds)
{
    /// <summary>Requires every phase deadline and the total deadline to be positive.</summary>
    /// <returns>This budget set when every deadline is positive.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Any phase or total deadline is not positive.</exception>
    public AgentSandboxStartupBudgets Validate()
    {
        if (ScheduledSeconds <= 0 || ImageReadySeconds <= 0 || StartedSeconds <= 0 ||
            ConfiguredSeconds <= 0 || ReadySeconds <= 0 || TotalSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(AgentSandboxStartupBudgets),
                "Every startup phase and total time budget must be configured with a positive value.");
        return this;
    }

    /// <summary>Returns the deadline assigned to a startup phase.</summary>
    /// <param name="phase">The phase whose budget is requested.</param>
    /// <returns>The configured deadline in seconds.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The phase is not supported.</exception>
    public int For(SandboxStartupPhase phase) => phase switch
    {
        SandboxStartupPhase.Scheduled => ScheduledSeconds,
        SandboxStartupPhase.ImageReady => ImageReadySeconds,
        SandboxStartupPhase.Started => StartedSeconds,
        SandboxStartupPhase.Configured => ConfiguredSeconds,
        SandboxStartupPhase.Ready => ReadySeconds,
        _ => throw new ArgumentOutOfRangeException(nameof(phase))
    };

    /// <summary>Copies these values into the provider-neutral startup budget contract.</summary>
    public SandboxStartupTimeBudgets ToContract() => new(
        ScheduledSeconds, ImageReadySeconds, StartedSeconds, ConfiguredSeconds, ReadySeconds, TotalSeconds);
}

/// <summary>Describes the Agent Sandbox provider identity and constructs its registration contract.</summary>
public static class AgentSandboxProviderMetadata
{
    /// <summary>The stable identifier used to select this Sandbox provider.</summary>
    public const string ProviderId = "agent-sandbox";
    /// <summary>The adapter version advertised to provider selection.</summary>
    public static Version AdapterVersion { get; } = new(1, 0, 0);

    private static readonly ImmutableHashSet<string> Capabilities =
        ImmutableHashSet.Create(
            StringComparer.Ordinal,
            SandboxCapabilities.VmIsolation,
            SandboxCapabilities.WorkspacePersistentVolumeClaim);

    /// <summary>
    /// Creates a registration after validating the options and deriving supported capabilities.
    /// </summary>
    /// <param name="options">Configuration revision to advertise.</param>
    /// <param name="enabled">Whether callers may select this registration.</param>
    /// <returns>The Sandbox provider registration.</returns>
    public static ProviderRegistration CreateRegistration(
        AgentSandboxOptions options,
        bool enabled = true)
    {
        options.Validate();
        var capabilities = options.AcceptedBuildTestProfile is null
            ? Capabilities
            : Capabilities.Add(SandboxCapabilities.BuildTestCommandPod);
        return new ProviderRegistration(
            new ProviderDescriptor(
                ProviderSeam.Sandbox,
                ProviderId,
                AdapterVersion,
                AgentSandboxOptions.CurrentOptionsSchemaVersion,
                ProviderHostingPattern.KubernetesController,
                capabilities),
            enabled,
            options.OptionsRevision,
            options.OptionsSchemaVersion);
    }
}
