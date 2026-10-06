extern alias AzureIdentity;

using System.Collections.Immutable;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using Agentweaver.Abstractions;
using Agentweaver.Environment;
using Agentweaver.Providers.Storage.AzureFiles;
using Azure.Core;
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
    var migration = EnvironmentPostgresDataSource.ReadMigrationConnection(builder.Configuration);
    var migrationCredential = EnvironmentPostgresDataSource.CreateCredential(migration);
    try
    {
        await using var migrationDataSource = EnvironmentPostgresDataSource.Create(
            migration.ConnectionString,
            migrationCredential);
        var migrationOptions = new DbContextOptionsBuilder<EnvironmentDbContext>()
            .UseNpgsql(
                migrationDataSource,
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    EnvironmentDbContext.Schema))
            .Options;
        await EnvironmentMigrator.MigrateAsync(migrationDataSource, migrationOptions);
    }
    finally
    {
        if (migrationCredential is IDisposable disposable)
            disposable.Dispose();
    }
    return;
}

var runtime = EnvironmentPostgresDataSource.ReadRuntimeConnection(builder.Configuration);
var identity = builder.Configuration.GetSection("Environment:Authentication");
var authority = Required(identity["Authority"], "Environment:Authentication:Authority");
if (!Uri.TryCreate(authority, UriKind.Absolute, out var authorityUri) ||
    authorityUri.Scheme != Uri.UriSchemeHttps)
    throw new InvalidOperationException("Environment authentication authority must use HTTPS.");
var audience = Required(identity["Audience"], "Environment:Authentication:Audience");
var projectsBaseAddress = EnvironmentHttpTransport.RequireTrustedHttpsBaseUri(
    Required(builder.Configuration["ProjectsConfig:BaseAddress"], "ProjectsConfig:BaseAddress"),
    "ProjectsConfig:BaseAddress");
var kubernetesBaseAddress = EnvironmentHttpTransport.RequireTrustedHttpsBaseUri(
    builder.Configuration["Kubernetes:ApiServer"] ?? "https://kubernetes.default.svc/",
    "Kubernetes:ApiServer");
var serviceAccountToken = builder.Configuration["Kubernetes:ServiceAccountTokenFile"] ??
    "/var/run/secrets/kubernetes.io/serviceaccount/token";
var serviceAccountCa = builder.Configuration["Kubernetes:CertificateAuthorityFile"] ??
    "/var/run/secrets/kubernetes.io/serviceaccount/ca.crt";
var ciliumOptions = ReadCiliumOptions(builder.Configuration);
var azureFilesOptions = ReadAzureFilesOptions(builder.Configuration);

builder.Services.AddSingleton<TokenCredential>(_ =>
    EnvironmentPostgresDataSource.CreateCredential(runtime));
builder.Services.AddSingleton<NpgsqlDataSource>(services =>
    EnvironmentPostgresDataSource.Create(
        runtime.ConnectionString,
        services.GetRequiredService<TokenCredential>()));
builder.Services.AddDbContext<EnvironmentDbContext>((services, options) =>
{
    options.UseNpgsql(
        services.GetRequiredService<NpgsqlDataSource>(),
        npgsql => npgsql.MigrationsHistoryTable(
            "__ef_migrations_history",
            EnvironmentDbContext.Schema));
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IEnvironmentLifecycleStore, EnvironmentLifecycleStore>();
builder.Services.AddScoped<IEnvironmentLifecycleProducer, EnvironmentLifecycleProducer>();
builder.Services.AddSingleton(ciliumOptions);
builder.Services.AddHttpClient<IProjectsConfigClient, ProjectsConfigHttpClient>(client =>
{
    client.BaseAddress = projectsBaseAddress;
    client.Timeout = TimeSpan.FromSeconds(15);
}).ConfigurePrimaryHttpMessageHandler(EnvironmentHttpTransport.CreateRedirectDisabledHandler);
builder.Services.AddHttpClient<KubernetesCiliumPolicyResourceStore>(client =>
{
    client.BaseAddress = kubernetesBaseAddress;
    client.Timeout = TimeSpan.FromSeconds(20);
}).ConfigurePrimaryHttpMessageHandler(() =>
    KubernetesServiceAccountHandler.Create(serviceAccountToken, serviceAccountCa));
builder.Services.AddScoped<ICiliumPolicyResourceStore>(services =>
    services.GetRequiredService<KubernetesCiliumPolicyResourceStore>());
builder.Services.AddScoped<CiliumEgressPolicyAdapter>();
builder.Services.AddScoped<EnvironmentEgressManager>();
builder.Services.AddAgentweaverWorkspaceVolumeService(
    azureFilesOptions,
    kubernetesBaseAddress,
    serviceAccountToken,
    serviceAccountCa);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
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
    options.SerializerOptions.UnmappedMemberHandling =
        System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow;
    options.SerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter(
            System.Text.Json.JsonNamingPolicy.CamelCase));
});

