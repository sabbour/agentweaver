using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;

namespace Agentweaver.Identity;

/// <summary>Validated loopback CRL listener settings for local X509Chain revocation checks.</summary>
public sealed record LoopbackCrlEndpointOptions(int LoopbackPort, string FilePath);

/// <summary>
/// Serves an operator-signed DER CRL on loopback HTTP only for private service mTLS validation.
/// Uses programmatic Kestrel binding outside Kestrel:Endpoints so configured HTTPS listeners stay isolated.
/// </summary>
public static class LoopbackCrlEndpoint
{
    public const string SectionName = "ServiceBackendCrl";
    public const string RoutePath = "/ca.crl";

    public static LoopbackCrlEndpointOptions? AddLoopbackCrl(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var section = builder.Configuration.GetSection(SectionName);
        if (!section.Exists())
            return null;

        var children = section.GetChildren().ToArray();
        if (children.Length == 0)
            return null;
        if (children.Any(child => child.Key is not ("LoopbackPort" or "FilePath")))
            throw new InvalidOperationException("ServiceBackendCrl contains unsupported configuration keys.");

        var portValue = section["LoopbackPort"];
        var filePath = section["FilePath"];
        if (!int.TryParse(portValue, out var port) || port is < 1 or > 65535 ||
            string.IsNullOrWhiteSpace(filePath) || !Path.IsPathFullyQualified(filePath))
            throw new InvalidOperationException("ServiceBackendCrl requires a valid LoopbackPort and absolute FilePath.");

        var info = new FileInfo(filePath);
        if (!info.Exists || info.Length == 0)
            throw new InvalidOperationException("ServiceBackendCrl requires an existing non-empty CRL file.");

        if (GetConfiguredPorts(builder.Configuration).Contains(port))
            throw new InvalidOperationException($"ServiceBackendCrl LoopbackPort {port} collides with a configured listener port.");

        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(IPAddress.Loopback, port, listen => listen.Protocols = HttpProtocols.Http1));

        return new LoopbackCrlEndpointOptions(port, filePath);
    }

    public static void MapLoopbackCrl(this IEndpointRouteBuilder endpoints, LoopbackCrlEndpointOptions? options)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (options is null)
            return;

        endpoints.MapGet(RoutePath, async (HttpContext context, CancellationToken cancellationToken) =>
        {
            var remote = context.Connection.RemoteIpAddress;
            if (context.Connection.LocalPort != options.LoopbackPort ||
                remote is null ||
                !IPAddress.IsLoopback(remote))
            {
                return Results.NotFound();
            }

            if (!File.Exists(options.FilePath))
                return Results.NotFound();

            var bytes = await File.ReadAllBytesAsync(options.FilePath, cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0)
                return Results.NotFound();

            context.Response.Headers.CacheControl = "no-store";
            return Results.Bytes(bytes, "application/pkix-crl");
        });
    }

    private static HashSet<int> GetConfiguredPorts(IConfiguration configuration)
    {
        var ports = new HashSet<int>();
        AddPortsFromUrlList(configuration["ASPNETCORE_URLS"], ports);
        AddPortsFromUrlList(configuration["urls"], ports);
        foreach (var endpoint in configuration.GetSection("Kestrel:Endpoints").GetChildren())
            AddPortsFromUrlList(endpoint["Url"], ports);

        if (int.TryParse(configuration["ProjectsConfig:ProviderHostRequest:Tls:ListenerPort"], out var projectsPort))
            ports.Add(projectsPort);
        if (int.TryParse(configuration["Environment:ProviderHostRequest:Tls:ListenerPort"], out var envPort))
            ports.Add(envPort);
        return ports;
    }

    private static void AddPortsFromUrlList(string? value, HashSet<int> ports)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        foreach (var item in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalized = item
                .Replace("://+:", "://localhost:", StringComparison.Ordinal)
                .Replace("://*:", "://localhost:", StringComparison.Ordinal);
            if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri) && uri.Port is >= 1 and <= 65535)
                ports.Add(uri.Port);
        }
    }
}
