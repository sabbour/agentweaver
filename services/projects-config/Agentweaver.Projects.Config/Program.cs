extern alias AzureIdentity;

using Agentweaver.Providers;
using Agentweaver.Projects.Config;
using Agentweaver.Abstractions;
using Agentweaver.ObjectStore.AzureBlob;
using Azure.Core;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using WorkloadIdentityCredential = AzureIdentity::Azure.Identity.WorkloadIdentityCredential;

// Composes the host and keeps migration, privileged setup, and first-admin bootstrap on separate command paths.
// Runtime database access uses workload identity and verifies the applied schema and read-only authority boundary.
var runMigrations = args.Length == 1 && args[0] == "--migrate";
if (args.Contains("--migrate", StringComparer.Ordinal) && !runMigrations)
    throw new ArgumentException("The --migrate command must be used by itself.", nameof(args));
var runPrivilegedProjectSetup = args.Length == 1 && args[0] == "--privileged-project-setup";
if (args.Contains("--privileged-project-setup", StringComparer.Ordinal) && !runPrivilegedProjectSetup)
    throw new ArgumentException(
        "The --privileged-project-setup command must be used by itself.", nameof(args));
var runFirstTenantAdminBootstrap = args.Length == 1 && args[0] == "--bootstrap-first-tenant-admin";
if (args.Contains("--bootstrap-first-tenant-admin", StringComparer.Ordinal) && !runFirstTenantAdminBootstrap)
    throw new ArgumentException(
        "The --bootstrap-first-tenant-admin command must be used by itself.", nameof(args));

var builder = WebApplication.CreateBuilder(
    runMigrations || runPrivilegedProjectSetup || runFirstTenantAdminBootstrap ? [] : args);
if (runPrivilegedProjectSetup)
{
    // This one-shot path reports only a stable error code; token and exception details never reach console output.
    try
    {
        await PrivilegedProjectSetupCommand.RunAsync(builder.Configuration);
    }
    catch (ProjectConfigException exception)
    {
        await Console.Error.WriteLineAsync($"Privileged project setup failed ({exception.Code}).");
        Environment.ExitCode = 1;
    }
    catch (Exception)
    {
        await Console.Error.WriteLineAsync("Privileged project setup failed; no credential or exception detail was written.");
        Environment.ExitCode = 1;
    }
    return;
}

if (runFirstTenantAdminBootstrap)
{
    try
    {
        var created = await TenantAdminBootstrapCommand.RunAsync(builder.Configuration);
        await Console.Out.WriteLineAsync(created
            ? "The first TenantAdmin was created."
            : "The approved TenantAdmin already exists; no changes were made.");
    }
    catch (TenantAdminBootstrapConflictException)
    {
        await Console.Error.WriteLineAsync(
            "First TenantAdmin bootstrap refused because another active TenantAdmin exists for this tenant.");
        Environment.ExitCode = 1;
    }
    catch (Exception)
    {
        await Console.Error.WriteLineAsync(
            "First TenantAdmin bootstrap failed. Check the approved identity, migration settings, and database connectivity.");
        Environment.ExitCode = 1;
    }
    return;
}

