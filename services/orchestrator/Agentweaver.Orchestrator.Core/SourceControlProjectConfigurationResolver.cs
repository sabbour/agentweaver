using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Providers;

namespace Agentweaver.Orchestrator.Core;

public enum SourceControlProjectConfigurationFailure
{
    Missing,
    Invalid
}

public sealed class SourceControlProjectConfigurationException(
    SourceControlProjectConfigurationFailure failure,
    string message,
    Exception? innerException = null)
    : Exception(message, innerException)
{
    public SourceControlProjectConfigurationFailure Failure { get; } = failure;
}

public static class SourceControlProjectConfigurationResolver
{
    private static readonly JsonSerializerOptions ConfigurationJsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            MaxDepth = 16,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };

    public static SourceControlProjectSettings Resolve(JsonElement acceptedSelectionSnapshot)
    {
        if (acceptedSelectionSnapshot.ValueKind != JsonValueKind.Object ||
            !acceptedSelectionSnapshot.TryGetProperty("projectConfiguration", out var projectConfiguration) ||
            projectConfiguration.ValueKind != JsonValueKind.Object)
            throw Missing("The accepted run selection does not contain its project configuration snapshot.");

        if (!projectConfiguration.TryGetProperty("sourceControl", out var sourceControl) ||
            sourceControl.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            throw Missing("The accepted project configuration does not select a SourceControl repository.");
        if (sourceControl.ValueKind != JsonValueKind.Object)
            throw Invalid("The accepted SourceControl project settings are malformed.");

        try
        {
            return JsonSerializer.Deserialize<SourceControlProjectSettings>(
                    sourceControl.GetRawText(), ConfigurationJsonOptions)
                ?? throw Invalid("The accepted SourceControl project settings are empty.");
        }
        catch (JsonException exception)
        {
            throw Invalid("The accepted SourceControl project settings are malformed.", exception);
        }
        catch (ArgumentException exception)
        {
            throw Invalid("The accepted SourceControl project settings are invalid.", exception);
        }
    }

    public static SourceControlRepositoryPin PinNegotiatedRepository(
        JsonElement acceptedSelectionSnapshot,
        ProviderCatalog catalog,
        ProviderResolver resolver,
        SourceControlAcceptedRunBinding acceptedRun,
        string pinId,
        SourceControlRepositoryNegotiation negotiation,
        DateTimeOffset pinnedAt)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(acceptedRun);
        ArgumentNullException.ThrowIfNull(negotiation);
        ValidateSnapshotBinding(acceptedSelectionSnapshot, acceptedRun);
        var settings = Resolve(acceptedSelectionSnapshot);
        if (negotiation.Repository != settings.Repository)
            throw Invalid(
                "The negotiated physical repository does not match the accepted ProjectConfiguration.");

        var selection = SourceControlProviderSelectionResolver.Resolve(
            acceptedSelectionSnapshot, catalog, resolver);
        return SourceControlProviderSelectionResolver.PinNegotiatedRepository(
            resolver,
            selection,
            acceptedRun,
            pinId,
            negotiation,
            new SourceControlCredentialReference(
                settings.ApiSecretReference, SourceControlSecretPurposes.Api),
            settings.CheckoutSecretReference is { } checkout
                ? new SourceControlCredentialReference(checkout, SourceControlSecretPurposes.Checkout)
                : null,
            settings.WebhookSecretReference is { } webhook
                ? new SourceControlCredentialReference(webhook, SourceControlSecretPurposes.Webhook)
                : null,
            pinnedAt);
    }

    private static void ValidateSnapshotBinding(
        JsonElement snapshot,
        SourceControlAcceptedRunBinding acceptedRun)
    {
        if (snapshot.ValueKind != JsonValueKind.Object ||
            !TryReadString(snapshot, "projectId", out var projectId) ||
            !TryReadString(snapshot, "runId", out var runId) ||
            !TryReadInt64(snapshot, "projectRevision", out var projectRevision) ||
            !TryReadInt64(snapshot, "projectConfigurationRevision", out var configRevision) ||
            !TryReadInt64(snapshot, "platformRuntimeRevision", out var platformRevision) ||
            !TryReadString(snapshot, "contextRevision", out var contextRevision))
            throw Invalid("The accepted run selection snapshot is missing its immutable run binding.");

        var snapshotHash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(snapshot.GetRawText())));
        if (!string.Equals(projectId, acceptedRun.ProjectId, StringComparison.Ordinal) ||
            !string.Equals(runId, acceptedRun.RunId, StringComparison.Ordinal) ||
            projectRevision != acceptedRun.ProjectRevision ||
            configRevision != acceptedRun.ProjectConfigurationRevision ||
            platformRevision != acceptedRun.PlatformRuntimeRevision ||
            !string.Equals(contextRevision, acceptedRun.ContextRevision, StringComparison.Ordinal) ||
            !string.Equals(snapshotHash, acceptedRun.AcceptedSelectionHash, StringComparison.OrdinalIgnoreCase))
            throw Invalid("The accepted run selection snapshot no longer matches its actor, revisions, or hash.");
    }

    private static bool TryReadString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryReadInt64(JsonElement element, string name, out long value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) &&
               property.TryGetInt64(out value) &&
               value > 0;
    }

    private static SourceControlProjectConfigurationException Missing(string message) =>
        new(SourceControlProjectConfigurationFailure.Missing, message);

    private static SourceControlProjectConfigurationException Invalid(
        string message,
        Exception? innerException = null) =>
        new(SourceControlProjectConfigurationFailure.Invalid, message, innerException);
}
