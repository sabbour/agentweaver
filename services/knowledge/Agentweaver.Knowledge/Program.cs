extern alias AzureIdentity;

using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Knowledge;
using Agentweaver.Persistence.Postgres;
using Agentweaver.Providers;
using Agentweaver.Telemetry;
using Azure.Core;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Azure.Cosmos;
using Npgsql;
using StackExchange.Redis;

var migrate = args.SequenceEqual(["--migrate"], StringComparer.Ordinal);
if (args.Contains("--migrate", StringComparer.Ordinal) && !migrate)
    throw new ArgumentException("The --migrate command must be used by itself.", nameof(args));

var builder = WebApplication.CreateBuilder(migrate ? [] : args);
builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = 64 * 1024);
if (migrate)
{
    var memoryOptions = NativePostgresMemoryOptions.Read(builder.Configuration);
    var migrationConnection = KnowledgePostgresDataSource.ReadMigrationConnection(builder.Configuration);
    var migrationCredential = KnowledgePostgresDataSource.CreateCredential(migrationConnection);
    try
    {
        await using var migrationDataSource = KnowledgePostgresDataSource.Create(
            migrationConnection.ConnectionString, migrationCredential);
        await KnowledgeMigrator.MigrateAsync(
            migrationDataSource, memoryOptions.Schema, CancellationToken.None).ConfigureAwait(false);
    }
    finally
    {
        if (migrationCredential is IDisposable disposable)
            disposable.Dispose();
    }
    return;
}

var runtimeOptions = KnowledgeRuntimeOptions.Read(builder.Configuration);
var runtimeConnection = KnowledgePostgresDataSource.ReadRuntimeConnection(builder.Configuration);
var runtimeCredential = KnowledgePostgresDataSource.CreateCredential(runtimeConnection);
var dataSource = KnowledgePostgresDataSource.Create(runtimeConnection.ConnectionString, runtimeCredential);
var providerCatalog = ProviderCatalogConfiguration.Load(builder.Configuration);
var cosmosMemoryOptions = CosmosMemoryOptions.ReadOptional(builder.Configuration);
var redisMemoryOptions = RedisMemoryOptions.ReadOptional(builder.Configuration);

builder.Services.AddSingleton(runtimeOptions);
builder.Services.AddSingleton(runtimeOptions.MemoryProvider);
if (cosmosMemoryOptions is not null)
    builder.Services.AddSingleton(cosmosMemoryOptions);
builder.Services.AddSingleton<TokenCredential>(runtimeCredential);
builder.Services.AddSingleton<NpgsqlDataSource>(dataSource);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddAgentweaverTelemetry("agentweaver.knowledge");
builder.Services.AddSingleton(providerCatalog);
builder.Services.AddSingleton(new ProviderResolver(providerCatalog));
builder.Services.AddSingleton<NativePostgresMemoryProvider>();
builder.Services.AddSingleton(services => new PostgresOutbox(
    services.GetRequiredService<NpgsqlDataSource>(),
    runtimeOptions.MemoryProvider.Schema));
if (cosmosMemoryOptions is not null)
{
    builder.Services.AddSingleton(services => new CosmosClient(
        cosmosMemoryOptions.Endpoint.AbsoluteUri,
        services.GetRequiredService<TokenCredential>(),
        new CosmosClientOptions
        {
            SerializerOptions = new CosmosSerializationOptions
            {
                PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase
            }
        }));
    builder.Services.AddSingleton<ICosmosMemoryDocumentStore, CosmosMemoryDocumentStore>();
    builder.Services.AddSingleton<CosmosMemoryProvider>();
}
if (redisMemoryOptions is not null)
{
    builder.Services.AddSingleton(redisMemoryOptions);
    builder.Services.AddSingleton<IRedisMemoryCommandClient>(_ =>
        new RedisMemoryCommandClient(
            () => ConnectionMultiplexer.Connect(redisMemoryOptions.CreateConnectionOptions()),
            redisMemoryOptions));
    builder.Services.AddSingleton<RedisMemoryDocumentStore>();
    builder.Services.AddSingleton<RedisMemoryProvider>();
}
builder.Services.AddSingleton<IMemoryProvider>(services =>
    services.GetRequiredService<NativePostgresMemoryProvider>());
builder.Services.AddSingleton<IReadOnlyDictionary<string, IMemoryProvider>>(services =>
{
    var providers = ImmutableDictionary.CreateBuilder<string, IMemoryProvider>(StringComparer.Ordinal);
    var nativeProvider = services.GetRequiredService<NativePostgresMemoryProvider>();
    providers.Add(nativeProvider.Descriptor.Id, nativeProvider);
    if (cosmosMemoryOptions is not null)
    {
        var cosmosProvider = services.GetRequiredService<CosmosMemoryProvider>();
        providers.Add(cosmosProvider.Descriptor.Id, cosmosProvider);
    }
    if (redisMemoryOptions is not null)
    {
        var redisProvider = services.GetRequiredService<RedisMemoryProvider>();
        providers.Add(redisProvider.Descriptor.Id, redisProvider);
    }
    return providers.ToImmutable();
});
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient<ProjectsConfigClient>(client =>
    client.BaseAddress = runtimeOptions.ProjectsConfigBaseAddress)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient<AcceptedEffectRelay>()
    .RedactLoggedHeaders(
        [
            "X-Agentweaver-Events-Authorization",
            "X-Agentweaver-Knowledge-Authorization"
        ])
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddScoped<KnowledgeProviderBindingService>();
builder.Services.AddSingleton<MemoryContextCompiler>();
builder.Services.AddScoped<KnowledgeApplicationService>();
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(authentication =>
    {
        authentication.Authority = runtimeOptions.IdentityAuthority.AbsoluteUri;
        authentication.Audience = runtimeOptions.Audience;
        authentication.RequireHttpsMetadata = true;
        authentication.MapInboundClaims = false;
    });
