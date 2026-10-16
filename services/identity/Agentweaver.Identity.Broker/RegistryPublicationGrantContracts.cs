using Agentweaver.Abstractions;

namespace Agentweaver.Identity.Broker;

internal static class RegistryPublicationGrantContracts
{
    public const string Purpose = "registry-publication";
    public const string AuthorityUnavailableCode = "registry_publication_authority_unavailable";

    public static string MismatchCode(RegistryPublicationGrantBindingField field) => field switch
    {
        RegistryPublicationGrantBindingField.Actor => "registry_publication_actor_mismatch",
        RegistryPublicationGrantBindingField.Project => "registry_publication_project_mismatch",
        RegistryPublicationGrantBindingField.Run => "registry_publication_run_mismatch",
        RegistryPublicationGrantBindingField.RegistryTarget => "registry_publication_target_mismatch",
        RegistryPublicationGrantBindingField.Selection => "registry_publication_selection_mismatch",
        RegistryPublicationGrantBindingField.Fence => "registry_publication_fence_mismatch",
        RegistryPublicationGrantBindingField.SecretId => "registry_publication_secret_id_mismatch",
        RegistryPublicationGrantBindingField.SecretVersion => "registry_publication_secret_version_mismatch",
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };
}

internal enum RegistryPublicationGrantBindingField
{
    Actor,
    Project,
    Run,
    RegistryTarget,
    Selection,
    Fence,
    SecretId,
    SecretVersion,
}

// This carries untrusted proposed values for equality checks only, not authenticated actor or owner evidence.
internal sealed class RegistryPublicationGrantCandidate
{
    public RegistryPublicationGrantCandidate(
        string actorId,
        string projectId,
        string runId,
        string registryTarget,
        string selection,
        long environmentFence,
        SecretRef registryCredential)
    {
        ActorId = ValidateIdentifier(actorId, nameof(actorId));
        ProjectId = ValidateIdentifier(projectId, nameof(projectId));
        RunId = ValidateIdentifier(runId, nameof(runId));
        RegistryTarget = ValidateText(registryTarget, nameof(registryTarget));
        Selection = ValidateText(selection, nameof(selection));
        if (environmentFence <= 0)
            throw new ArgumentOutOfRangeException(nameof(environmentFence));
        EnvironmentFence = environmentFence;
        RegistryCredential = registryCredential ?? throw new ArgumentNullException(nameof(registryCredential));
    }

    public string ActorId { get; }
    public string ProjectId { get; }
    public string RunId { get; }
    public string RegistryTarget { get; }
    public string Selection { get; }
    public long EnvironmentFence { get; }
    public SecretRef RegistryCredential { get; }

    private static string ValidateText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 ||
            value.Any(char.IsControl))
            throw new ArgumentException("A nonempty publication binding value is required.", parameterName);
        return value;
    }

    private static string ValidateIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 256 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '_' or '-' or ':')))
            throw new ArgumentException("Expected a nonempty opaque identifier.", parameterName);
        return value;
    }
}

internal sealed class RegistryPublicationGrantDeniedException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