if (runMigrations)
{
    await using var migrationDataSource =
        ProjectsConfigPostgresDataSource.CreateMigrationDataSource(builder.Configuration);
    var migrationDbOptions = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
        .UseNpgsql(
            migrationDataSource,
            npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history", ProjectsConfigDbContext.Schema))
        .Options;
    await ProjectsConfigMigrator.MigrateAsync(migrationDataSource, migrationDbOptions);
    return;
}

var runtimeConnectionString = builder.Configuration.GetConnectionString("ProjectsConfig")
    ?? throw new InvalidOperationException("Missing required configuration 'ConnectionStrings:ProjectsConfig'.");
var runtimeIdentity = ProjectsConfigPostgresDataSource.ReadWorkloadIdentityOptions(
    builder.Configuration, "ProjectsConfig:Database:WorkloadIdentity");
builder.Services.AddSingleton<TokenCredential>(_ => new WorkloadIdentityCredential(runtimeIdentity));
builder.Services.AddSingleton<NpgsqlDataSource>(provider =>
    ProjectsConfigPostgresDataSource.Create(
        runtimeConnectionString, provider.GetRequiredService<TokenCredential>()));
builder.Services.AddDbContextFactory<ProjectsConfigDbContext>((provider, options) =>
{
    options.UseNpgsql(
        provider.GetRequiredService<NpgsqlDataSource>(),
        npgsql => npgsql.MigrationsHistoryTable(
            "__ef_migrations_history", ProjectsConfigDbContext.Schema));
});
builder.Services.AddScoped<ProjectsConfigService>();
builder.Services.AddScoped<MarketplaceSourceStore>();
builder.Services.AddScoped<IProjectMarketplaceSourceStore>(
    provider => provider.GetRequiredService<MarketplaceSourceStore>());
builder.Services.AddScoped<ProjectMarketplaceSourceService>();
builder.Services.AddScoped<ProjectMarketplaceSkillContentService>();
builder.Services.AddHttpClient<SkillMarketplaceBrowseService>();
var skillContentContainer = builder.Configuration["ProjectsConfig:SkillContent:ContainerUri"];
if (skillContentContainer is not null)
{
    // Skill blobs are optional, but when configured the store must target one explicit HTTPS container.
    if (!Uri.TryCreate(skillContentContainer, UriKind.Absolute, out var skillContentContainerUri) ||
        skillContentContainerUri.Scheme != Uri.UriSchemeHttps ||
        !string.IsNullOrEmpty(skillContentContainerUri.UserInfo) ||
        !string.IsNullOrEmpty(skillContentContainerUri.Query) ||
        !string.IsNullOrEmpty(skillContentContainerUri.Fragment) ||
        skillContentContainerUri.AbsolutePath.Trim('/').Contains('/') ||
        string.IsNullOrWhiteSpace(skillContentContainerUri.AbsolutePath.Trim('/')))
        throw new InvalidOperationException(
            "Skill content requires an explicitly configured HTTPS Blob container.");

    builder.Services.AddSingleton<IObjectStore>(services => new AzureBlobObjectStore(
        new BlobContainerClient(
            skillContentContainerUri,
            services.GetRequiredService<TokenCredential>())));
    builder.Services.AddScoped<SkillContentObjectStore>();
}
builder.Services.AddScoped<SkillContentService>(services => new SkillContentService(
    services.GetRequiredService<ProjectsConfigDbContext>(),
    services.GetService<SkillContentObjectStore>(),
    services.GetRequiredService<ProjectsConfigService>(),
    services.GetRequiredService<TimeProvider>()));
builder.Services.AddScoped<ISkillContentService>(services => services.GetRequiredService<SkillContentService>());
builder.Services.AddScoped<ISkillAssignmentService>(services => services.GetRequiredService<SkillContentService>());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(provider =>
    ProviderCatalogConfiguration.Load(provider.GetRequiredService<IConfiguration>()));

var authentication = builder.Configuration.GetSection("ProjectsConfig:Authentication");
var authority = authentication["Authority"];
var audience = authentication["Audience"];
if (!Uri.TryCreate(authority, UriKind.Absolute, out var authorityUri) ||
    authorityUri.Scheme != Uri.UriSchemeHttps ||
    string.IsNullOrWhiteSpace(audience))
    throw new InvalidOperationException(
        "Projects & Config authentication requires an HTTPS authority and an explicit audience.");

var brokerRunBindingAudience = authentication["BrokerRunBindingAudience"];
Uri? brokerRunBindingAudienceUri = null;
if (brokerRunBindingAudience is not null)
{
    if (!Uri.TryCreate(brokerRunBindingAudience, UriKind.Absolute, out var parsedBrokerAudience) ||
        parsedBrokerAudience is null ||
        parsedBrokerAudience.Scheme != Uri.UriSchemeHttps ||
        string.IsNullOrEmpty(parsedBrokerAudience.Host) ||
        !string.IsNullOrEmpty(parsedBrokerAudience.UserInfo) ||
        !string.IsNullOrEmpty(parsedBrokerAudience.Query) ||
        !string.IsNullOrEmpty(parsedBrokerAudience.Fragment) ||
        string.Equals(parsedBrokerAudience.AbsoluteUri, audience, StringComparison.Ordinal) ||
        Uri.TryCreate(audience, UriKind.Absolute, out var publicAudience) &&
        string.Equals(
            parsedBrokerAudience.AbsoluteUri,
            publicAudience.AbsoluteUri,
            StringComparison.Ordinal))
        throw new InvalidOperationException(
            "The Broker run-binding audience must be a distinct, absolute HTTPS audience.");
    brokerRunBindingAudienceUri = parsedBrokerAudience;
}
BrokerRunBindingTlsIdentity.Configure(builder, brokerRunBindingAudienceUri is not null);

builder.Services.AddSingleton(new ProjectsConfigIdentityOptions(authorityUri.AbsoluteUri));
builder.Services.AddScoped<ProjectAuthorizationOwner>();
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = authorityUri.AbsoluteUri;
        options.Audience = audience;
        options.RequireHttpsMetadata = true;
        options.MapInboundClaims = false;
    });