builder.Services.AddAuthorization();
builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow;
    json.SerializerOptions.MaxDepth = 16;
    json.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(
        System.Text.Json.JsonNamingPolicy.CamelCase));
});

var app = builder.Build();
await KnowledgeMigrator.VerifyAsync(dataSource, runtimeOptions.MemoryProvider.Schema).ConfigureAwait(false);
VerifyNativeProviderRegistration(
    providerCatalog,
    app.Services.GetRequiredService<NativePostgresMemoryProvider>(),
    runtimeOptions.MemoryProvider);
if (cosmosMemoryOptions is not null &&
    providerCatalog.TryGetProvider(CosmosMemoryProvider.ProviderId, out _))
    VerifyCosmosProviderRegistration(
        providerCatalog,
        app.Services.GetRequiredService<CosmosMemoryProvider>(),
        cosmosMemoryOptions);
if (redisMemoryOptions is not null &&
    providerCatalog.TryGetProvider(RedisMemoryProvider.ProviderId, out _))
    VerifyRedisProviderRegistration(
        providerCatalog,
        app.Services.GetRequiredService<RedisMemoryProvider>(),
        redisMemoryOptions);
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", async (CancellationToken cancellationToken) =>
{
    try
    {
        await KnowledgeMigrator.VerifyAsync(
            dataSource, runtimeOptions.MemoryProvider.Schema, cancellationToken).ConfigureAwait(false);
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
app.MapKnowledgeEndpoints();
app.Run();

static void VerifyNativeProviderRegistration(
    ProviderCatalog catalog,
    NativePostgresMemoryProvider provider,
    NativePostgresMemoryOptions options)
{
    if (!catalog.TryGetProvider(provider.Descriptor.Id, out var registration) ||
        registration is null ||
        registration.Descriptor.Seam != ProviderSeam.Memory ||
        registration.Descriptor.AdapterVersion != provider.Descriptor.AdapterVersion ||
        registration.Descriptor.OptionsSchemaVersion != options.OptionsSchemaVersion ||
        registration.OptionsSchemaVersion != options.OptionsSchemaVersion ||
        !string.Equals(registration.OptionsRevision, options.OptionsRevision, StringComparison.Ordinal) ||
        !registration.Descriptor.AdvertisedCapabilities.SetEquals(
            provider.Descriptor.AdvertisedCapabilities))
        throw new InvalidOperationException(
            "The provider catalog owner must register the exact native PostgreSQL Memory descriptor and options revision.");
}

static void VerifyCosmosProviderRegistration(
    ProviderCatalog catalog,
    CosmosMemoryProvider provider,
    CosmosMemoryOptions options)
{
    if (!catalog.TryGetProvider(provider.Descriptor.Id, out var registration) ||
        registration is null ||
        registration.Descriptor.Seam != ProviderSeam.Memory ||
        registration.Descriptor.AdapterVersion != provider.Descriptor.AdapterVersion ||
        registration.Descriptor.OptionsSchemaVersion != options.OptionsSchemaVersion ||
        registration.OptionsSchemaVersion != options.OptionsSchemaVersion ||
        !string.Equals(registration.OptionsRevision, options.OptionsRevision, StringComparison.Ordinal) ||
        !registration.Descriptor.AdvertisedCapabilities.SetEquals(provider.Descriptor.AdvertisedCapabilities))
        throw new InvalidOperationException(
            "The provider catalog owner must register the exact Cosmos Memory descriptor and options revision.");
}

static void VerifyRedisProviderRegistration(
    ProviderCatalog catalog,
    RedisMemoryProvider provider,
    RedisMemoryOptions options)
{
    if (!catalog.TryGetProvider(provider.Descriptor.Id, out var registration) ||
        registration is null ||
        registration.Descriptor.Seam != ProviderSeam.Memory ||
        registration.Descriptor.AdapterVersion != provider.Descriptor.AdapterVersion ||
        registration.Descriptor.OptionsSchemaVersion != options.OptionsSchemaVersion ||
        registration.OptionsSchemaVersion != options.OptionsSchemaVersion ||
        !string.Equals(registration.OptionsRevision, options.OptionsRevision, StringComparison.Ordinal) ||
        !registration.Descriptor.AdvertisedCapabilities.SetEquals(provider.Descriptor.AdvertisedCapabilities))
        throw new InvalidOperationException(
            "The provider catalog owner must register the exact Redis Memory descriptor and options revision.");
}

public partial class Program;
