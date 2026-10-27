extern alias AzureIdentity;

using System.Collections.Immutable;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Agentweaver.Abstractions;
using Agentweaver.Environment;
using Agentweaver.Identity;
using Agentweaver.Persistence.Postgres;
using Agentweaver.Providers;
using Agentweaver.Providers.Storage.AzureFiles;
using Agentweaver.Providers.Sandbox.AgentSandbox;
using Azure.Core;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WorkloadIdentityCredential = AzureIdentity::Azure.Identity.WorkloadIdentityCredential;
using static Program;

// Starts the Environment API, or runs the explicitly requested database migration path before host startup.
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
        var supportedVersions = EnvironmentMigrator.ValidateMigrationCatalog(migrationOptions);
        var runtimeRole = builder.Configuration["Environment:Migration:RuntimeRole"];
        // The runner supplies a reviewed role and hash; validate both before any migration connection is used.
        var plannedGrants = string.IsNullOrWhiteSpace(runtimeRole)
            ? null
            : PlannedRuntimeGrants.Load(
                "environment",
                EnvironmentDbContext.Schema,
                runtimeRole,
                builder.Configuration["Environment:Migration:PlanSha256"] ?? "",
                supportedVersions);
        await EnvironmentMigrator.MigrateAsync(migrationDataSource, migrationOptions);
        if (plannedGrants is not null)
        {
            await using var grantConnection = await migrationDataSource.OpenConnectionAsync();
            await PlannedRuntimeGrants.ApplyAndVerifyAsync(grantConnection, plannedGrants);
            var applied = await EnvironmentMigrator.ReadAppliedVersionsAsync(migrationOptions);
            Console.WriteLine(PlannedRuntimeGrants.FormatMarker(plannedGrants, applied));
        }
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
builder.Services.AddScoped<IEnvironmentProviderLifecycleReportStore, EnvironmentProviderLifecycleReportStore>();
builder.Services.AddScoped<ISandboxLeaseStore, EnvironmentSandboxLeaseStore>();
builder.Services.AddScoped<
    IEnvironmentRuntimeBootstrapProfileReplacementStore,
    EnvironmentRuntimeBootstrapProfileReplacementStore>();
builder.Services.AddScoped<IEnvironmentSandboxBuildTestCommandStore, EnvironmentSandboxBuildTestCommandStore>();
builder.Services.AddScoped<RemoteMcpConnectionStore>();
// The trusted transport registers the production discovery source; without it discovery fails with 503.
Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions
    .TryAddSingleton<IRemoteMcpDiscoverySource, UnavailableRemoteMcpDiscoverySource>(builder.Services);
builder.Services.AddScoped<RemoteMcpDiscoveryService>();
builder.AddRemoteMcpTrustedServiceTls();
var remoteMcpCurrentCallVerificationEnabled =
    builder.AddRemoteMcpCurrentCallVerification(authorityUri.AbsoluteUri);
var remoteMcpNativeCallDispatchEnabled = builder.ConfigureRemoteMcpNativeCallDispatch();
builder.Services.AddScoped<IEnvironmentLifecycleProducer, EnvironmentLifecycleProducer>();
builder.Services.AddScoped<EnvironmentLifecycleManager>();
builder.Services.AddScoped<EnvironmentRuntimePlacementReader>();
builder.Services.AddScoped<EnvironmentP1CreationReader>();
builder.Services.AddSingleton(new EnvironmentRuntimeBootstrapProfileRegistry(
    builder.Configuration.GetSection("Environment:RuntimeBootstrap:Profiles")
        .Get<EnvironmentRuntimeBootstrapProfileRegistration[]>() ?? []));
