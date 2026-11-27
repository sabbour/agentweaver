extern alias AzureIdentity;

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WorkloadIdentityCredential = AzureIdentity::Azure.Identity.WorkloadIdentityCredential;

namespace Agentweaver.Projects.Config;

internal static class ProjectPolicyBaselineCommand
{
    internal const string Argument = "--project-policy-baseline";
    private const int MaxInputBytes = 1_572_864;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    internal static bool IsCommandAlone(string[] args) =>
        args.Length == 1 && args[0] == Argument;

    public static async Task RunAsync(IConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var provisioning = ProjectPolicyProvisioningApproval.Read(configuration);
        var connectionString = configuration.GetConnectionString("ProjectsConfigAuthority")
            ?? throw new InvalidOperationException(
                "Missing required configuration 'ConnectionStrings:ProjectsConfigAuthority'.");
        await using var stdin = Console.OpenStandardInput();
        var input = await ReadInputAsync(stdin, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(input.AccessToken))
            throw ProjectConfigException.Forbidden();
        if (!string.Equals(input.TenantId, provisioning.ApprovedTenantId, StringComparison.Ordinal))
            throw ProjectConfigException.Forbidden();

        var identityOptions = ProjectsConfigPostgresDataSource.ReadWorkloadIdentityOptions(
            configuration, "ProjectsConfig:AuthorityDatabase:WorkloadIdentity");
        var credential = new WorkloadIdentityCredential(identityOptions);
        await using var dataSource = ProjectsConfigPostgresDataSource.Create(connectionString, credential);
        var receipt = await ExecuteWithInputAsync(
            provisioning,
            input,
            dataSource,
            configureJwtBearer: null,
            cancellationToken).ConfigureAwait(false);
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(receipt, JsonOptions)).ConfigureAwait(false);
    }

    internal static async Task<NativeAcsProjectPolicyBaselineReceipt> RunCoreAsync(
        IConfiguration configuration,
        ProjectPolicyProvisioningApproval provisioning,
        Stream inputStream,
        Npgsql.NpgsqlDataSource dataSource,
        Action<JwtBearerOptions>? configureJwtBearer = null,
        CancellationToken cancellationToken = default)
    {
        var input = await ReadInputAsync(inputStream, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(input.AccessToken))
            throw ProjectConfigException.Forbidden();
        if (!string.Equals(input.TenantId, provisioning.ApprovedTenantId, StringComparison.Ordinal))
            throw ProjectConfigException.Forbidden();

        return await ExecuteWithInputAsync(
            provisioning,
            input,
            dataSource,
            configureJwtBearer,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<NativeAcsProjectPolicyBaselineReceipt> ExecuteWithInputAsync(
        ProjectPolicyProvisioningApproval provisioning,
        ProjectPolicyBaselineInput input,
        Npgsql.NpgsqlDataSource dataSource,
        Action<JwtBearerOptions>? configureJwtBearer,
        CancellationToken cancellationToken)
    {

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
        services.AddDataProtection();
        services.AddSingleton(new ProjectsConfigIdentityOptions(provisioning.Authority.AbsoluteUri));
        services.AddScoped<ProjectAuthorizationOwner>();
        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = provisioning.Authority.AbsoluteUri;
                options.Audience = provisioning.Audience;
                options.RequireHttpsMetadata = true;
                options.MapInboundClaims = false;
                configureJwtBearer?.Invoke(options);
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
                db, new ProjectsConfigIdentityOptions(provisioning.Authority.AbsoluteUri))
            .ResolveAsync(principal, [input.TenantId], cancellationToken)
            .ConfigureAwait(false);
        caller.RequireHumanActor();
        provisioning.RequireApprovedOperator(caller);

        var store = new NativeAcsPolicyStore(
            provider.GetRequiredService<IDbContextFactory<ProjectsConfigDbContext>>(),
            TimeProvider.System,
            provider.GetRequiredService<IDataProtectionProvider>());

        return input.Operation switch
        {
            ProjectPolicyBaselineOperation.Pin => await store.PinBaselineByOperatorAsync(
                caller,
                input.ProjectId,
                input.BaselineId,
                input.ExpectedContentSha256,
                input.IdempotencyKey ?? string.Empty,
                input.Reason,
                input.CorrelationId,
                cancellationToken).ConfigureAwait(false),
            ProjectPolicyBaselineOperation.Revoke => await store.RevokeBaselineByOperatorAsync(
                caller,
                input.ProjectId,
                input.BaselineId,
                input.ExpectedContentSha256,
                input.Reason,
                input.CorrelationId,
                cancellationToken).ConfigureAwait(false),
            _ => throw ProjectConfigException.Invalid("native_acs_policy_invalid", "Unsupported baseline operation."),
        };
    }

    private static async Task<ProjectPolicyBaselineInput> ReadInputAsync(CancellationToken cancellationToken)
    {
        await using var input = Console.OpenStandardInput();
        return await ReadInputAsync(input, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<ProjectPolicyBaselineInput> ReadInputAsync(
        Stream input,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var count = await input.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (count == 0)
                break;
            if (buffer.Length + count > MaxInputBytes)
                throw ProjectConfigException.Invalid(
                    "native_acs_policy_invalid",
                    "The project policy baseline input exceeds its size limit.");
            await buffer.WriteAsync(chunk.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var bytes = buffer.ToArray();
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
            if (HasAmbiguousProperties(document.RootElement))
                throw ProjectConfigException.Invalid(
                    "native_acs_policy_invalid",
                    "The project policy baseline input contains duplicate properties.");
            return document.RootElement.Deserialize<ProjectPolicyBaselineInput>(JsonOptions)
                ?? throw ProjectConfigException.Invalid(
                    "native_acs_policy_invalid",
                    "The project policy baseline input is required.");
        }
        catch (JsonException)
        {
            throw ProjectConfigException.Invalid(
                "native_acs_policy_invalid",
                "The project policy baseline input is not valid JSON.");
        }
    }

    private static bool HasAmbiguousProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
                if (!names.Add(property.Name) || HasAmbiguousProperties(property.Value))
                    return true;
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                if (HasAmbiguousProperties(item))
                    return true;
        }
        return false;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            AllowDuplicateProperties = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

internal enum ProjectPolicyBaselineOperation
{
    Pin,
    Revoke,
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ProjectPolicyBaselineInput
{
    public required string AccessToken { get; init; }
    public required ProjectPolicyBaselineOperation Operation { get; init; }
    public required string TenantId { get; init; }
    public required string ProjectId { get; init; }
    public required Guid BaselineId { get; init; }
    public required string ExpectedContentSha256 { get; init; }
    public string? IdempotencyKey { get; init; }
    public required string Reason { get; init; }
    public required string CorrelationId { get; init; }
}

internal sealed record ProjectPolicyProvisioningApproval(
    Uri Authority,
    string Audience,
    string ApprovedSubject,
    string ApprovedTenantId)
{
    public static ProjectPolicyProvisioningApproval Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var authentication = configuration.GetSection("ProjectsConfig:Authentication");
        var authority = RequireHttpsUri(
            authentication["Authority"],
            "ProjectsConfig:Authentication:Authority");
        var audience = authentication["Audience"];
        if (string.IsNullOrWhiteSpace(audience))
            throw new InvalidOperationException(
                "Missing required configuration 'ProjectsConfig:Authentication:Audience'.");

        var provisioning = configuration.GetSection("ProjectsConfig:ProjectPolicyProvisioning");
        var approvedIssuer = RequireHttpsUri(
            provisioning["ApprovedIssuer"],
            "ProjectsConfig:ProjectPolicyProvisioning:ApprovedIssuer");
        var approvedSubject = RequireIdentifier(
            provisioning["ApprovedSubject"],
            "ProjectsConfig:ProjectPolicyProvisioning:ApprovedSubject");
        var approvedTenantId = RequireIdentifier(
            provisioning["ApprovedTenantId"],
            "ProjectsConfig:ProjectPolicyProvisioning:ApprovedTenantId");
        if (!string.Equals(authority.AbsoluteUri, approvedIssuer.AbsoluteUri, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The project policy provisioning issuer must match the configured Projects & Config authentication authority.");

        return new ProjectPolicyProvisioningApproval(
            authority,
            audience,
            approvedSubject,
            approvedTenantId);
    }

    public void RequireApprovedOperator(ProjectAuthorizationContext caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!string.Equals(caller.Issuer, Authority.AbsoluteUri, StringComparison.Ordinal) ||
            !string.Equals(caller.ActorId, ApprovedSubject, StringComparison.Ordinal) ||
            !string.Equals(caller.TenantId, ApprovedTenantId, StringComparison.Ordinal))
            throw ProjectConfigException.Forbidden();
    }

    private static Uri RequireHttpsUri(string? value, string setting)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrEmpty(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException(
                $"'{setting}' requires an absolute HTTPS URI without user information, query, or fragment.");
        return uri;
    }

    private static string RequireIdentifier(string? value, string setting)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 ||
            !value.All(character => char.IsAsciiLetterOrDigit(character) ||
                character is '.' or '_' or '-' or ':'))
            throw new InvalidOperationException(
                $"'{setting}' requires a nonempty opaque identifier.");
        return value;
    }
}
