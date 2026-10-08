using Agentweaver.AgentHost;
using Agentweaver.AgentRuntime;
using Agentweaver.Identity;
using Agentweaver.Telemetry;
using GitHub.Copilot;
using Microsoft.Extensions.Logging.Abstractions;
using OpenIddict.Validation.AspNetCore;

if (args.SequenceEqual(["--verify-native-runtime"]))
{
    var verified = NativeRuntimeManifest.ReadAndVerify(AppContext.BaseDirectory);
    await using var client = new CopilotClient(new CopilotClientOptions
    {
        Mode = CopilotClientMode.Empty,
        Connection = NativeRuntimeManifest.Connection(verified.ExecutablePath, "/state"),
        BaseDirectory = "/state",
        Logger = NullLogger.Instance,
        LogLevel = CopilotLogLevel.None
    });
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await client.StartAsync(deadline.Token);
    var status = await client.GetStatusAsync(deadline.Token);
    if (status.Version != verified.RuntimeVersion)
        throw new InvalidOperationException("The native SDK status does not match the image-owned runtime.");
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
    {
        sdkVersion = "1.0.11", runtimeVersion = status.Version, status.ProtocolVersion
    }));
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = 128 * 1024);
var section = builder.Configuration.GetSection("AgentHost");
var options = section.Get<RuntimeAgentHostOptions>()
    ?? throw new InvalidOperationException("Explicit AgentHost owner, placement, image, and queue configuration is required.");
options.Validate();
var authority = new Uri(Required(section["Authentication:Authority"], "AgentHost:Authentication:Authority"));
if (!RuntimeContractValidation.IsHttpsEndpoint(authority))
    throw new InvalidOperationException("AgentHost authentication requires an exact HTTPS issuer.");
var audience = Required(section["Authentication:Audience"], "AgentHost:Authentication:Audience");
var models = section.GetSection("ModelBindings").Get<Dictionary<string, RuntimeModelBinding>>()
    ?? throw new InvalidOperationException("Explicit server-owned AgentHost model bindings are required.");
var baseDirectory = Required(section["PrivateStateDirectory"], "AgentHost:PrivateStateDirectory");
var workingDirectory = Required(section["WorkingDirectory"], "AgentHost:WorkingDirectory");
if (!Path.IsPathFullyQualified(baseDirectory) || !Path.IsPathFullyQualified(workingDirectory) ||
    !Directory.Exists(baseDirectory) || !Directory.Exists(workingDirectory) ||
    baseDirectory == workingDirectory)
    throw new InvalidOperationException("Existing, distinct absolute private-state and attached-workspace directories are required.");
var native = NativeRuntimeManifest.ReadAndVerify(AppContext.BaseDirectory);
var connection = NativeRuntimeManifest.Connection(native.ExecutablePath, baseDirectory);
var sessions = new RuntimeCopilotSessionFactory(connection, baseDirectory, models,
    workingDirectory: workingDirectory, expectedRuntimeVersion: native.RuntimeVersion);
builder.Logging.AddFilter("OpenIddict", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Authentication", LogLevel.Warning);
builder.Services.AddAgentweaverTelemetry("agentweaver.agenthost");
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(sessions);
builder.Services.AddHttpClient("runtime-owners", client => client.Timeout = TimeSpan.FromSeconds(20))
    .ConfigurePrimaryHttpMessageHandler(RuntimeOwnerHttpTransport.CreateHandler);
builder.Services.AddSingleton(services =>
{
    var http = services.GetRequiredService<IHttpClientFactory>().CreateClient("runtime-owners");
    return new RuntimeAgentHost(options, http,
        new RuntimeRegistrationHttpClient(http, options.OrchestratorAddress), sessions,
        services.GetRequiredService<TimeProvider>());
});
builder.Services.AddAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
builder.Services.AddOpenIddict().AddValidation(validation =>
{
    validation.SetIssuer(authority);
    validation.AddAudiences(audience);
    validation.UseSystemNetHttp();
    validation.UseAspNetCore();
});
builder.Services.AddAuthorization();
builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.UnmappedMemberHandling =
        System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow;
    json.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(
        System.Text.Json.JsonNamingPolicy.CamelCase));
});
var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapRuntimeAgentHost(app.Services.GetRequiredService<RuntimeAgentHost>(), TimeProvider.System);
await app.RunAsync();

static string Required(string? value, string name) =>
    string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException($"{name} is required.") : value;

public partial class Program;
