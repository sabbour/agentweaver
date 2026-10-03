using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Identity.Broker;
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

var builder = WebApplication.CreateBuilder(args);

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
    ?? throw new InvalidOperationException("Missing required connection string 'IdentityBroker'.");

builder.Services.AddSingleton(new NpgsqlDataSourceBuilder(connectionString).Build());

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
builder.Services.AddHttpContextAccessor();
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");

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
    await IdentityBrokerMigrator.MigrateAsync(dataSource, dbOptions);

    await scope.ServiceProvider.GetRequiredService<BrokerScopeSeeder>().SeedAsync(CancellationToken.None);
    await scope.ServiceProvider.GetRequiredService<BrokerClientSeeder>().SeedAsync(CancellationToken.None);
}

app.UseAuthentication();
app.UseAuthorization();

app.MapIdentityBrokerEndpoints();
app.MapIdentitySecretRedemptionEndpoints();

app.Run();

/// <summary>Exposed so WebApplicationFactory-based tests can bootstrap this host.</summary>
public partial class Program;
