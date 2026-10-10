extern alias AzureIdentity;

using System.Collections.Immutable;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using Agentweaver.Abstractions;
using Agentweaver.Environment;
using Agentweaver.Identity;
using Agentweaver.Providers.Storage.AzureFiles;
using Agentweaver.Providers.Sandbox.AgentSandbox;
using Azure.Core;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WorkloadIdentityCredential = AzureIdentity::Azure.Identity.WorkloadIdentityCredential;
using static Program;

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
var sandboxOptions = ReadSandboxOptions(builder.Configuration);

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
builder.Services.AddScoped<ISandboxLeaseStore, EnvironmentSandboxLeaseStore>();
builder.Services.AddScoped<IEnvironmentSandboxBuildTestCommandStore, EnvironmentSandboxBuildTestCommandStore>();
builder.Services.AddScoped<RemoteMcpConnectionStore>();
builder.Services.AddScoped<IEnvironmentLifecycleProducer, EnvironmentLifecycleProducer>();
builder.Services.AddScoped<EnvironmentRuntimePlacementReader>();
builder.Services.AddSingleton(new EnvironmentRuntimeBootstrapProfileRegistry(
    builder.Configuration.GetSection("Environment:RuntimeBootstrap:Profiles")
        .Get<EnvironmentRuntimeBootstrapProfileRegistration[]>() ?? []));
var runtimeOrchestratorAddress = builder.Configuration["Environment:RuntimeBootstrap:OrchestratorOwnerAddress"];
var runtimeBrokerAddress = builder.Configuration["Environment:RuntimeBootstrap:BrokerOwnerAddress"];
var runtimeBootstrapEnabled = runtimeOrchestratorAddress is not null || runtimeBrokerAddress is not null;
if (runtimeBootstrapEnabled)
{
    var runtimeDeliveryOptions = new EnvironmentRuntimeBootstrapDeliveryOptions(
        RuntimeOwnerHttpTransport.RequireOwnerAddress(new Uri(
            runtimeOrchestratorAddress ?? throw new InvalidOperationException(
                "Environment Runtime bootstrap requires an Orchestrator owner address."))),
        RuntimeOwnerHttpTransport.RequireOwnerAddress(new Uri(
            runtimeBrokerAddress ?? throw new InvalidOperationException(
                "Environment Runtime bootstrap requires a Broker owner address."))));
    builder.Services.AddSingleton(runtimeDeliveryOptions);
    builder.Services.AddHttpClient<EnvironmentRuntimeBootstrapDelivery>(client =>
        client.Timeout = TimeSpan.FromSeconds(20))
        .ConfigurePrimaryHttpMessageHandler(RuntimeOwnerHttpTransport.CreateHandler);
}
builder.Services.AddSingleton(ciliumOptions);
builder.Services.AddHttpClient<EnvironmentRuntimeOwnerContextClient>(client => client.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(RuntimeOwnerHttpTransport.CreateHandler);
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient<
        ISandboxBuildTestAcceptedCommandVerifier,
        EnvironmentSandboxBuildTestAcceptedCommandVerifier>(client => client.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(RuntimeOwnerHttpTransport.CreateHandler);
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
builder.Services.AddSingleton(sandboxOptions);
builder.Services.AddHttpClient<KubernetesAgentSandboxClient>(client =>
{
    client.BaseAddress = kubernetesBaseAddress;
    client.Timeout = TimeSpan.FromSeconds(sandboxOptions.ReconciliationTimeoutSeconds);
}).ConfigurePrimaryHttpMessageHandler(() =>
    KubernetesServiceAccountHandler.Create(serviceAccountToken, serviceAccountCa));
builder.Services.AddScoped<ISandboxProvider, AgentSandboxProvider>();
builder.Services.AddScoped<ISandboxBuildTestCommandProvider>(services =>
    (ISandboxBuildTestCommandProvider)services.GetRequiredService<ISandboxProvider>());
builder.Services.AddScoped<EnvironmentSandboxManager>();
builder.Services.AddScoped<EnvironmentSandboxBuildTestCommandManager>();
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
if (runtimeBootstrapEnabled)
    app.MapEnvironmentRuntimeBootstrap();
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
app.MapRemoteMcpConnectionEndpoints();
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

public partial class Program
{
    public static AgentSandboxOptions ReadSandboxOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection("Environment:Sandbox:AgentSandbox");
        return new AgentSandboxOptions(
            section.GetValue("OptionsSchemaVersion", 0),
            Required(section["OptionsRevision"], "Environment:Sandbox:AgentSandbox:OptionsRevision"),
            Required(section["Namespace"], "Environment:Sandbox:AgentSandbox:Namespace"),
            Required(section["WorkspaceStorageProviderId"], "Environment:Sandbox:AgentSandbox:WorkspaceStorageProviderId"),
            Required(section["ContainerImage"], "Environment:Sandbox:AgentSandbox:ContainerImage"),
            Required(section["ContainerImagePlatform"], "Environment:Sandbox:AgentSandbox:ContainerImagePlatform"),
            section.GetValue("ContainerImageCompressedPullBytes", 0L),
            Required(section["RuntimeClassName"], "Environment:Sandbox:AgentSandbox:RuntimeClassName"),
            Required(section["ExpectedRuntimeHandler"], "Environment:Sandbox:AgentSandbox:ExpectedRuntimeHandler"),
            Required(section["CpuRequest"], "Environment:Sandbox:AgentSandbox:CpuRequest"),
            Required(section["MemoryRequest"], "Environment:Sandbox:AgentSandbox:MemoryRequest"),
            section.GetValue("ReconciliationTimeoutSeconds", 0),
            section.GetValue("PollIntervalMilliseconds", 0),
            new AgentSandboxStartupBudgets(
                section.GetValue("StartupBudgets:ScheduledSeconds", 0),
                section.GetValue("StartupBudgets:ImageReadySeconds", 0),
                section.GetValue("StartupBudgets:StartedSeconds", 0),
                section.GetValue("StartupBudgets:ConfiguredSeconds", 0),
                section.GetValue("StartupBudgets:ReadySeconds", 0),
                section.GetValue("StartupBudgets:TotalSeconds", 0)))
        {
            AgentHost = section.GetSection("AgentHost")
                .Get<AgentSandboxOptions.AgentSandboxAgentHostProfile>(),
            AcceptedBuildTestProfile = ReadBuildTestProfile(section.GetSection("AcceptedBuildTestProfile"))
        }.Validate();
    }

    private static SandboxBuildTestAcceptedExecutionOptions? ReadBuildTestProfile(IConfigurationSection section)
    {
        if (!section.Exists())
            return null;
        var profile = section.Get<SandboxBuildTestAcceptedExecutionOptions>()
            ?? throw new InvalidOperationException("An accepted BuildTest profile is required.");
        // ConfigurationBinder does not populate ImmutableArray record parameters.
        return profile with
        {
            AllowedExecutables = (section.GetSection("AllowedExecutables").Get<string[]>()
                ?? throw new InvalidOperationException("The accepted BuildTest executable allowlist is required."))
                .ToImmutableArray(),
            CollectorAssemblyArguments = (section.GetSection("CollectorAssemblyArguments").Get<string[]>()
                ?? throw new InvalidOperationException("The accepted BuildTest collector assembly arguments are required."))
                .ToImmutableArray()
        };
    }

    internal static string Required(string? value, string name) =>
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Missing required configuration '{name}'.");
}
