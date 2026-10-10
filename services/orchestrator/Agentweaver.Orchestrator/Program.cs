extern alias AzureIdentity;

using System.Collections.Immutable;
using Agentweaver.Orchestrator;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator.Core;
using Agentweaver.Providers;
using Agentweaver.SourceControl;
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
var secretRedemption = builder.Configuration.GetSection("Identity:SecretRedemption");
var secretRedemptionOptions = new SourceControlSecretRedemptionOptions(
    issuerUri.AbsoluteUri,
    audience,
    secretRedemption["Audience"],
    secretRedemption["OwnerBaseAddress"]);
const string providerCatalogSection = "ProjectsConfig:ProviderCatalog";
var providerCatalog = builder.Configuration.GetSection(providerCatalogSection).Exists()
    ? ProviderCatalogConfiguration.Load(builder.Configuration, providerCatalogSection)
    : CreateEmptyProviderCatalog();
var agentGovernancePolicyOptions = ReadAgentGovernancePolicyOptions(builder.Configuration);
var sourceControlWorkspaceRoot = builder.Configuration["SourceControl:WorkspaceRoot"];
if (sourceControlWorkspaceRoot is not null &&
    (string.IsNullOrWhiteSpace(sourceControlWorkspaceRoot) ||
     !Path.IsPathFullyQualified(sourceControlWorkspaceRoot)))
    throw new InvalidOperationException(
        "Source Control workspace root must be an absolute filesystem path.");

builder.Services.AddSingleton<TokenCredential>(credential);
builder.Services.AddSingleton<NpgsqlDataSource>(_ =>
    CoordinationPostgresDataSource.Create(runtime.ConnectionString, credential));
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(providerCatalog);
builder.Services.AddSingleton<ProviderResolver>();
builder.Services.AddSingleton<AgtPolicyProvider>();
if (agentGovernancePolicyOptions is not null)
    builder.Services.AddSingleton(agentGovernancePolicyOptions);
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(services => new CoordinationOwnerStore(
    services.GetRequiredService<NpgsqlDataSource>(),
    options.Schema,
    services.GetRequiredService<TimeProvider>(),
    services.GetRequiredService<CoordinatorDecisionOwnerStore>()));
builder.Services.AddScoped<SessionSuspendResumeCoordinator>();
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
builder.Services.AddSingleton(services => new BacklogOwnerStore(
    services.GetRequiredService<NpgsqlDataSource>(),
    options.Schema));
builder.Services.AddSingleton(services => new SourceControlOwnerStore(
    services.GetRequiredService<NpgsqlDataSource>(),
    options.Schema,
    services.GetRequiredService<ProviderCatalog>(),
    services.GetRequiredService<ProviderResolver>()));
builder.Services.AddSingleton<ExecutableActionGrantOwnerStore>();
builder.Services.AddSingleton<IExecutableActionGrantOwnerLookup>(services =>
    services.GetRequiredService<ExecutableActionGrantOwnerStore>());
builder.Services.AddSingleton<IExecutableActionSourceReceiptWriter>(services =>
    services.GetRequiredService<ExecutableActionGrantOwnerStore>());
builder.Services.AddSingleton<IExecutableActionPolicyEvaluationReceiptWriter>(services =>
    services.GetRequiredService<ExecutableActionGrantOwnerStore>());
builder.Services.AddSingleton(services => new PostgresMafCheckpointStore(
    services.GetRequiredService<NpgsqlDataSource>(),
    options.Schema,
    services.GetService<IObjectStore>()));
builder.Services.AddSingleton(services => new MafExecutionOutputWitnessStore(options.Schema));
builder.Services.AddSingleton<IBacklogPrerequisiteEvidenceReader>(services =>
    new MafBacklogPrerequisiteEvidenceReader(
        services.GetRequiredService<NpgsqlDataSource>(),
        services.GetRequiredService<BacklogOwnerStore>(),
        services.GetRequiredService<CoordinationOwnerStore>(),
        services.GetRequiredService<CoordinatorDecisionOwnerStore>(),
        services.GetRequiredService<CoordinatorRunSelectionContextStore>(),
        services.GetRequiredService<SourceControlOwnerStore>(),
        services.GetRequiredService<PostgresMafCheckpointStore>(),
        services.GetRequiredService<MafExecutionOutputWitnessStore>()));
