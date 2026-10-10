extern alias AzureIdentity;

using Agentweaver.Providers;
using Agentweaver.Projects.Config;
using Agentweaver.Abstractions;
using Agentweaver.ObjectStore.AzureBlob;
using Azure.Core;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WorkloadIdentityCredential = AzureIdentity::Azure.Identity.WorkloadIdentityCredential;

var runMigrations = args.Length == 1 && args[0] == "--migrate";
if (args.Contains("--migrate", StringComparer.Ordinal) && !runMigrations)
    throw new ArgumentException("The --migrate command must be used by itself.", nameof(args));

var builder = WebApplication.CreateBuilder(runMigrations ? [] : args);
if (runMigrations)
{
    var migrationConnectionString = builder.Configuration.GetConnectionString("ProjectsConfigMigration")
        ?? throw new InvalidOperationException(
            "Missing required configuration 'ConnectionStrings:ProjectsConfigMigration'.");
    var migrationIdentity = ProjectsConfigPostgresDataSource.ReadWorkloadIdentityOptions(
        builder.Configuration, "ProjectsConfig:Migration:WorkloadIdentity");
    var migrationCredential = new WorkloadIdentityCredential(migrationIdentity);
    await using var migrationDataSource = ProjectsConfigPostgresDataSource.Create(
        migrationConnectionString, migrationCredential);
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
var skillContentContainer = builder.Configuration["ProjectsConfig:SkillContent:ContainerUri"];
if (skillContentContainer is not null)
{
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
builder.Services.AddAuthorization();
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
app.MapProjectConfigEndpoints();
app.MapSkillContentEndpoints();
app.Run();

public partial class Program;
