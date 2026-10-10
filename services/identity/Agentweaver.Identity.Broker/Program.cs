extern alias AzureIdentity;

using System.ComponentModel.DataAnnotations;
using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using Azure.Core;
using Azure.Security.KeyVault.Secrets;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Identity.Broker;
using Agentweaver.SourceControl;
using Agentweaver.Secrets.AzureKeyVault;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Npgsql;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;
using WorkloadIdentityCredential = AzureIdentity::Azure.Identity.WorkloadIdentityCredential;
using WorkloadIdentityCredentialOptions = AzureIdentity::Azure.Identity.WorkloadIdentityCredentialOptions;

var runMigrations = IdentityBrokerMigrationCommand.IsRequested(args);
var runPostgresBootstrap = IdentityBrokerPostgresBootstrapCommand.IsRequested(args);
var verifyPostgresBootstrap = IdentityBrokerPostgresBootstrapCommand.IsVerifyRequested(args);
if (IdentityBrokerMigrationCommand.ContainsArgument(args) && !runMigrations)
    throw new ArgumentException("The --migrate command must be used by itself.", nameof(args));
if (IdentityBrokerPostgresBootstrapCommand.ContainsArgument(args) &&
    !runPostgresBootstrap && !verifyPostgresBootstrap)
    throw new ArgumentException("An identity PostgreSQL maintenance command must be used by itself.", nameof(args));
if ((runMigrations ? 1 : 0) + (runPostgresBootstrap ? 1 : 0) + (verifyPostgresBootstrap ? 1 : 0) > 1)
    throw new ArgumentException("Only one Identity broker maintenance command can run at a time.", nameof(args));

var builder = WebApplication.CreateBuilder(
    runMigrations || runPostgresBootstrap || verifyPostgresBootstrap
        ? Array.Empty<string>()
        : args);
if (runMigrations)
{
    using var cancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };
    Console.CancelKeyPress += cancelHandler;
    using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
    {
        context.Cancel = true;
        cancellation.Cancel();
    });
    try
    {
        await IdentityBrokerMigrationCommand.RunAsync(
            builder.Configuration, cancellationToken: cancellation.Token);
    }
    finally
    {
        Console.CancelKeyPress -= cancelHandler;
    }
    return;
}
if (runPostgresBootstrap)
{
    await IdentityBrokerPostgresBootstrapCommand.RunAsync(builder.Configuration);
    return;
}
if (verifyPostgresBootstrap)
{
    await IdentityBrokerPostgresBootstrapCommand.RunAsync(builder.Configuration, verifyOnly: true);
    return;
}

// Explicit, fail-closed configuration: every broker setting (issuer, signing material,
// external provider, client list) must be supplied by the host. There is no environment
// branching and no development fallback anywhere in this composition.
var identityOptions = builder.Configuration
    .GetSection(IdentityBrokerOptions.SectionName)
    .Get<IdentityBrokerOptions>()
    ?? throw new InvalidOperationException(
        $"Missing required configuration section '{IdentityBrokerOptions.SectionName}'.");