builder.Services.AddHttpClient<ProjectsRunSelectionClient>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient<EventsAddressedMessageClient>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient<RuntimeRunAdmissionClient>(client => client.Timeout = TimeSpan.FromSeconds(20))
    .ConfigurePrimaryHttpMessageHandler(RuntimeOwnerHttpTransport.CreateHandler);
builder.Services.AddSingleton(secretRedemptionOptions);
builder.Services.AddHttpClient<SourceControlSecretRedemptionClient>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient<GitHubSourceControlAdapter>(client =>
    {
        client.BaseAddress = new Uri("https://api.github.com/");
    })
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddTransient<ISourceControlAdapter>(services =>
    services.GetRequiredService<GitHubSourceControlAdapter>());
if (sourceControlWorkspaceRoot is not null)
    builder.Services.AddSingleton(new GitWorkspaceManager(sourceControlWorkspaceRoot));
builder.Services.AddTransient<IExecutableActionPolicyEvaluationJournal>(services =>
    services.GetRequiredService<EventsAddressedMessageClient>());
builder.Services.AddScoped(services => new ExecutableActionGuard(
    services.GetService<AgtPolicyProvider>(),
    services.GetService<AgtPolicyProviderOptions>(),
    services.GetService<IExecutableActionPolicyEvaluationJournal>(),
    services.GetService<IExecutableActionGrantOwnerLookup>(),
    services.GetRequiredService<TimeProvider>(),
    services.GetService<IExecutableActionSourceReceiptWriter>(),
    services.GetService<IExecutableActionPolicyEvaluationReceiptWriter>()));
var runtimeRegistrationEnabled = builder.Services.AddRuntimeRegistrationOwner(builder.Configuration, options);
if (runtimeRegistrationEnabled)
    builder.Services.AddHttpClient<MafBuildTestEnvironmentClient>(client => client.Timeout = TimeSpan.FromSeconds(15))
        .ConfigurePrimaryHttpMessageHandler(RuntimeOwnerHttpTransport.CreateHandler);
var runtimeUsageEnabled = builder.Services.AddRuntimeUsageSource(builder.Configuration);
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
app.MapBacklogEndpoints();
app.MapSuspendResumeEndpoints();
app.MapSourceControlEndpoints();
if (runtimeRegistrationEnabled)
{
    app.MapRuntimeRegistrationEndpoints();
    app.MapRuntimeSuspendResumeEndpoints();
}
if (runtimeUsageEnabled)
    app.MapRuntimeUsageSourceEndpoints();
app.Run();

static string Required(IConfiguration configuration, string key) =>
    !string.IsNullOrWhiteSpace(configuration[key])
        ? configuration[key]!
        : throw new InvalidOperationException($"Missing required configuration '{key}'.");

static ProviderCatalog CreateEmptyProviderCatalog() =>
    ProviderCatalog.Create([], [], []).Value
    ?? throw new InvalidOperationException("An empty provider catalog could not be constructed.");

static AgtPolicyProviderOptions? ReadAgentGovernancePolicyOptions(IConfiguration configuration)
{
    const string sectionName = "AgentGovernance:Policy";
    var section = configuration.GetSection(sectionName);
    if (!section.Exists())
        return null;

    if (!long.TryParse(section["ResourceGeneration"], out var generation))
        throw new InvalidOperationException(
            $"Missing or invalid configuration '{sectionName}:ResourceGeneration'.");
    var documents = section.GetSection("PlatformPolicyDocuments")
        .GetChildren()
        .Select(item => item.Value)
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value!)
        .ToImmutableArray();
    var options = new AgtPolicyProviderOptions(
        Required(configuration, sectionName + ":ResourceId"),
        generation,
        Required(configuration, sectionName + ":OptionsRevision"),
        documents);
    options.Validate();
    return options;
}

public partial class Program;
