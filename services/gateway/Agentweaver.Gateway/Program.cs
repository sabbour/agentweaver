using Agentweaver.Gateway;
using Agentweaver.Identity;
using Agentweaver.Telemetry;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using OpenIddict.Validation.AspNetCore;

// Keep owner HTTP clients unbounded here; GatewayOwnerClient applies per-request deadlines.
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
    .ConfigurePrimaryHttpMessageHandler(RuntimeOwnerHttpTransport.CreateTrustedOwnerHandler);
builder.Services.AddHttpClient(nameof(GatewayOwner.Orchestrator), client =>
    client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(RuntimeOwnerHttpTransport.CreateTrustedOwnerHandler);
builder.Services.AddHttpClient(nameof(GatewayOwner.Knowledge), client =>
    client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(RuntimeOwnerHttpTransport.CreateTrustedOwnerHandler);
builder.Services.AddHttpClient(nameof(GatewayOwner.Events), client =>
    client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(RuntimeOwnerHttpTransport.CreateTrustedOwnerHandler);
builder.Services.AddHttpClient(nameof(GatewayOwner.IdentityBroker), client =>
    client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(RuntimeOwnerHttpTransport.CreateTrustedOwnerHandler);
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

// Authentication and authorization run before the explicit public route map.
var app = builder.Build();
app.UseRouting();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapGatewayEndpoints();
app.Run();

public partial class Program;