Validator.ValidateObject(identityOptions, new ValidationContext(identityOptions), validateAllProperties: true);
Validator.ValidateObject(identityOptions.Signing, new ValidationContext(identityOptions.Signing), validateAllProperties: true);
Validator.ValidateObject(identityOptions.ExternalProvider, new ValidationContext(identityOptions.ExternalProvider), validateAllProperties: true);
Validator.ValidateObject(identityOptions.SecretRedemption, new ValidationContext(identityOptions.SecretRedemption), validateAllProperties: true);
if (identityOptions.GitHubRepoApp is { } githubRepoAppOptions)
{
    Validator.ValidateObject(githubRepoAppOptions, new ValidationContext(githubRepoAppOptions), validateAllProperties: true);
    if (!Uri.TryCreate(githubRepoAppOptions.CallbackUri, UriKind.Absolute, out var repoAppCallbackUri) ||
        repoAppCallbackUri.Scheme != Uri.UriSchemeHttps ||
        repoAppCallbackUri.AbsolutePath != "/auth/github/repo-app/callback" ||
        !string.IsNullOrEmpty(repoAppCallbackUri.UserInfo) ||
        !string.IsNullOrEmpty(repoAppCallbackUri.Query) ||
        !string.IsNullOrEmpty(repoAppCallbackUri.Fragment) ||
        string.IsNullOrWhiteSpace(githubRepoAppOptions.AppSlug) ||
        githubRepoAppOptions.AppSlug.Length > 100 ||
        githubRepoAppOptions.AppSlug.Any(character =>
            !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        throw new InvalidOperationException("GitHub Repo App configuration is invalid.");
    _ = new SecretRef(
        githubRepoAppOptions.PrivateKeySecretId,
        githubRepoAppOptions.PrivateKeySecretVersion);
}
foreach (var client in identityOptions.Clients)
    Validator.ValidateObject(client, new ValidationContext(client), validateAllProperties: true);
foreach (var uri in new[] { identityOptions.Issuer, identityOptions.ExternalProvider.Authority })
    if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps)
        throw new InvalidOperationException("Identity issuer and external authority must be absolute HTTPS URIs.");
if (!Uri.TryCreate(identityOptions.SecretRedemption.Audience, UriKind.Absolute, out var redemptionAudience) ||
    redemptionAudience.Scheme != Uri.UriSchemeHttps ||
    !identityOptions.Clients.SelectMany(client => client.Resources)
        .Contains(identityOptions.SecretRedemption.Audience, StringComparer.Ordinal))
    throw new InvalidOperationException("Secret redemption requires an HTTPS audience registered on an Identity client.");
if (!Path.IsPathFullyQualified(identityOptions.SecretRedemption.WorkloadIdentityTokenFilePath))
    throw new InvalidOperationException("Secret redemption requires an absolute workload-identity token file path.");
// Validate the vault URI and workload-identity settings before opening the database or
// starting the host. The projected token file is read only when Key Vault is contacted.
var vaultConfiguration = new AzureKeyVaultConfiguration(new Uri(identityOptions.SecretRedemption.VaultUri));
var workloadIdentityOptions = new WorkloadIdentityCredentialOptions
{
    TenantId = identityOptions.SecretRedemption.WorkloadIdentityTenantId,
    ClientId = identityOptions.SecretRedemption.WorkloadIdentityClientId,
    TokenFilePath = identityOptions.SecretRedemption.WorkloadIdentityTokenFilePath,
};
var secretClientOptions = new SecretClientOptions();

builder.Services.AddSingleton(identityOptions);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter<ModelSourceMode>(System.Text.Json.JsonNamingPolicy.CamelCase));
    options.SerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter<RuntimeModelCredentialKind>(System.Text.Json.JsonNamingPolicy.CamelCase));
    options.SerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter<ProjectAuthorityResourceType>(System.Text.Json.JsonNamingPolicy.CamelCase));
    options.SerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter<CopilotConnectionState>(System.Text.Json.JsonNamingPolicy.CamelCase));
});

