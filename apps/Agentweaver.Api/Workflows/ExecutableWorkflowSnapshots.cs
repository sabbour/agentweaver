using System.Security.Cryptography;
using System.Text;
using Agentweaver.Domain;

namespace Agentweaver.Api.Workflows;

internal static class ExecutableWorkflowSnapshots
{
    public static bool HasManifestData(Run run) =>
        run.ExecutableWorkflowManifestSchemaVersion is not null
        || run.ExecutableWorkflowDefinitionId is not null
        || run.ExecutableWorkflowDefinitionVersion is not null
        || run.ExecutableWorkflowSource is not null
        || run.ExecutableWorkflowContentDigest is not null
        || run.ExecutableWorkflowDefinitionYaml is not null
        || run.ExecutableWorkflowPinnedAt is not null;

    public static ExecutableWorkflowPin Create(WorkflowDefinition definition, string source)
    {
        var yaml = WorkflowDefinitionYamlSerializer.Serialize(definition);
        return new ExecutableWorkflowPin
        {
            ManifestSchemaVersion = ExecutableWorkflowPin.CurrentSchemaVersion,
            DefinitionId = definition.Id,
            DefinitionVersion = definition.Version,
            Source = source,
            ContentDigest = Digest(yaml),
            DefinitionYaml = yaml,
            PinnedAt = DateTimeOffset.UtcNow,
        };
    }

    public static string Digest(string yaml) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(yaml))).ToLowerInvariant();

    public static WorkflowDefinition Load(string runId, ExecutableWorkflowPin pin)
    {
        if (pin.ManifestSchemaVersion != ExecutableWorkflowPin.CurrentSchemaVersion)
            throw new WorkflowBindException(
                $"Run '{runId}' pinned executable workflow manifest schema version {pin.ManifestSchemaVersion} is not supported by this application.", runId);

        var actualDigest = Digest(pin.DefinitionYaml);
        if (!string.Equals(actualDigest, pin.ContentDigest, StringComparison.Ordinal))
            throw new WorkflowBindException(
                $"Run '{runId}' pinned executable workflow content digest mismatch: expected {pin.ContentDigest}, computed {actualDigest}.", runId);

        var loaded = WorkflowDefinitionLoader.Load(
            pin.DefinitionYaml, pin.Source, validationMode: WorkflowDefinitionValidationMode.LegacyCompatible);
        if (!loaded.IsValid || loaded.Definition is null)
            throw new WorkflowBindException(
                $"Run '{runId}' pinned executable workflow '{pin.DefinitionId}' could not be loaded: {loaded.Error ?? "unknown workflow error"}", runId);

        if (!string.Equals(loaded.Definition.Id, pin.DefinitionId, StringComparison.OrdinalIgnoreCase))
            throw new WorkflowBindException(
                $"Run '{runId}' pinned executable workflow identity mismatch: manifest references '{pin.DefinitionId}' but content contains '{loaded.Definition.Id}'.", runId);
        if (!string.Equals(loaded.Definition.Version, pin.DefinitionVersion, StringComparison.Ordinal))
            throw new WorkflowBindException(
                $"Run '{runId}' pinned executable workflow version mismatch: manifest references '{pin.DefinitionVersion}' but content contains '{loaded.Definition.Version}'.", runId);
        return loaded.Definition;
    }
}
