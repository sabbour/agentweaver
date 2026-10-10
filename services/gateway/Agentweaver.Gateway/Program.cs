using Agentweaver.Gateway;
using Agentweaver.Telemetry;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using OpenIddict.Validation.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = 64 * 1024);
var options = GatewayOptions.Read(builder.Configuration);

builder.Logging.AddFilter("OpenIddict", LogLevel.Warning);
builder.Logging.AddFilter("OpenIddict.Validation.OpenIddictValidationDispatcher", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Authentication", LogLevel.Warning);
builder.Services.AddSingleton(options);
builder.Services.AddGatewayWebCors(options);
builder.Services.AddAgentweaverTelemetry("agentweaver.gateway");
builder.Services.AddHttpClient(nameof(GatewayOwner.Projects), client =>
    client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(CreateOwnerHandler);
builder.Services.AddHttpClient(nameof(GatewayOwner.Orchestrator), client =>
    client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(CreateOwnerHandler);
builder.Services.AddHttpClient(nameof(GatewayOwner.Knowledge), client =>
    client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(CreateOwnerHandler);
builder.Services.AddHttpClient(nameof(GatewayOwner.Events), client =>
    client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(CreateOwnerHandler);
builder.Services.AddHttpClient(nameof(GatewayOwner.IdentityBroker), client =>
    client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(CreateOwnerHandler);
builder.Services.AddSingleton<GatewayOwnerClient>();
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, GatewayAuthorizationResultHandler>();
builder.Services.AddAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
builder.Services.AddOpenIddict().AddValidation(validation =>
{
    validation.SetIssuer(options.IdentityIssuer);
    validation.AddAudiences(options.IdentityAudience);
    validation.UseSystemNetHttp();
    validation.UseAspNetCore();
});
builder.Services.AddAuthorization();

var app = builder.Build();
app.UseRouting();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapGatewayEndpoints();
app.Run();

static HttpMessageHandler CreateOwnerHandler() => new HttpClientHandler
{
    AllowAutoRedirect = false,
    UseCookies = false,
};

public partial class Program;
