using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Agentweaver.Abstractions;
using Agentweaver.Providers;
using Agentweaver.Telemetry;
using Npgsql;

namespace Agentweaver.EventsAndSessions;

/// <summary>Names the durable addressed-message operations supported by the PostgreSQL adapter.</summary>
public static class AddressedMessageCapabilities
{
    public const string Send = "messaging.addressed.send";
    public const string ThreadOrder = "messaging.addressed.thread_order";
    public const string Claim = "messaging.addressed.claim";
    public const string Presentation = "messaging.addressed.presentation";
    public const string Acknowledge = "messaging.addressed.acknowledge";
    public const string Expiry = "messaging.addressed.expiry";
    public const string Undeliverable = "messaging.addressed.undeliverable";
    public const string Fencing = "messaging.addressed.fencing";
    public const string DeliveryModes = "messaging.addressed.delivery_modes";
    public const string Correlation = "messaging.addressed.correlation";
    public const string PurposeNotifications = "messaging.addressed.purpose_notifications";

    public static ImmutableHashSet<string> All { get; } = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        Send,
        ThreadOrder,
        Claim,
        Presentation,
        Acknowledge,
        Expiry,
        Undeliverable,
        Fencing,
        DeliveryModes,
        Correlation,
        PurposeNotifications);
}

/// <summary>Configures the PostgreSQL addressed-message lease and lifetime limits.</summary>
/// <param name="OptionsRevision">Provider configuration revision used to match the catalog entry.</param>
/// <param name="ClaimLeaseSeconds">Duration a consumer may hold a message claim; defaults to 120 and must be 10 through 3600 seconds.</param>
/// <param name="MaximumMessageLifetimeDays">Maximum configured message lifetime; defaults to 1 and cannot exceed the system maximum.</param>
/// <param name="OptionsSchemaVersion">Options contract version; defaults to and must equal version 1.</param>
public sealed record NativePostgresMessagingProviderOptions(
    string OptionsRevision,
    int ClaimLeaseSeconds = 120,
    int MaximumMessageLifetimeDays = 1,
    int OptionsSchemaVersion = 1)
{
    /// <summary>
    /// Requires a nonempty revision without whitespace or control characters, options schema
    /// version 1, a claim lease of 10 through 3600 seconds, and a message lifetime within the
    /// system-supported range.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(OptionsRevision) ||
            OptionsRevision.Length > 128 ||
            OptionsRevision.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)) ||
            OptionsSchemaVersion != 1 ||
            ClaimLeaseSeconds is < 10 or > 3600 ||
            MaximumMessageLifetimeDays is < 1 or > AddressedMessageValidation.MaximumLifetimeDays)
            throw new ArgumentException("PostgreSQL Messaging provider options are invalid.");
    }
}

public sealed class NativePostgresMessagingProvider
{
    public const string ProviderId = "postgres.native-messaging";
    public static Version AdapterVersion { get; } = new(1, 0, 0);
    public const int OptionsSchemaVersion = 1;

    public ProviderDescriptor Descriptor { get; } = new(
        ProviderSeam.Messaging,
        ProviderId,
        AdapterVersion,
        OptionsSchemaVersion,
        ProviderHostingPattern.RemoteService,
        AddressedMessageCapabilities.All);

    /// <summary>Creates an enabled catalog registration from validated messaging options.</summary>
    public ProviderRegistration CreateRegistration(NativePostgresMessagingProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return new ProviderRegistration(
            Descriptor,
            Enabled: true,
            options.OptionsRevision,
            OptionsSchemaVersion);
    }

    /// <summary>Resolves and pins the Messaging provider after verifying the shared PostgreSQL resource identity.</summary>
    /// <returns>The immutable run binding, or a typed error when selection or resource negotiation fails.</returns>
    public async Task<ProviderResult<PinnedProviderBinding>> ResolveNegotiateAndPinAsync(
        ProviderResolver resolver,
        ProviderResolutionRequest request,
        NativePostgresMessagingProviderOptions options,
        PostgresSessionsProviderOptions resourceOptions,
        NpgsqlDataSource dataSource,
        string runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(resourceOptions);
        ArgumentNullException.ThrowIfNull(dataSource);
        options.Validate();
        resourceOptions.Validate();

        var resolution = resolver.Resolve(request);
        if (!resolution.IsSuccess || resolution.Value?.Candidate is not { } candidate)
            return ProviderResult<PinnedProviderBinding>.Failure(
                resolution.Error?.Code ?? ProviderErrorCode.MissingDefault,
                resolution.Error?.Message ?? "A Messaging provider is required.");
        if (candidate.Seam != ProviderSeam.Messaging ||
            candidate.ProviderId != ProviderId ||
            candidate.OptionsSchemaVersion != options.OptionsSchemaVersion ||
            candidate.OptionsRevision != options.OptionsRevision)
            return ProviderResult<PinnedProviderBinding>.Failure(
                ProviderErrorCode.InvalidConfiguration,
                "The Messaging candidate does not match the configured provider options.");

        try
        {
            await EventsAndSessionsMigrator.VerifyAsync(
                dataSource, resourceOptions.Schema, cancellationToken).ConfigureAwait(false);
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT current_database()", connection);
            var databaseName = (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(databaseName, resourceOptions.ExpectedDatabaseName, StringComparison.Ordinal))
                return ProviderResult<PinnedProviderBinding>.Failure(
                    ProviderErrorCode.ResourceMismatch,
                    "The live Messaging resource does not match its configured database identity.");

            var negotiation = new ResourceNegotiation(
                new ProviderResourceRef(
                    ProviderSeam.Messaging,
                    ProviderId,
                    resourceOptions.ResourceId,
                    resourceOptions.ResourceGeneration),
                AddressedMessageCapabilities.All);
            var pinned = resolver.Pin(
                runId, candidate, resourceOptions.ResourceId, negotiation);
            if (pinned.IsSuccess)
                RecordPinnedBinding(candidate, pinned.Value!);
            return pinned;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
        {
            return ProviderResult<PinnedProviderBinding>.Failure(
                ProviderErrorCode.InvalidNegotiation,
                "The configured Messaging resource could not be negotiated.");
        }
    }

    /// <summary>Records non-sensitive provider identity and a hash of the resource name for diagnostics.</summary>
    private static void RecordPinnedBinding(ProviderCandidate candidate, PinnedProviderBinding binding)
    {
        try
        {
            using var activity = TelemetrySignals.Activities.StartActivity(
                "messaging.provider.binding.pinned", ActivityKind.Internal);
            if (activity is null)
                return;

            activity.SetTag("provider.seam", ProviderSeam.Messaging.ToString());
            activity.SetTag("provider.resolved.id", candidate.ProviderId);
            activity.SetTag("provider.pinned.id", binding.ProviderId);
            activity.SetTag("provider.adapter.version", binding.AdapterVersion.ToString());
            activity.SetTag("provider.options.schema_version", binding.OptionsSchemaVersion);
            activity.SetTag("provider.options.revision", binding.OptionsRevision);
            activity.SetTag("provider.negotiated_capabilities",
                binding.NegotiatedCapabilities.OrderBy(value => value, StringComparer.Ordinal).ToArray());
            activity.SetTag("provider.resource.id_hash",
                Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes(binding.Resource.ResourceId)))[..24]);
            activity.SetTag("provider.resource.generation", binding.Resource.Generation);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Diagnostic telemetry never changes a successful provider binding.
        }
    }
}
