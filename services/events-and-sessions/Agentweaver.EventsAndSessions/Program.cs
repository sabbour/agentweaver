using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.EventsAndSessions;
using Agentweaver.EventsAndSessions.Cost;
using Azure.Core;
using Agentweaver.Providers;
using Agentweaver.Telemetry;
using Agentweaver.ObjectStore.AzureBlob;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Authentication;
using Npgsql;
using OpenIddict.Validation.AspNetCore;

var migrate = args.SequenceEqual(["--migrate"], StringComparer.Ordinal);
if (args.Contains("--migrate", StringComparer.Ordinal) && !migrate)
    throw new ArgumentException("The --migrate command must be used by itself.");

var builder = WebApplication.CreateBuilder(migrate ? [] : args);
builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = 64 * 1024);
var providerSection = builder.Configuration.GetSection("EventsAndSessions:Provider");
var options = new PostgresSessionsProviderOptions(
    RequiredSection(providerSection, "ResourceId"),
    RequiredSection(providerSection, "DatabaseName"),
    providerSection.GetValue<long>("ResourceGeneration"),
    providerSection["Schema"] ?? "events_sessions",
    providerSection["OptionsRevision"] ?? "native-postgres-v1",
    providerSection.GetValue("OptionsSchemaVersion", 1),
    providerSection.GetValue("PollIntervalMilliseconds", 250),
    providerSection.GetValue("ReferenceRetentionDays", 365));
options.Validate();
var messagingSection = builder.Configuration.GetSection("EventsAndSessions:Messaging");
var messagingOptions = new NativePostgresMessagingProviderOptions(
    messagingSection["OptionsRevision"] ?? "native-postgres-messaging-v1",
    messagingSection.GetValue("ClaimLeaseSeconds", 120),
    messagingSection.GetValue("MaximumMessageLifetimeDays", 1),
    messagingSection.GetValue("OptionsSchemaVersion", 1));
messagingOptions.Validate();

if (migrate)
{
    var migrationConnection = EventsAndSessionsPostgresDataSource.ReadMigrationConnection(builder.Configuration);
    var migrationCredential = EventsAndSessionsPostgresDataSource.CreateCredential(migrationConnection);
    try
    {
        await using var migrationDataSource = EventsAndSessionsPostgresDataSource.Create(
            migrationConnection.ConnectionString, migrationCredential);
        await EventsAndSessionsMigrator.MigrateAsync(migrationDataSource, options.Schema);
    }
    finally
    {
        if (migrationCredential is IDisposable disposable)
            disposable.Dispose();
    }
    return;
}

var runtimeConnection = EventsAndSessionsPostgresDataSource.ReadRuntimeConnection(builder.Configuration);
var issuer = Required(builder.Configuration, "Identity:Issuer");
if (!Uri.TryCreate(issuer, UriKind.Absolute, out var issuerUri) || issuerUri.Scheme != Uri.UriSchemeHttps)
    throw new InvalidOperationException("Identity issuer must be an absolute HTTPS URI.");
var audience = Required(builder.Configuration, "Identity:Audience");
var acceptedEffectOptions = AcceptedEffectRuntimeOptions.Read(builder.Configuration, options.Schema);

builder.Logging.AddFilter("OpenIddict", LogLevel.Warning);
builder.Logging.AddFilter("OpenIddict.Validation.OpenIddictValidationDispatcher", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Authentication", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Logging.AddFilter("Npgsql", LogLevel.Warning);

builder.Services.AddSingleton<TokenCredential>(_ =>
    EventsAndSessionsPostgresDataSource.CreateCredential(runtimeConnection));
builder.Services.AddSingleton<NpgsqlDataSource>(services =>
    EventsAndSessionsPostgresDataSource.Create(
        runtimeConnection.ConnectionString, services.GetRequiredService<TokenCredential>()));
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(acceptedEffectOptions);
builder.Services.AddSingleton(messagingOptions);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddAgentweaverTelemetry("agentweaver.events");
builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow;
    json.SerializerOptions.MaxDepth = 16;
    json.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(
        System.Text.Json.JsonNamingPolicy.CamelCase));
});
builder.Services.AddSingleton<PostgresSessionsJournal>();
builder.Services.AddSingleton<ISessionsJournal>(services =>
    services.GetRequiredService<PostgresSessionsJournal>());
builder.Services.AddSingleton<IProjectFactJournal, PostgresProjectFactJournal>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient<KnowledgeAcceptedEffectReceiptClient>(client =>
    client.BaseAddress = acceptedEffectOptions.KnowledgeBaseAddress)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient<ProjectsConfigAuthorizationClient>(client =>
    client.BaseAddress = acceptedEffectOptions.ProjectsConfigBaseAddress)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddScoped<AcceptedEffectApplicationService>();
var projectsAuthorization = builder.Configuration.GetSection("ProjectsConfig:AuthorizationContext");
builder.Services.AddSingleton(new ProjectsAuthorizationContextOptions(
    projectsAuthorization["OwnerBaseAddress"],
    projectsAuthorization["Audience"],
    issuerUri.AbsoluteUri));
builder.Services.AddHttpClient<IProjectsAuthorizationContextClient, ProjectsAuthorizationContextClient>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
var coordinationOwner = builder.Configuration.GetSection("EventsAndSessions:OrchestratorOwner");
builder.Services.AddSingleton(new CoordinationOwnerClientOptions(
    coordinationOwner["OwnerBaseAddress"],
    coordinationOwner["Audience"],
    issuerUri.AbsoluteUri));
