extern alias AzureIdentity;

using Agentweaver.Orchestrator;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Agentweaver.Providers;
using Azure.Core;
using Npgsql;
using OpenIddict.Validation.AspNetCore;
using WorkloadIdentityCredential =
    AzureIdentity::Azure.Identity.WorkloadIdentityCredential;

var migrate = args.SequenceEqual(["--migrate"], StringComparer.Ordinal);
if (args.Contains("--migrate", StringComparer.Ordinal) && !migrate)
    throw new ArgumentException("The --migrate command must be used by itself.", nameof(args));

var builder = WebApplication.CreateBuilder(migrate ? [] : args);
builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = 64 * 1024);
var schema = builder.Configuration["Orchestrator:Schema"] ?? "orchestrator";
if (migrate)
{
    var migration = CoordinationPostgresDataSource.ReadConnection(
        builder.Configuration,
        "OrchestratorMigration",
        "Orchestrator:Migration:WorkloadIdentity");
    var migrationCredential = new WorkloadIdentityCredential(migration.Identity);
    await using var migrationDataSource = CoordinationPostgresDataSource.Create(
        migration.ConnectionString, migrationCredential);
    await CoordinationOwnerMigrator.MigrateAsync(migrationDataSource, schema);
    return;
}

var runtime = CoordinationPostgresDataSource.ReadConnection(
    builder.Configuration,
    "Orchestrator",
    "Orchestrator:Database:WorkloadIdentity");
var credential = new WorkloadIdentityCredential(runtime.Identity);
var issuer = Required(builder.Configuration, "Identity:Issuer");
if (!Uri.TryCreate(issuer, UriKind.Absolute, out var issuerUri) ||
    issuerUri.Scheme != Uri.UriSchemeHttps)
    throw new InvalidOperationException("Identity issuer must be an absolute HTTPS URI.");
var audience = Required(builder.Configuration, "Identity:Audience");
var projects = builder.Configuration.GetSection("ProjectsConfig:AuthorizationContext");
var events = builder.Configuration.GetSection("EventsAndSessions:Authorization");
var options = new OrchestratorOptions(
    issuerUri.AbsoluteUri,
    audience,
    projects["OwnerBaseAddress"] ?? string.Empty,
    projects["Audience"] ?? string.Empty,
    events["OwnerBaseAddress"] ?? string.Empty,
    events["Audience"] ?? string.Empty,
    schema);
const string providerCatalogSection = "ProjectsConfig:ProviderCatalog";
var providerCatalog = builder.Configuration.GetSection(providerCatalogSection).Exists()
    ? ProviderCatalogConfiguration.Load(builder.Configuration, providerCatalogSection)
    : CreateEmptyProviderCatalog();

builder.Services.AddSingleton<TokenCredential>(credential);
builder.Services.AddSingleton<NpgsqlDataSource>(_ =>
    CoordinationPostgresDataSource.Create(runtime.ConnectionString, credential));
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(providerCatalog);
builder.Services.AddSingleton<ProviderResolver>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(services => new CoordinationOwnerStore(
    services.GetRequiredService<NpgsqlDataSource>(),
    options.Schema,
    services.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton(services => new CoordinatorRunSelectionContextStore(
    services.GetRequiredService<NpgsqlDataSource>(),
    options.Schema,
    services.GetRequiredService<ProviderCatalog>(),
    services.GetRequiredService<ProviderResolver>(),
    services.GetServices<ICoordinatorSandboxResourceProvider>()));
builder.Services.AddSingleton(services => new CoordinatorDecisionOwnerStore(
    services.GetRequiredService<NpgsqlDataSource>(),
    options.Schema,
    services.GetRequiredService<CoordinatorRunSelectionContextStore>(),
    services.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<ExecutableActionGrantOwnerStore>();
builder.Services.AddSingleton<IExecutableActionGrantOwnerLookup>(services =>
    services.GetRequiredService<ExecutableActionGrantOwnerStore>());
builder.Services.AddSingleton<IExecutableActionSourceReceiptWriter>(services =>
    services.GetRequiredService<ExecutableActionGrantOwnerStore>());
builder.Services.AddSingleton(services => new PostgresMafCheckpointStore(
    services.GetRequiredService<NpgsqlDataSource>(),
    options.Schema,
    services.GetService<IObjectStore>()));
builder.Services.AddHttpClient<ProjectsRunSelectionClient>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient<EventsAddressedMessageClient>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
var runtimeRegistrationEnabled = builder.Services.AddRuntimeRegistrationOwner(builder.Configuration, options);
builder.Services.AddAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
builder.Services.AddOpenIddict().AddValidation(validation =>
{
    validation.SetIssuer(issuerUri);
    validation.AddAudiences(audience);
    validation.UseSystemNetHttp();
    validation.UseAspNetCore();
});
builder.Services.AddAuthorization();
builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.UnmappedMemberHandling =
        System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow;
    json.SerializerOptions.MaxDepth = 16;
    json.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(
        System.Text.Json.JsonNamingPolicy.CamelCase));
});

var app = builder.Build();
var dataSource = app.Services.GetRequiredService<NpgsqlDataSource>();
await CoordinationOwnerMigrator.VerifyAsync(dataSource, schema);
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", async (CancellationToken cancellationToken) =>
{
    try
    {
        await CoordinationOwnerMigrator.VerifyAsync(dataSource, schema, cancellationToken);
        return Results.Ok(new { status = "ready" });
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        throw;
    }
    catch (Exception exception) when (exception is not OutOfMemoryException)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
});
app.MapCoordinationEndpoints();
if (runtimeRegistrationEnabled)
    app.MapRuntimeRegistrationEndpoints();
app.Run();

static string Required(IConfiguration configuration, string key) =>
    !string.IsNullOrWhiteSpace(configuration[key])
        ? configuration[key]!
        : throw new InvalidOperationException($"Missing required configuration '{key}'.");

static ProviderCatalog CreateEmptyProviderCatalog() =>
    ProviderCatalog.Create([], [], []).Value
    ?? throw new InvalidOperationException("An empty provider catalog could not be constructed.");

public partial class Program;
