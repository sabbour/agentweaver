using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Agentweaver.Abstractions;
using Agentweaver.Providers;
using Agentweaver.Telemetry;
using Npgsql;

namespace Agentweaver.EventsAndSessions;

/// <summary>Configures the PostgreSQL resource, schema, and polling limits for the Sessions adapter.</summary>
/// <param name="ResourceId">Stable identity for the PostgreSQL Sessions resource used in provider bindings.</param>
/// <param name="ExpectedDatabaseName">Database name that the live PostgreSQL connection must report.</param>
/// <param name="ResourceGeneration">Positive generation recorded when the provider resource is pinned.</param>
/// <param name="Schema">PostgreSQL schema containing the Sessions tables.</param>
/// <param name="OptionsRevision">Provider configuration revision used to match the catalog entry.</param>
/// <param name="OptionsSchemaVersion">Options contract version; defaults to and must equal version 1.</param>
/// <param name="PollIntervalMilliseconds">Event subscription polling interval; defaults to 250 and must be 50 through 30,000.</param>
/// <param name="ReferenceRetentionDays">Object-reference retention period; defaults to 365 and must be 1 through 3650 days.</param>
public sealed record PostgresSessionsProviderOptions(
    string ResourceId,
    string ExpectedDatabaseName,
    long ResourceGeneration,
    string Schema,
    string OptionsRevision,
    int OptionsSchemaVersion = 1,
    int PollIntervalMilliseconds = 250,
    int ReferenceRetentionDays = 365)
{
    /// <summary>
    /// Requires token-shaped resource and database names, a positive generation, a non-reserved
    /// lowercase schema name, the supported options version, a polling interval of 50 through
    /// 30,000 milliseconds, and reference retention of 1 through 3650 days.
    /// </summary>
    public void Validate()
    {
        if (!IsToken(ResourceId) || !IsToken(ExpectedDatabaseName) ||
            ResourceGeneration < 1 || !IsSchema(Schema) ||
            !IsRevision(OptionsRevision) ||
            OptionsSchemaVersion != 1 ||
            PollIntervalMilliseconds is < 50 or > 30_000 ||
            ReferenceRetentionDays is < 1 or > 3650)
            throw new ArgumentException("PostgreSQL Sessions provider options are invalid.");
    }

    private static bool IsSchema(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 63 &&
        char.IsAsciiLetterLower(value[0]) &&
        value.All(character => char.IsAsciiLetterLower(character) ||
            char.IsAsciiDigit(character) || character == '_') &&
        value is not ("public" or "pg_catalog" or "information_schema") &&
        !value.StartsWith("pg_", StringComparison.Ordinal);

    private static bool IsToken(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-' or ':');

    private static bool IsRevision(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-' or ':');
}

public sealed class NativePostgresSessionsProvider
{
    public const string ProviderId = "postgres.native-sessions";
    public static Version AdapterVersion { get; } = new(1, 0, 0);
    public const int OptionsSchemaVersion = 1;

    public ProviderDescriptor Descriptor { get; } = new(
        ProviderSeam.Sessions,
        ProviderId,
        AdapterVersion,
        OptionsSchemaVersion,
        ProviderHostingPattern.RemoteService,
        SessionsCapabilities.All);

    /// <summary>Creates an enabled catalog registration for these validated Sessions options.</summary>
    public ProviderRegistration CreateRegistration(PostgresSessionsProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return new ProviderRegistration(
            Descriptor,
            Enabled: true,
            options.OptionsRevision,
            OptionsSchemaVersion);
    }

    /// <summary>Resolves a candidate, verifies the live database identity, negotiates capabilities, and pins it to a run.</summary>
    /// <returns>A provider result containing the immutable run binding, or a typed resolution/negotiation failure.</returns>
    public async Task<ProviderResult<PinnedProviderBinding>> ResolveNegotiateAndPinAsync(
        ProviderResolver resolver,
        ProviderResolutionRequest request,
        PostgresSessionsProviderOptions options,
        NpgsqlDataSource dataSource,
        string expectedResourceId,
        string runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dataSource);
        options.Validate();

        var resolution = resolver.Resolve(request);
        if (!resolution.IsSuccess || resolution.Value?.Candidate is not { } candidate)
            return ProviderResult<PinnedProviderBinding>.Failure(
                resolution.Error?.Code ?? ProviderErrorCode.MissingDefault,
                resolution.Error?.Message ?? "A Sessions provider is required.");

        var negotiation = await NegotiateAsync(candidate, options, dataSource, cancellationToken);
        if (!negotiation.IsSuccess)
            return ProviderResult<PinnedProviderBinding>.Failure(
                negotiation.Error!.Code, negotiation.Error.Message);

        var pinned = resolver.Pin(runId, candidate, expectedResourceId, negotiation.Value!);
        if (pinned.IsSuccess)
            RecordPinnedBinding(candidate, pinned.Value!);
        return pinned;
    }

    /// <summary>Checks the candidate against current options and confirms the configured schema and database.</summary>
    /// <returns>The negotiated resource or a typed failure; cancellation is propagated.</returns>
    public async Task<ProviderResult<ResourceNegotiation>> NegotiateAsync(
        ProviderCandidate candidate,
        PostgresSessionsProviderOptions options,
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dataSource);
        options.Validate();
        if (candidate.Seam != ProviderSeam.Sessions || candidate.ProviderId != ProviderId ||
            candidate.OptionsSchemaVersion != options.OptionsSchemaVersion ||
            candidate.OptionsRevision != options.OptionsRevision ||
            !candidate.RequiredCapabilities.IsSubsetOf(Descriptor.AdvertisedCapabilities))
            return ProviderResult<ResourceNegotiation>.Failure(
                ProviderErrorCode.InvalidConfiguration,
                "The Sessions candidate does not match the configured provider options.");

        try
        {
            await EventsAndSessionsMigrator.VerifyAsync(dataSource, options.Schema, cancellationToken);
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT current_database()", connection);
            var databaseName = (string?)await command.ExecuteScalarAsync(cancellationToken);
            if (!string.Equals(databaseName, options.ExpectedDatabaseName, StringComparison.Ordinal))
                return ProviderResult<ResourceNegotiation>.Failure(
                    ProviderErrorCode.ResourceMismatch,
                    "The live Sessions resource does not match its configured database identity.");

            return ProviderResult<ResourceNegotiation>.Success(new ResourceNegotiation(
                new ProviderResourceRef(
                    ProviderSeam.Sessions, ProviderId, options.ResourceId, options.ResourceGeneration),
                candidate.RequiredCapabilities));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return ProviderResult<ResourceNegotiation>.Failure(
                ProviderErrorCode.InvalidNegotiation,
                "The configured Sessions resource could not be negotiated.");
        }
    }

    /// <summary>Records non-sensitive provider and hashed resource identity for diagnostics.</summary>
    private static void RecordPinnedBinding(ProviderCandidate candidate, PinnedProviderBinding binding)
    {
        try
        {
            using var activity = TelemetrySignals.Activities.StartActivity(
                "sessions.provider.binding.pinned", ActivityKind.Internal);
            if (activity is null)
                return;

            activity.SetTag("provider.seam", ProviderSeam.Sessions.ToString());
            activity.SetTag("provider.resolved.id", candidate.ProviderId);
            activity.SetTag("provider.pinned.id", binding.ProviderId);
            activity.SetTag("provider.adapter.version", binding.AdapterVersion.ToString());
            activity.SetTag("provider.options.schema_version", binding.OptionsSchemaVersion);
            activity.SetTag("provider.options.revision", binding.OptionsRevision);
            activity.SetTag("provider.negotiated_capabilities",
                binding.NegotiatedCapabilities.OrderBy(value => value, StringComparer.Ordinal).ToArray());
            activity.SetTag("provider.resource.id_hash",
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(binding.Resource.ResourceId)))[..24]);
            activity.SetTag("provider.resource.generation", binding.Resource.Generation);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Telemetry is diagnostic only and must not change a successful provider binding.
        }
    }
}