// The direct-Pod transport keeps the TLS hostname while connecting to the validated Pod IP.
builder.Services.AddHttpClient<EnvironmentRuntimeEndpointVerifier>(client =>
    client.Timeout = TimeSpan.FromSeconds(10))
    .ConfigurePrimaryHttpMessageHandler(RuntimeSandboxHttpTransport.CreateHandler);
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
                "Environment Runtime bootstrap requires a Broker owner address."))))
    {
        EventsOwnerAddress = builder.Configuration["Environment:RuntimeBootstrap:EventsOwnerAddress"] is { } eventsAddress
            ? RuntimeOwnerHttpTransport.RequireOwnerAddress(new Uri(eventsAddress))
            : null
    };
    builder.Services.AddSingleton(runtimeDeliveryOptions);
    builder.Services.AddScoped<IEnvironmentRuntimeResumeBootstrapStore, EnvironmentRuntimeResumeBootstrapStore>();
    // Bootstrap calls share backend-CA validation with direct-Pod sandbox traffic.
    builder.Services.AddHttpClient<EnvironmentRuntimeBootstrapDelivery>(client =>
        client.Timeout = TimeSpan.FromSeconds(20))
        .ConfigurePrimaryHttpMessageHandler(RuntimeSandboxHttpTransport.CreateHandler);
    builder.Services.AddScoped<EnvironmentRuntimeWorkspaceResumeManager>();
    builder.Services.AddHttpClient<EnvironmentRuntimeSuspendReleaseAuthority>(client =>
        client.Timeout = TimeSpan.FromSeconds(15))
        .ConfigurePrimaryHttpMessageHandler(RuntimeOwnerHttpTransport.CreateTrustedOwnerHandler);
}
builder.Services.AddSingleton(ciliumOptions);
// Owner API clients use the mounted backend CA; public issuer discovery keeps system trust.
builder.Services.AddHttpClient<EnvironmentRuntimeOwnerContextClient>(client => client.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(RuntimeOwnerHttpTransport.CreateTrustedOwnerHandler);
builder.Services.AddScoped<IEnvironmentP1InvocationBindingReader>(services =>
    services.GetRequiredService<EnvironmentRuntimeOwnerContextClient>());
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient<
        ISandboxBuildTestAcceptedCommandVerifier,
        EnvironmentSandboxBuildTestAcceptedCommandVerifier>(client => client.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(RuntimeOwnerHttpTransport.CreateTrustedOwnerHandler);
builder.Services.AddHttpClient<IProjectsConfigClient, ProjectsConfigHttpClient>(client =>
{
    client.BaseAddress = projectsBaseAddress;
    client.Timeout = TimeSpan.FromSeconds(15);
}).ConfigurePrimaryHttpMessageHandler(RuntimeOwnerHttpTransport.CreateTrustedOwnerHandler);
builder.Services.AddHttpClient<KubernetesCiliumPolicyResourceStore>(client =>
{
    client.BaseAddress = kubernetesBaseAddress;
    client.Timeout = TimeSpan.FromSeconds(20);
}).ConfigurePrimaryHttpMessageHandler(services =>
    KubernetesServiceAccountHandler.Create(
        serviceAccountToken,
        services.GetRequiredService<byte[]>()));
builder.Services.AddScoped<ICiliumPolicyResourceStore>(services =>
    services.GetRequiredService<KubernetesCiliumPolicyResourceStore>());
builder.Services.AddScoped<CiliumEgressPolicyAdapter>();
builder.Services.AddScoped<EnvironmentEgressManager>();
builder.Services.AddSingleton(sandboxOptions);
builder.Services.AddHttpClient<KubernetesAgentSandboxClient>(client =>
{
    client.BaseAddress = kubernetesBaseAddress;
    client.Timeout = TimeSpan.FromSeconds(sandboxOptions.ReconciliationTimeoutSeconds);
}).ConfigurePrimaryHttpMessageHandler(services =>
    KubernetesServiceAccountHandler.Create(
        serviceAccountToken,
        services.GetRequiredService<byte[]>()));
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
var platformComponentAuthentication =
    PlatformComponentServiceAuthenticationOptions.ReadOptional(builder.Configuration, audience);
builder.Services.AddPlatformComponentServiceAuthentication(platformComponentAuthentication);
// The report catalog mirrors the registrations this owner already validates for Sandbox,
// Storage, and the L3/L4 Network Policy layer. It is used only for platform component reports.
var platformComponentCatalogResult = ProviderCatalog.Create(
    [
        AgentSandboxProviderMetadata.CreateRegistration(sandboxOptions),
        AzureFilesCsiProviderMetadata.CreateRegistration(azureFilesOptions),
        ciliumOptions.CreateRegistration(),
    ],
    [
        new ProviderSelection(ProviderSeam.Sandbox, AgentSandboxProviderMetadata.ProviderId),
        new ProviderSelection(ProviderSeam.Storage, AzureFilesCsiProviderMetadata.ProviderId),
    ],
    [],
    layerSelections: [new ProviderLayerSelection(NetworkPolicyLayer.L3L4, CiliumEgressPolicyAdapter.ProviderId)]);
if (!platformComponentCatalogResult.IsSuccess)
    throw new InvalidOperationException("The Environment platform component catalog is invalid.");
var platformComponentCatalog = platformComponentCatalogResult.Value!;
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
// Snapshots have no selected implementation, and Application Hosting has none in this release.
app.MapPlatformComponentOwner(platformComponentAuthentication, (services, ct) =>
    PlatformComponentCatalogReporter.CreateOwnerReportAsync(
        PlatformComponentOwners.Environment,
        async token =>
        {
            await EnvironmentMigrator.VerifyMigrationsAppliedAsync(dataSource, dbOptions, token);
            await EnvironmentMigrator.VerifyRuntimeAuthorityAsync(dataSource, token);
            return true;
        },
        (readiness, observedAt) =>
        [
            PlatformComponentCatalogReporter.DescribeArea(platformComponentCatalog, ProviderSeam.Snapshots, readiness, observedAt),
            PlatformComponentCatalogReporter.DescribeArea(platformComponentCatalog, ProviderSeam.Sandbox, readiness, observedAt),
            PlatformComponentCatalogReporter.DescribeArea(platformComponentCatalog, ProviderSeam.Storage, readiness, observedAt),
            PlatformComponentCatalogReporter.DescribeArea(platformComponentCatalog, ProviderSeam.NetworkPolicy, readiness, observedAt),
            PlatformComponentReportBuilder.Empty(
                PlatformComponentAreaIds.ApplicationHosting,
                PlatformComponentReadiness.NotAvailable,
                PlatformComponentReasons.NotImplemented),
        ],
        services.GetRequiredService<TimeProvider>(),
        ct));
app.MapRemoteMcpConnectionEndpoints();
if (remoteMcpCurrentCallVerificationEnabled)
    app.MapRemoteMcpCurrentCallVerification();
if (remoteMcpNativeCallDispatchEnabled)
    app.MapRemoteMcpNativeCallDispatch();
app.MapProviderLifecycleReportEndpoints();
app.Run();

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

/// <summary>Signs Kubernetes calls and validates TLS against the CA snapshot used for cluster pins.</summary>
internal sealed class KubernetesServiceAccountHandler : DelegatingHandler
{
    private readonly string _tokenFile;

    /// <summary>Creates a handler that reads the projected service-account token from the specified file.</summary>
    internal KubernetesServiceAccountHandler(HttpMessageHandler innerHandler, string tokenFile)
        : base(innerHandler) =>
        _tokenFile = tokenFile;

    /// <summary>Creates a Kubernetes handler whose TLS trust matches the shared cluster-pin CA snapshot.</summary>
    /// <param name="tokenFile">Absolute path to the projected service-account token.</param>
    /// <param name="clusterCaCertificate">Projected CA bytes also used to derive provider cluster pins.</param>
    public static HttpMessageHandler Create(string tokenFile, byte[] clusterCaCertificate)
    {
        if (!Path.IsPathFullyQualified(tokenFile))
            throw new InvalidOperationException("The Kubernetes token path must be absolute.");
        if (!File.Exists(tokenFile))
            throw new InvalidOperationException("The projected Kubernetes service-account token is required.");
        ArgumentNullException.ThrowIfNull(clusterCaCertificate);
        if (clusterCaCertificate.Length == 0)
            throw new InvalidOperationException("The projected Kubernetes service-account CA is required.");
        var caSnapshot = clusterCaCertificate.ToArray();
        var handler = EnvironmentHttpTransport.CreateRedirectDisabledHandler();
        handler.ServerCertificateCustomValidationCallback = (_, certificate, chain, errors) =>
            ValidateClusterCertificate(certificate, chain, errors, caSnapshot);
        return new KubernetesServiceAccountHandler(
            handler,
            tokenFile);
    }

    /// <summary>Loads the projected bearer token for each request and rejects redirects to avoid credential forwarding.</summary>
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

    /// <summary>Validates the server name and chain against every root in the configured CA snapshot.</summary>
    private static bool ValidateClusterCertificate(
        X509Certificate? certificate,
        X509Chain? peerChain,
        System.Net.Security.SslPolicyErrors errors,
        byte[] clusterCaCertificate)
    {
        if (certificate is null ||
            errors.HasFlag(System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch))
            return false;

        var roots = new X509Certificate2Collection();
        try
        {
            roots.ImportFromPem(Encoding.UTF8.GetString(clusterCaCertificate));
            if (roots.Count == 0)
                return false;

            using var leaf = X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            foreach (var root in roots)
                chain.ChainPolicy.CustomTrustStore.Add(root);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            if (peerChain is not null)
            {
                foreach (var element in peerChain.ChainElements.Cast<X509ChainElement>().Skip(1))
                    chain.ChainPolicy.ExtraStore.Add(element.Certificate);
            }
            return chain.Build(leaf);
        }
        catch (CryptographicException)
        {
            return false;
        }
        finally
        {
            foreach (var root in roots)
                root.Dispose();
        }
    }
}

/// <summary>Provides test-visible configuration readers for the Environment service's Sandbox providers.</summary>
public partial class Program
{
    /// <summary>Reads Cilium settings and restores literal label keys split by the JSON configuration provider.</summary>
    public static CiliumEgressProviderOptions ReadCiliumOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection("Environment:NetworkPolicy:Cilium");
        var @namespace = Required(section["Namespace"], "Environment:NetworkPolicy:Cilium:Namespace");
        var versionText = Required(section["AdapterVersion"], "Environment:NetworkPolicy:Cilium:AdapterVersion");
        if (!Version.TryParse(versionText, out var version))
            throw new InvalidOperationException("The Cilium adapter version must be a valid semantic version.");
        var revision = Required(section["OptionsRevision"], "Environment:NetworkPolicy:Cilium:OptionsRevision");
        return new CiliumEgressProviderOptions(
            @namespace,
            version,
            section.GetValue("OptionsSchemaVersion", 0),
            revision,
            ReadCiliumSelectors(section.GetSection("EndpointSelectors"))).Validate();
    }

    /// <summary>Rebuilds Cilium selector labels from the configuration tree without losing colon prefixes.</summary>
    private static ImmutableDictionary<string, ImmutableDictionary<string, string>> ReadCiliumSelectors(
        IConfigurationSection section)
    {
        var selectors = ImmutableDictionary.CreateBuilder<string, ImmutableDictionary<string, string>>(
            StringComparer.Ordinal);
        foreach (var selector in section.GetChildren())
        {
            var labels = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            foreach (var label in selector.GetChildren())
                AddCiliumLabel(labels, label, label.Key);
            selectors.Add(selector.Key, labels.ToImmutable());
        }
        return selectors.ToImmutable();
    }

    /// <summary>Reassembles a label key from its configuration-path segments and preserves its value.</summary>
    private static void AddCiliumLabel(
        ImmutableDictionary<string, string>.Builder labels,
        IConfigurationSection section,
        string key)
    {
        if (section.Value is { } value)
        {
            labels.Add(key, value);
            return;
        }

        var children = section.GetChildren().ToArray();
        if (children.Length == 0)
        {
            // Preserve null leaf labels as empty so validation rejects them instead of widening the selector.
            labels.Add(key, string.Empty);
            return;
        }

        // IConfiguration treats colons in JSON property names as path separators.
        foreach (var child in children)
            AddCiliumLabel(labels, child, $"{key}:{child.Key}");
    }

    /// <summary>Reads and validates the complete AgentSandbox provider configuration, including startup budgets.</summary>
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

    /// <summary>Reads an optional accepted BuildTest profile and restores its immutable allowlist arrays.</summary>
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

    /// <summary>Returns a required configuration value or fails startup with the missing setting name.</summary>
    internal static string Required(string? value, string name) =>
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Missing required configuration '{name}'.");
}
