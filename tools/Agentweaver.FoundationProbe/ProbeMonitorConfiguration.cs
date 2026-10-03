using System.Text.Json.Serialization;

namespace Agentweaver.FoundationProbe;

internal static class ProbeMonitorConfiguration
{
    private const string AzureIngestionSuffix = ".in.applicationinsights.azure.com";

    public static MonitorConfigurationEvidence Validate(string? connectionString, ProbeTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.FoundationResources is null)
            throw new ProbeException("monitor_configuration_invalid");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ProbeException("monitor_configuration_missing");
        if (connectionString.Length > 4096 || connectionString.Any(char.IsControl))
            throw new ProbeException("monitor_configuration_invalid");

        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0 || separator == part.Length - 1)
                throw new ProbeException("monitor_configuration_invalid");
            var key = part[..separator].Trim();
            var value = part[(separator + 1)..].Trim();
            if (key.Length == 0 || value.Length == 0 || !properties.TryAdd(key, value))
                throw new ProbeException("monitor_configuration_invalid");
        }

        if (!properties.TryGetValue("InstrumentationKey", out var instrumentationKey) ||
            !Guid.TryParse(instrumentationKey, out var parsedKey) || parsedKey == Guid.Empty ||
            !properties.TryGetValue("IngestionEndpoint", out var endpointValue) ||
            !Uri.TryCreate(endpointValue, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps || !endpoint.IsDefaultPort ||
            endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 ||
            endpoint.AbsolutePath is not ("" or "/") ||
            endpoint.HostNameType != UriHostNameType.Dns ||
            !endpoint.Host.EndsWith(AzureIngestionSuffix, StringComparison.OrdinalIgnoreCase))
            throw new ProbeException("monitor_configuration_invalid");

        return new MonitorConfigurationEvidence(
            target.FoundationResources?.AppInsightsResourceId ?? "",
            endpoint.AbsoluteUri,
            InstrumentationKeyConfigured: true);
    }

    public static bool IsValidFor(MonitorConfigurationEvidence? evidence, ProbeTarget target)
    {
        if (target.FoundationResources is null || evidence is null || !evidence.InstrumentationKeyConfigured ||
            !string.Equals(evidence.AppInsightsResourceId, target.FoundationResources.AppInsightsResourceId,
                StringComparison.OrdinalIgnoreCase) ||
            !Uri.TryCreate(evidence.IngestionEndpoint, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps || !endpoint.IsDefaultPort ||
            endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 ||
            endpoint.AbsolutePath is not ("" or "/") ||
            endpoint.HostNameType != UriHostNameType.Dns ||
            !endpoint.Host.EndsWith(AzureIngestionSuffix, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record MonitorConfigurationEvidence(
    string AppInsightsResourceId,
    string IngestionEndpoint,
    bool InstrumentationKeyConfigured);
