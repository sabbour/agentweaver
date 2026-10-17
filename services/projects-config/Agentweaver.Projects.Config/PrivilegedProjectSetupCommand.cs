extern alias AzureIdentity;

using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Providers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using WorkloadIdentityCredential = AzureIdentity::Azure.Identity.WorkloadIdentityCredential;

namespace Agentweaver.Projects.Config;

/// <summary>
/// Runs the one-shot, privileged project setup protocol used by the operator-facing command-line entry point.
/// </summary>
internal static class PrivilegedProjectSetupCommand
{
    private const int MaxInputBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    /// <summary>
    /// Reads bounded JSON from standard input, authenticates the supplied bearer token, and executes a setup operation.
    /// </summary>
    /// <param name="configuration">Service configuration for authentication, database access, and approved caller identity.</param>
    /// <param name="cancellationToken">Cancels input, database, and authentication work.</param>
    /// <exception cref="ProjectConfigException">The request or authenticated caller is not authorized.</exception>
    /// <exception cref="InvalidOperationException">Required configuration or database migration state is invalid.</exception>
    public static async Task RunAsync(IConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var input = await ReadInputAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(input.AccessToken))
            throw ProjectConfigException.Forbidden();

        var authentication = configuration.GetSection("ProjectsConfig:Authentication");
        var authority = RequireHttpsUri(
            authentication["Authority"],
            "ProjectsConfig:Authentication:Authority");
        var audience = authentication["Audience"];
        if (string.IsNullOrWhiteSpace(audience))
            throw new InvalidOperationException(
                "Missing required configuration 'ProjectsConfig:Authentication:Audience'.");

        var setup = configuration.GetSection("ProjectsConfig:PrivilegedSetup");
        var approvedIssuer = RequireHttpsUri(
            setup["ApprovedIssuer"],
            "ProjectsConfig:PrivilegedSetup:ApprovedIssuer");
        var approvedSubject = RequireIdentifier(
            setup["ApprovedSubject"],
            "ProjectsConfig:PrivilegedSetup:ApprovedSubject");
        var approvedTenantId = RequireIdentifier(
            setup["ApprovedTenantId"],
            "ProjectsConfig:PrivilegedSetup:ApprovedTenantId");
        if (!string.Equals(authority.AbsoluteUri, approvedIssuer.AbsoluteUri, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The privileged setup issuer must match the configured Projects & Config authentication authority.");
        if (!string.Equals(input.TenantId, approvedTenantId, StringComparison.Ordinal))
            throw ProjectConfigException.Forbidden();

        var connectionString = configuration.GetConnectionString("ProjectsConfigAuthority")
            ?? throw new InvalidOperationException(
                "Missing required configuration 'ConnectionStrings:ProjectsConfigAuthority'.");
        var identityOptions = ProjectsConfigPostgresDataSource.ReadWorkloadIdentityOptions(
            configuration, "ProjectsConfig:AuthorityDatabase:WorkloadIdentity");
        var credential = new WorkloadIdentityCredential(identityOptions);
        await using var dataSource = ProjectsConfigPostgresDataSource.Create(connectionString, credential);
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.ClearProviders());
        services.AddSingleton(dataSource);
        services.AddDbContextFactory<ProjectsConfigDbContext>((_, options) =>
        {
            options.UseNpgsql(
                dataSource,
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history", ProjectsConfigDbContext.Schema));
        });
        services.AddSingleton(new ProjectsConfigIdentityOptions(authority.AbsoluteUri));
        services.AddScoped<ProjectAuthorizationOwner>();
        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = authority.AbsoluteUri;
                options.Audience = audience;
                options.RequireHttpsMetadata = true;
                options.MapInboundClaims = false;
            });

        await using var provider = services.BuildServiceProvider();
        await ProjectsConfigMigrator.VerifyMigrationsAppliedAsync(
            dataSource,
            provider.GetRequiredService<DbContextOptions<ProjectsConfigDbContext>>(),
            cancellationToken).ConfigureAwait(false);

        var httpContext = new DefaultHttpContext { RequestServices = provider };
        httpContext.Request.Headers.Authorization = $"Bearer {input.AccessToken}";
        var authenticationService = provider.GetRequiredService<IAuthenticationService>();
        var authenticationResult = await authenticationService.AuthenticateAsync(
            httpContext, JwtBearerDefaults.AuthenticationScheme).ConfigureAwait(false);
        var principal = authenticationResult.Principal;
        if (!authenticationResult.Succeeded || principal is null)
            throw ProjectConfigException.Forbidden();

        await using var db = new ProjectsConfigDbContext(
            provider.GetRequiredService<DbContextOptions<ProjectsConfigDbContext>>());
        var caller = await new ProjectAuthorizationOwner(
                db, new ProjectsConfigIdentityOptions(authority.AbsoluteUri))
            .ResolveAsync(principal, [input.TenantId], cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(caller.Issuer, approvedIssuer.AbsoluteUri, StringComparison.Ordinal) ||
            !string.Equals(caller.ActorId, approvedSubject, StringComparison.Ordinal) ||
            !string.Equals(caller.TenantId, approvedTenantId, StringComparison.Ordinal))
            throw ProjectConfigException.Forbidden();

        var request = new ProjectSetupOperationRequest(
            input.Version,
            input.InvocationId,
            input.OperationKind,
            input.TenantId,
            input.ExpectedProjectRevision,
            input.ExpectedAssignmentRevision);
        var store = new ProjectsConfigPrivilegedAuthorityStore(
            provider.GetRequiredService<DbContextOptions<ProjectsConfigDbContext>>(),
            TimeProvider.System);
        var result = await store.ExecuteProjectSetupOperationAsync(
            caller, request, cancellationToken).ConfigureAwait(false);
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(result, JsonOptions)).ConfigureAwait(false);
    }

    /// <summary>Reads at most the configured input bound and rejects malformed or unknown JSON fields.</summary>
    private static async Task<PrivilegedProjectSetupInput> ReadInputAsync(CancellationToken cancellationToken)
    {
        await using var input = Console.OpenStandardInput();
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var count = await input.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (count == 0)
                break;
            if (buffer.Length + count > MaxInputBytes)
                throw ProjectConfigException.Invalid(
                    "invalid_project_setup_request",
                    "The privileged project setup input exceeds its size limit.");
            await buffer.WriteAsync(chunk.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return JsonSerializer.Deserialize<PrivilegedProjectSetupInput>(buffer.ToArray(), JsonOptions)
                ?? throw ProjectConfigException.Invalid(
                    "invalid_project_setup_request",
                    "The privileged project setup input is required.");
        }
        catch (JsonException)
        {
            throw ProjectConfigException.Invalid(
                "invalid_project_setup_request",
                "The privileged project setup input is not valid JSON.");
        }
    }

    /// <summary>Parses an absolute HTTPS authority URI without credentials, query parameters, or fragments.</summary>
    private static Uri RequireHttpsUri(string? value, string setting)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException(
                $"'{setting}' requires an absolute HTTPS URI without user information, query, or fragment.");
        return uri;
    }

    /// <summary>Validates an opaque identifier used to pin the approved setup caller.</summary>
    private static string RequireIdentifier(string? value, string setting)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 ||
            !value.All(character => char.IsAsciiLetterOrDigit(character) ||
                character is '.' or '_' or '-' or ':'))
            throw new InvalidOperationException(
                $"'{setting}' requires a nonempty opaque identifier.");
        return value;
    }

    /// <summary>Creates strict command JSON settings with camel-case enum values.</summary>
    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
