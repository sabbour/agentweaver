using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.EventsAndSessions;
using Agentweaver.Providers;
using Agentweaver.Telemetry;
using Microsoft.AspNetCore.Authentication;
using Npgsql;
using OpenIddict.Validation.AspNetCore;

var migrate = args.SequenceEqual(["--migrate"], StringComparer.Ordinal);
if (args.Contains("--migrate", StringComparer.Ordinal) && !migrate)
    throw new ArgumentException("The --migrate command must be used by itself.");

var builder = WebApplication.CreateBuilder(migrate ? [] : args);
builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = 64 * 1024);
var connectionString = Required(builder.Configuration, "ConnectionStrings:EventsAndSessions");
var providerSection = builder.Configuration.GetSection("EventsAndSessions:Provider");
var options = new PostgresSessionsProviderOptions(
    RequiredSection(providerSection, "ResourceId"),
    RequiredSection(providerSection, "DatabaseName"),
    providerSection.GetValue<long>("ResourceGeneration"),
    providerSection["Schema"] ?? "events_sessions",
    providerSection["OptionsRevision"] ?? "native-postgres-v1",
    providerSection.GetValue("OptionsSchemaVersion", 1),
    providerSection.GetValue("PollIntervalMilliseconds", 250),
    providerSection.GetValue("ReferenceRetentionDays", 365));
options.Validate();

var dataSource = NpgsqlDataSource.Create(connectionString);
if (migrate)
{
    await EventsAndSessionsMigrator.MigrateAsync(dataSource, options.Schema);
    await dataSource.DisposeAsync();
    return;
}

var issuer = Required(builder.Configuration, "Identity:Issuer");
if (!Uri.TryCreate(issuer, UriKind.Absolute, out var issuerUri) || issuerUri.Scheme != Uri.UriSchemeHttps)
    throw new InvalidOperationException("Identity issuer must be an absolute HTTPS URI.");
var audience = Required(builder.Configuration, "Identity:Audience");

builder.Logging.AddFilter("OpenIddict", LogLevel.Warning);
builder.Logging.AddFilter("OpenIddict.Validation.OpenIddictValidationDispatcher", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Authentication", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Logging.AddFilter("Npgsql", LogLevel.Warning);

builder.Services.AddSingleton(dataSource);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddAgentweaverTelemetry("agentweaver.events");
builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow;
    json.SerializerOptions.MaxDepth = 16;
    json.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(
        System.Text.Json.JsonNamingPolicy.CamelCase));
});
builder.Services.AddSingleton<ISessionsJournal, PostgresSessionsJournal>();
builder.Services.AddAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
builder.Services.AddOpenIddict().AddValidation(validation =>
{
    validation.SetIssuer(issuerUri);
    validation.AddAudiences(audience);
    validation.UseSystemNetHttp();
    validation.UseAspNetCore();
});
builder.Services.AddAuthorization();

var projectOverrides = builder.Configuration.GetSection("EventsAndSessions:ProjectOverrides")
    .GetChildren()
    .ToImmutableDictionary(section => section.Key, section => section.Value ?? string.Empty, StringComparer.Ordinal);
var provider = new NativePostgresSessionsProvider();
var registration = provider.CreateRegistration(options);
var permittedOverrides = projectOverrides.Values.Distinct(StringComparer.Ordinal)
    .Select(providerId => new ProviderOverridePermission(ProviderSeam.Sessions, providerId))
    .ToArray();
var catalogResult = ProviderCatalog.Create(
    [registration],
    [new ProviderSelection(ProviderSeam.Sessions, NativePostgresSessionsProvider.ProviderId)],
    permittedOverrides);
if (!catalogResult.IsSuccess)
    throw new InvalidOperationException("The Sessions provider catalog configuration is invalid.");
var resolver = new ProviderResolver(catalogResult.Value!);
builder.Services.AddSingleton(provider);
builder.Services.AddSingleton(catalogResult.Value!);
builder.Services.AddSingleton(resolver);
builder.Services.AddSingleton<IReadOnlyDictionary<string, string>>(projectOverrides);
builder.Services.AddSingleton<SessionsProviderBindingService>();
builder.Services.AddSingleton<ISessionsProviderBinder>(services =>
    services.GetRequiredService<SessionsProviderBindingService>());

var app = builder.Build();
await EventsAndSessionsMigrator.VerifyAsync(dataSource, options.Schema);
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", async (CancellationToken ct) =>
{
    try
    {
        await EventsAndSessionsMigrator.VerifyAsync(dataSource, options.Schema, ct);
        return Results.Ok(new { status = "ready" });
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        throw;
    }
    catch (Exception exception) when (exception is not OutOfMemoryException)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
});
app.MapEventsAndSessionsEndpoints();
app.Run();

static string Required(IConfiguration configuration, string key) =>
    !string.IsNullOrWhiteSpace(configuration[key])
        ? configuration[key]!
        : throw new InvalidOperationException($"Missing required configuration '{key}'.");

static string RequiredSection(IConfigurationSection configuration, string key) =>
    !string.IsNullOrWhiteSpace(configuration[key])
        ? configuration[key]!
        : throw new InvalidOperationException($"Missing required configuration '{configuration.Path}:{key}'.");

public partial class Program;
