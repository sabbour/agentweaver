using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Identity;

/// <summary>Dedicated, certificate-pinned issue-sync service channels; no run or user token authority.</summary>
public static class IssueSyncServiceTransport
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static bool AddChannel(WebApplicationBuilder builder, string key, string sectionName,
        string local, string peer, bool listener)
    {
        var section = builder.Configuration.GetSection(sectionName);
        if (!section.Exists())
            return false;
        var options = (section.Get<RuntimeServiceTlsOptions>()
            ?? throw new InvalidOperationException("Issue sync requires an explicit TLS profile.")).Validate(local, peer);
        if (options.PeerCertificateSha256Pins!.Contains(options.LocalCertificateSha256!, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Issue sync peer certificates must be distinct.");
        builder.Services.AddKeyedSingleton(key, options);
        builder.Services.AddKeyedSingleton<RuntimeServiceTlsIdentity>(key, (services, _) =>
            new(options, local, peer, services.GetRequiredService<TimeProvider>()));
        builder.Services.AddHttpClient(key, client => client.Timeout = TimeSpan.FromSeconds(20))
            .ConfigurePrimaryHttpMessageHandler(services =>
                services.GetRequiredKeyedService<RuntimeServiceTlsIdentity>(key).CreateHandler());
        if (listener)
        {
            var endpoints = builder.Configuration.GetSection("Kestrel:Endpoints");
            if (!Uri.TryCreate(endpoints[key + ":Url"], UriKind.Absolute, out var address) ||
                address.Scheme != Uri.UriSchemeHttps || address.Port != options.ListenerPort ||
                endpoints.GetChildren().Any(endpoint => endpoint.Key != key &&
                    Uri.TryCreate(endpoint["Url"], UriKind.Absolute, out var other) && other.Port == options.ListenerPort))
                throw new InvalidOperationException("Issue sync requires a dedicated configured HTTPS listener.");
            builder.WebHost.ConfigureKestrel(server =>
                server.Configure(builder.Configuration.GetSection("Kestrel")).Endpoint(key, endpoint =>
                {
                    var identity = server.ApplicationServices.GetRequiredKeyedService<RuntimeServiceTlsIdentity>(key);
                    endpoint.HttpsOptions.ServerCertificate = identity.Certificate;
                    endpoint.HttpsOptions.SslProtocols = RuntimeServiceTlsIdentity.AllowedProtocols;
                    endpoint.HttpsOptions.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                    endpoint.HttpsOptions.CheckCertificateRevocation = true;
                    endpoint.HttpsOptions.ClientCertificateValidation = identity.AcceptClientCertificate;
                }));
        }
        return true;
    }

    public static void RequirePeer(HttpContext context, string key)
    {
        var identity = context.RequestServices.GetRequiredKeyedService<RuntimeServiceTlsIdentity>(key);
        identity.RequirePeerConnection(context.Request.IsHttps,
            context.Features.Get<ITlsHandshakeFeature>()?.Protocol, context.Connection.LocalPort,
            context.Features.Get<ITlsConnectionFeature>()?.ClientCertificate);
    }

    public static async Task<T> SendAsync<T>(IServiceProvider services, string key, string path,
        object body, CancellationToken cancellationToken)
    {
        var identity = services.GetRequiredKeyedService<RuntimeServiceTlsIdentity>(key);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(identity.PeerOwnerAddress, path))
        {
            Content = JsonContent.Create(body, options: Json)
        };
        using var response = await services.GetRequiredService<IHttpClientFactory>().CreateClient(key)
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await response.Content.LoadIntoBufferAsync(64 * 1024, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadFromJsonAsync<ServiceError>(Json, cancellationToken).ConfigureAwait(false);
            throw new RuntimeAuthorizationException(error?.Error ?? "issue_sync_authority_unavailable");
        }
        if (response.Headers.CacheControl?.NoStore != true)
            throw new RuntimeAuthorizationException("issue_sync_authority_response_invalid");
        return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken).ConfigureAwait(false)
            ?? throw new RuntimeAuthorizationException("issue_sync_authority_response_invalid");
    }

    private sealed record ServiceError(string Error);
}