if (brokerRunBindingAudienceUri is not null)
{
    builder.Services.AddAuthentication().AddJwtBearer(
        ProjectAuthorizationOwner.BrokerRunBindingAuthenticationScheme,
        options =>
        {
            options.Authority = authorityUri.AbsoluteUri;
            options.Audience = brokerRunBindingAudienceUri.AbsoluteUri;
            options.RequireHttpsMetadata = true;
            options.MapInboundClaims = false;
            options.TokenValidationParameters.ValidateIssuer = true;
            options.TokenValidationParameters.ValidIssuer = authorityUri.AbsoluteUri;
            options.TokenValidationParameters.ValidateAudience = true;
            options.TokenValidationParameters.ValidAudience = brokerRunBindingAudienceUri.AbsoluteUri;
            options.TokenValidationParameters.ValidateIssuerSigningKey = true;
            options.TokenValidationParameters.ValidateLifetime = true;
            options.TokenValidationParameters.RequireExpirationTime = true;
            options.TokenValidationParameters.RequireSignedTokens = true;
            options.TokenValidationParameters.ValidTypes =
                [ProjectAuthorizationOwner.BrokerRunBindingTokenType];
            options.TokenValidationParameters.ValidAlgorithms = [SecurityAlgorithms.RsaSha256];
            options.TokenValidationParameters.ClockSkew = TimeSpan.FromSeconds(5);
            options.Events = BrokerRunBindingTokenValidation.CreateEvents(TimeProvider.System);
        });
}
builder.Services.AddAuthorization(options =>
{
    if (brokerRunBindingAudienceUri is not null)
    {
        options.AddPolicy(
            ProjectAuthorizationOwner.BrokerRunBindingAuthorizationPolicy,
            policy => policy
                .AddAuthenticationSchemes(ProjectAuthorizationOwner.BrokerRunBindingAuthenticationScheme)
                .RequireAuthenticatedUser());
    }
});
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow;
    options.SerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
});

var app = builder.Build();
var dataSource = app.Services.GetRequiredService<NpgsqlDataSource>();
var dbOptions = app.Services.GetRequiredService<DbContextOptions<ProjectsConfigDbContext>>();
await ProjectsConfigMigrator.VerifyMigrationsAppliedAsync(dataSource, dbOptions);
await ProjectsConfigMigrator.VerifyRuntimeAuthorityReadOnlyAsync(dataSource);
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", async (ProjectsConfigDbContext db, CancellationToken cancellationToken) =>
    await db.Database.CanConnectAsync(cancellationToken)
        ? Results.Ok(new { status = "ready" })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
app.MapProjectConfigEndpoints(enableBrokerRunBinding: brokerRunBindingAudienceUri is not null);
app.MapSkillMarketplaceEndpoints();
app.MapSkillContentEndpoints();
app.Run();

public partial class Program;