// OpenIddict's own server dispatcher logs the full extracted token/authorization request
// at Information level (ID6075/ID6064/etc.), including the PKCE code_verifier (it
// redacts "code" but not "code_verifier"). That value is short-lived and single-use, but
// it is still client secret material this broker must never write to logs. Pin this
// protocol and hosting categories above verbose request dumps. Database logging is also
// bounded so operator Trace settings cannot expose request or persisted credential material.
builder.Logging.AddFilter("OpenIddict", LogLevel.Warning);
builder.Logging.AddFilter("OpenIddict.Server.OpenIddictServerDispatcher", LogLevel.Warning);
builder.Logging.AddFilter("OpenIddict.Validation.OpenIddictValidationDispatcher", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Authentication", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectHandler", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.HttpLogging", LogLevel.None);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
builder.Logging.AddFilter("Npgsql", LogLevel.Warning);

var connectionString = builder.Configuration.GetConnectionString("IdentityBroker")
    ?? throw new InvalidOperationException("Missing required configuration 'ConnectionStrings:IdentityBroker'.");

builder.Services.AddSingleton<TokenCredential>(_ => new WorkloadIdentityCredential(workloadIdentityOptions));
builder.Services.AddSingleton<NpgsqlDataSource>(provider =>
    IdentityBrokerPostgresDataSource.Create(
        connectionString,
        provider.GetRequiredService<TokenCredential>()));

builder.Services.AddDbContext<IdentityBrokerDbContext>((provider, options) =>
{
    var dataSource = provider.GetRequiredService<NpgsqlDataSource>();
    options.UseNpgsql(dataSource, npgsql => npgsql.MigrationsHistoryTable(
        "__ef_migrations_history", IdentityBrokerDbContext.Schema));
    options.UseOpenIddict();
});

builder.Services.AddScoped<BrokerUserProvisioner>();
builder.Services.AddScoped<BrokerClientSeeder>();
builder.Services.AddScoped<BrokerScopeSeeder>();
builder.Services.AddScoped<RefreshTokenFamilyRevoker>();
builder.Services.AddScoped<IdentityGrantAuthority>();
builder.Services.AddScoped<IGrantAuthority>(provider => provider.GetRequiredService<IdentityGrantAuthority>());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(vaultConfiguration);
builder.Services.AddSingleton(workloadIdentityOptions);
builder.Services.AddSingleton(secretClientOptions);
builder.Services.AddSingleton<AzureKeyVaultSecretRedemption>(provider =>
    AzureKeyVaultSecretRedemption.CreateWithWorkloadIdentity(
        provider.GetRequiredService<AzureKeyVaultConfiguration>(),
        provider.GetRequiredService<WorkloadIdentityCredentialOptions>(),
        provider.GetRequiredService<SecretClientOptions>(),
        provider.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<ISecretRedemption>(provider =>
    provider.GetRequiredService<AzureKeyVaultSecretRedemption>());
builder.Services.AddSingleton<ISecretVersionWriter>(provider =>
    new AzureKeyVaultSecretVersionWriter(
        provider.GetRequiredService<AzureKeyVaultConfiguration>(),
        provider.GetRequiredService<TokenCredential>(),
        provider.GetRequiredService<SecretClientOptions>()));
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient("github-repo-app-oauth", client =>
    client.BaseAddress = new Uri("https://github.com/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient("github-repo-app-api", client =>
    client.BaseAddress = new Uri("https://api.github.com/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
if (identityOptions.GitHubRepoApp is { } repoAppOptions)
{
    builder.Services.AddSingleton(repoAppOptions);
    builder.Services.AddScoped(provider => new GitHubRepoAppProviderClient(
        provider.GetRequiredService<IHttpClientFactory>().CreateClient("github-repo-app-oauth"),
        provider.GetRequiredService<IHttpClientFactory>().CreateClient("github-repo-app-api"),
        new GitHubRepoAppProviderOptions(
            repoAppOptions.OAuthClientId,
            repoAppOptions.OAuthClientSecret,
            new Uri(repoAppOptions.CallbackUri)),
        provider.GetRequiredService<TimeProvider>()));
    builder.Services.AddHttpClient("github-repo-app-installation-api", client =>
        client.BaseAddress = new Uri("https://api.github.com/"))
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
    builder.Services.AddScoped(provider => new GitHubAppInstallationTokenIssuer(
        provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient("github-repo-app-installation-api"),
        repoAppOptions.AppId,
        provider.GetRequiredService<TimeProvider>()));
    builder.Services.AddScoped<GitHubRepoAppConnectionService>();
}
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
var runtimeBootstrapOptions = builder.Configuration
    .GetSection("IdentityBroker:RuntimeBootstrap").Get<RuntimeBootstrapOptions>();
if (runtimeBootstrapOptions is not null)
    builder.Services.AddIdentityRuntimeCredentials(runtimeBootstrapOptions, identityOptions.Issuer);
var copilotConnectionOptions = builder.Configuration
    .GetSection("IdentityBroker:CopilotConnection").Get<CopilotConnectionOptions>();
if (copilotConnectionOptions is not null)
{
    RuntimeOwnerHttpTransport.RequireOwnerAddress(copilotConnectionOptions.ProjectsOwnerAddress);
    if (string.IsNullOrWhiteSpace(copilotConnectionOptions.ClientId) ||
        !RuntimeContractValidation.IsHttpsEndpoint(copilotConnectionOptions.CallbackUri) ||
        copilotConnectionOptions.CallbackUri.AbsolutePath != CopilotConnectionEndpoints.BrowserCallbackPath ||
        copilotConnectionOptions.ClientSecretReference is null)
        throw new InvalidOperationException(
            "Copilot connections require explicit OAuth and protected-store configuration with the fixed browser return path.");
    builder.Services.AddSingleton(copilotConnectionOptions);
    builder.Services.AddHttpClient("CopilotConnectionProjects")
        .ConfigurePrimaryHttpMessageHandler(RuntimeOwnerHttpTransport.CreateHandler);
    builder.Services.AddHttpClient("CopilotConnectionGitHub")
        .ConfigurePrimaryHttpMessageHandler(RuntimeOwnerHttpTransport.CreateHandler);
    builder.Services.AddSingleton<ISecretVersionWriter>(provider => new AzureKeyVaultSecretVersionWriter(
        vaultConfiguration, provider.GetRequiredService<TokenCredential>()));
    builder.Services.AddScoped(provider => new CopilotConnectionAuthority(
        provider.GetRequiredService<IdentityBrokerDbContext>(), copilotConnectionOptions,
        provider.GetRequiredService<IHttpClientFactory>().CreateClient("CopilotConnectionProjects"),
        provider.GetRequiredService<IHttpClientFactory>().CreateClient("CopilotConnectionGitHub"),
        provider.GetRequiredService<ISecretRedemption>(), provider.GetRequiredService<ISecretVersionWriter>(),
        provider.GetRequiredService<IDataProtectionProvider>(), provider.GetRequiredService<TimeProvider>()));
}
if (identityOptions.RemoteMcpOAuth is { } remoteMcpOAuthOptions)
{
    Validator.ValidateObject(
        remoteMcpOAuthOptions, new ValidationContext(remoteMcpOAuthOptions), validateAllProperties: true);
    if (!Uri.TryCreate(remoteMcpOAuthOptions.ProjectsOwnerAddress, UriKind.Absolute, out var projectsOwnerAddress))
        throw new InvalidOperationException("Remote MCP management requires an absolute Projects owner address.");
    RuntimeOwnerHttpTransport.RequireOwnerAddress(projectsOwnerAddress);
    builder.Services.AddSingleton(remoteMcpOAuthOptions);
    builder.Services.AddHttpClient("RemoteMcpOAuthProjects")
        .ConfigurePrimaryHttpMessageHandler(RuntimeOwnerHttpTransport.CreateHandler);
    builder.Services.AddScoped(provider => new RemoteMcpOAuthManagementService(
        provider.GetRequiredService<IdentityBrokerDbContext>(),
        remoteMcpOAuthOptions,
        provider.GetRequiredService<IHttpClientFactory>().CreateClient("RemoteMcpOAuthProjects"),
        provider.GetRequiredService<TimeProvider>(),
        provider.GetRequiredService<ISecretVersionWriter>()));
}

// The broker's own signing/encryption credential. Production composition mounts a real
// PFX; there is no "development certificate" escape hatch in this host.
var signingCertificate = X509CertificateLoader.LoadPkcs12FromFile(
    identityOptions.Signing.PfxPath, identityOptions.Signing.PfxPassword, X509KeyStorageFlags.EphemeralKeySet);
builder.Services.AddDataProtection()
    .SetApplicationName("Agentweaver.Identity.Broker")
    .PersistKeysToFileSystem(new DirectoryInfo(identityOptions.DataProtectionKeyPath))
    .ProtectKeysWithCertificate(signingCertificate);

builder.Services
    .AddAuthentication(options => options.DefaultScheme = IdentityBrokerEndpoints.LocalCookieScheme)
    .AddCookie(IdentityBrokerEndpoints.LocalCookieScheme)
    .AddOpenIdConnect(IdentityBrokerEndpoints.ExternalScheme, options =>
    {
        options.SignInScheme = IdentityBrokerEndpoints.LocalCookieScheme;
        options.Authority = identityOptions.ExternalProvider.Authority;
        options.MetadataAddress = identityOptions.ExternalProvider.MetadataAddress;
        options.ClientId = identityOptions.ExternalProvider.ClientId;
        options.ClientSecret = identityOptions.ExternalProvider.ClientSecret;
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.UsePkce = true;
        options.SaveTokens = false;
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.Scope.Add("email");
        options.Events = new OpenIdConnectEvents
        {
            // This event runs after native protocol and nonce validation, unlike
            // OnTokenValidated. Replaces the external provider's subject with the local
            // BrokerUser identifier before anything is persisted to the local cookie, so
            // downstream code never has to trust (or re-validate) an upstream subject claim.
            OnTicketReceived = async context =>
            {
                var principal = context.Principal
                    ?? throw new InvalidOperationException("The external token response has no principal.");
                var subject = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
                    ?? throw new InvalidOperationException("The external identity provider did not return a subject claim.");
                var displayName = principal.FindFirst(ClaimTypes.Name)?.Value;
                var email = principal.FindFirst(ClaimTypes.Email)?.Value;

                var provisioner = context.HttpContext.RequestServices.GetRequiredService<BrokerUserProvisioner>();
                var user = await provisioner.ProvisionAsync(
                    identityOptions.ExternalProvider.Authority, subject, displayName, email,
                    context.HttpContext.RequestAborted);

                var identity = new ClaimsIdentity(IdentityBrokerEndpoints.LocalCookieScheme);
                identity.AddClaim(new Claim(Claims.Subject, user.Id.ToString()));
                var localPrincipal = new ClaimsPrincipal(identity);

                context.Principal = localPrincipal;
            },
            OnRemoteFailure = context =>
            {
                context.HandleResponse();
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            },
        };
    });

var registeredScopes = identityOptions.Clients
    .SelectMany(client => client.Scopes)
    .Distinct(StringComparer.Ordinal)
    .ToArray();

builder.Services.AddOpenIddict()
    .AddCore(options =>
    {
        options.UseEntityFrameworkCore().UseDbContext<IdentityBrokerDbContext>();
    })
    .AddServer(options =>
    {
        options.SetIssuer(new Uri(identityOptions.Issuer));
        options.AddSigningCertificate(signingCertificate);
        options.AddEncryptionCertificate(signingCertificate);

        options.SetAuthorizationEndpointUris("/connect/authorize");
        options.SetTokenEndpointUris("/connect/token");

        options.AllowAuthorizationCodeFlow();
        options.AllowRefreshTokenFlow();
        options.RegisterScopes(registeredScopes);

        // OpenIddict enables both "plain" and "S256" code challenge methods by default, and
        // grants a grace-period "reuse leeway" (default 30s) during which an already-redeemed
        // refresh token is still accepted (to tolerate legitimate concurrent requests). This
        // broker requires PKCE with S256 only (never "plain") and treats ANY reuse of a
        // redeemed refresh token as a replay to be rejected and family-revoked immediately
        // (see RefreshTokenHandlers.cs) — so the built-in leeway is disabled.
        options.Configure(serverOptions =>
        {
            serverOptions.CodeChallengeMethods.Remove(OpenIddictConstants.CodeChallengeMethods.Plain);
            serverOptions.RequireProofKeyForCodeExchange = true;
            serverOptions.RefreshTokenReuseLeeway = TimeSpan.Zero;
        });

        // Access tokens stay unencrypted signed JWTs so resource servers can validate them
        // locally; refresh tokens are opaque server-side references so replay/reuse can be
        // detected and the whole token family revoked (see RefreshTokenHandlers.cs).
        options.DisableAccessTokenEncryption();
        options.UseReferenceRefreshTokens();
        options.DisableSlidingRefreshTokenExpiration();

        options.AddEventHandler<OpenIddict.Server.OpenIddictServerEvents.ValidateTokenRequestContext>(handler =>
            handler.UseScopedHandler<RefreshReplayHandler>());
        options.AddEventHandler<OpenIddict.Server.OpenIddictServerEvents.ProcessSignInContext>(handler =>
            handler.UseScopedHandler<AtomicRefreshTokenRedemptionHandler>());

        options.UseAspNetCore()
            .EnableAuthorizationEndpointPassthrough()
            .EnableTokenEndpointPassthrough();
    })
    .AddValidation(options =>
    {
        options.UseLocalServer();
        options.AddAudiences(identityOptions.SecretRedemption.Audience);
        options.UseAspNetCore();
    });

builder.Services.AddAuthorization();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var dataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
    var dbOptions = scope.ServiceProvider.GetRequiredService<DbContextOptions<IdentityBrokerDbContext>>();
    await IdentityBrokerMigrator.VerifyMigrationsAppliedAsync(dataSource, dbOptions);

    await scope.ServiceProvider.GetRequiredService<BrokerScopeSeeder>().SeedAsync(CancellationToken.None);
    await scope.ServiceProvider.GetRequiredService<BrokerClientSeeder>().SeedAsync(CancellationToken.None);
}

app.UseAuthentication();
app.UseAuthorization();

app.MapIdentityBrokerEndpoints();
app.MapIdentitySecretRedemptionEndpoints();
app.MapCopilotConnectionEndpoints();
app.MapRemoteMcpOAuthManagementEndpoints();
if (identityOptions.GitHubRepoApp is not null)
{
    app.MapGitHubRepoAppEndpoints();
    app.MapGitHubRepoAppInstallationTokenEndpoints();
}
if (runtimeBootstrapOptions is not null)
    app.MapIdentityRuntimeCredentialEndpoints();

app.Run();

/// <summary>Exposed so WebApplicationFactory-based tests can bootstrap this host.</summary>
public partial class Program;