var app = builder.Build();
var dataSource = app.Services.GetRequiredService<NpgsqlDataSource>();
var dbOptions = app.Services.GetRequiredService<DbContextOptions<EnvironmentDbContext>>();
await EnvironmentMigrator.VerifyMigrationsAppliedAsync(dataSource, dbOptions);
await EnvironmentMigrator.VerifyRuntimeAuthorityAsync(dataSource);
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", async (CancellationToken cancellationToken) =>
{
    try
    {
        await EnvironmentMigrator.VerifyMigrationsAppliedAsync(
            dataSource,
            dbOptions,
            cancellationToken);
        await EnvironmentMigrator.VerifyRuntimeAuthorityAsync(dataSource, cancellationToken);
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
app.MapEnvironmentEndpoints();
app.Run();

static CiliumEgressProviderOptions ReadCiliumOptions(IConfiguration configuration)
{
    var section = configuration.GetSection("Environment:NetworkPolicy:Cilium");
    var @namespace = Required(section["Namespace"], "Environment:NetworkPolicy:Cilium:Namespace");
    var versionText = Required(section["AdapterVersion"], "Environment:NetworkPolicy:Cilium:AdapterVersion");
    if (!Version.TryParse(versionText, out var version))
        throw new InvalidOperationException("The Cilium adapter version must be a valid semantic version.");
    var revision = Required(section["OptionsRevision"], "Environment:NetworkPolicy:Cilium:OptionsRevision");
    var selectors = section.GetSection("EndpointSelectors")
        .Get<Dictionary<string, Dictionary<string, string>>>()
        ?? throw new InvalidOperationException(
            "Environment:NetworkPolicy:Cilium:EndpointSelectors must be configured.");
    return new CiliumEgressProviderOptions(
        @namespace,
        version,
        section.GetValue("OptionsSchemaVersion", 0),
        revision,
        selectors.ToImmutableDictionary(
            pair => pair.Key,
            pair => pair.Value.ToImmutableDictionary(StringComparer.Ordinal),
            StringComparer.Ordinal)).Validate();
}

static AzureFilesCsiOptions ReadAzureFilesOptions(IConfiguration configuration)
{
    var section = configuration.GetSection("Environment:Storage:AzureFiles");
    return new AzureFilesCsiOptions(
        section.GetValue("OptionsSchemaVersion", 0),
        Required(section["OptionsRevision"], "Environment:Storage:AzureFiles:OptionsRevision"),
        Required(section["Namespace"], "Environment:Storage:AzureFiles:Namespace"),
        Required(section["StorageClassName"], "Environment:Storage:AzureFiles:StorageClassName"),
        section.GetValue("MaximumCapacityGiB", 0L),
        section.GetValue("ProvisioningTimeoutSeconds", 0),
        section.GetValue("PollIntervalMilliseconds", 0)).Validate();
}

static string Required(string? value, string name) =>
    !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new InvalidOperationException($"Missing required configuration '{name}'.");

internal sealed class KubernetesServiceAccountHandler : DelegatingHandler
{
    private readonly string _tokenFile;

    internal KubernetesServiceAccountHandler(HttpMessageHandler innerHandler, string tokenFile)
        : base(innerHandler) =>
        _tokenFile = tokenFile;

    public static HttpMessageHandler Create(string tokenFile, string caFile)
    {
        if (!Path.IsPathFullyQualified(tokenFile) || !Path.IsPathFullyQualified(caFile))
            throw new InvalidOperationException("Kubernetes token and CA paths must be absolute.");
        if (!File.Exists(tokenFile) || !File.Exists(caFile))
            throw new InvalidOperationException("The projected Kubernetes service-account token and CA are required.");
        var handler = EnvironmentHttpTransport.CreateRedirectDisabledHandler();
        handler.ServerCertificateCustomValidationCallback = (_, certificate, chain, errors) =>
            ValidateClusterCertificate(certificate, chain, errors, caFile);
        return new KubernetesServiceAccountHandler(
            handler,
            tokenFile);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var token = (await File.ReadAllTextAsync(_tokenFile, cancellationToken).ConfigureAwait(false)).Trim();
        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsWhiteSpace))
            throw new HttpRequestException("The projected Kubernetes service-account token is invalid.");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (EnvironmentHttpTransport.IsRedirect(response))
        {
            response.Dispose();
            throw new HttpRequestException("Kubernetes API redirects are not permitted for authenticated requests.");
        }
        return response;
    }

    private static bool ValidateClusterCertificate(
        X509Certificate? certificate,
        X509Chain? peerChain,
        System.Net.Security.SslPolicyErrors errors,
        string caFile)
    {
        if (certificate is null ||
            errors.HasFlag(System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch))
            return false;
        if (errors == System.Net.Security.SslPolicyErrors.None)
            return true;

        using var root = X509CertificateLoader.LoadCertificateFromFile(caFile);
        using var leaf = X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        if (peerChain is not null)
        {
            foreach (var element in peerChain.ChainElements.Cast<X509ChainElement>().Skip(1))
                chain.ChainPolicy.ExtraStore.Add(element.Certificate);
        }
        return chain.Build(leaf);
    }
}

public partial class Program;