builder.Services.AddHttpClient<ICoordinationOwnerClient, CoordinationOwnerClient>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
builder.Services.AddOpenIddict().AddValidation(validation =>
{
    validation.SetIssuer(issuerUri);
    validation.AddAudiences(audience);
    validation.UseSystemNetHttp();
    validation.UseAspNetCore();
});
builder.Services.AddAuthorization();

var projectOverrides = builder.Configuration.GetSection("EventsAndSessions:ProjectOverrides")
    .GetChildren()
    .ToImmutableDictionary(section => section.Key, section => section.Value ?? string.Empty, StringComparer.Ordinal);
var provider = new NativePostgresSessionsProvider();
var registration = provider.CreateRegistration(options);
var messagingProvider = new NativePostgresMessagingProvider();
var messagingRegistration = messagingProvider.CreateRegistration(messagingOptions);
var costOptions = CopilotCostProviderOptions.FromConfiguration(
    builder.Configuration.GetSection("EventsAndSessions:Cost:Copilot"));
var costProvider = costOptions is null ? null : new CopilotCostProvider(costOptions);
var registrations = new List<ProviderRegistration> { registration, messagingRegistration };
if (costProvider is not null)
{
    registrations.Add(costProvider.CreateRegistration());
    builder.Services.AddSingleton(costProvider);
    builder.Services.AddSingleton<ICostProvider>(costProvider);
    builder.Services.AddSingleton<ICostProviderBinder, CopilotCostProviderBinder>();
}
var permittedOverrides = projectOverrides.Values.Distinct(StringComparer.Ordinal)
    .Select(providerId => new ProviderOverridePermission(ProviderSeam.Sessions, providerId))
    .ToArray();
var catalogResult = ProviderCatalog.Create(
    registrations,
    [
        new ProviderSelection(ProviderSeam.Sessions, NativePostgresSessionsProvider.ProviderId),
        new ProviderSelection(ProviderSeam.Messaging, NativePostgresMessagingProvider.ProviderId)
    ],
    permittedOverrides,
    meterSourceSelections: costProvider is null ? []
        : [new(CopilotCostProvider.MeterSource, CopilotCostProvider.ProviderId)]);
if (!catalogResult.IsSuccess)
    throw new InvalidOperationException("The Sessions provider catalog configuration is invalid.");
var resolver = new ProviderResolver(catalogResult.Value!);
builder.Services.AddSingleton(provider);
builder.Services.AddSingleton(messagingProvider);
builder.Services.AddSingleton(catalogResult.Value!);
builder.Services.AddSingleton(resolver);
var nativeUsageEnabled = builder.Services.AddNativeUsageConsumer(builder.Configuration);
var materialContainer = builder.Configuration["EventsAndSessions:SessionMaterial:ContainerUri"];
if (materialContainer is not null)
{
    if (!Uri.TryCreate(materialContainer, UriKind.Absolute, out var containerUri) ||
        containerUri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(containerUri.UserInfo) ||
        !string.IsNullOrEmpty(containerUri.Query) || !string.IsNullOrEmpty(containerUri.Fragment) ||
        containerUri.AbsolutePath.Trim('/').Contains('/') ||
        string.IsNullOrWhiteSpace(containerUri.AbsolutePath.Trim('/')))
        throw new InvalidOperationException("Session material requires an explicitly configured HTTPS Blob container.");
    builder.Services.AddSingleton<IObjectStore>(services => new AzureBlobObjectStore(
        new BlobContainerClient(containerUri, services.GetRequiredService<TokenCredential>())));
    builder.Services.AddSessionMaterialOwner();
    builder.Services.AddProducedRunCaptureOwner();
}
builder.Services.AddSingleton<IReadOnlyDictionary<string, string>>(projectOverrides);
builder.Services.AddSingleton<SessionsProviderBindingService>();
builder.Services.AddSingleton<ISessionsProviderBinder>(services =>
    services.GetRequiredService<SessionsProviderBindingService>());
builder.Services.AddSingleton<PostgresAddressedMessageStore>(services =>
    new PostgresAddressedMessageStore(
        services.GetRequiredService<NpgsqlDataSource>(),
        options,
        messagingOptions,
        messagingProvider,
        resolver,
        services.GetRequiredService<PostgresSessionsJournal>(),
        issuerUri.AbsoluteUri,
        services.GetRequiredService<TimeProvider>()));

var app = builder.Build();
var dataSource = app.Services.GetRequiredService<NpgsqlDataSource>();
await EventsAndSessionsMigrator.VerifyAsync(dataSource, options.Schema);
app.UseAuthentication();
app.UseAuthorization();
if (materialContainer is not null)
{
    app.MapSessionMaterialEndpoints();
    app.MapProducedRunCaptureEndpoints();
}
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", async (CancellationToken ct) =>
{
    try
    {
        await EventsAndSessionsMigrator.VerifyAsync(dataSource, options.Schema, ct);
        return Results.Ok(new { status = "ready" });
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        throw;
    }
    catch (Exception exception) when (exception is not OutOfMemoryException)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
});
app.MapEventsAndSessionsEndpoints();
app.MapAcceptedEffectEndpoints();
app.MapAddressedMessageEndpoints();
if (nativeUsageEnabled)
    app.MapNativeUsageEndpoints();
app.Run();

static string Required(IConfiguration configuration, string key) =>
    !string.IsNullOrWhiteSpace(configuration[key])
        ? configuration[key]!
        : throw new InvalidOperationException($"Missing required configuration '{key}'.");

static string RequiredSection(IConfigurationSection configuration, string key) =>
    !string.IsNullOrWhiteSpace(configuration[key])
        ? configuration[key]!
        : throw new InvalidOperationException($"Missing required configuration '{configuration.Path}:{key}'.");

public partial class Program;
