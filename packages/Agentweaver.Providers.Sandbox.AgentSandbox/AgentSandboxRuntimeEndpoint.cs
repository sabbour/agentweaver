using System.Text.Json;
using Agentweaver.Abstractions;

namespace Agentweaver.Providers.Sandbox.AgentSandbox;

public sealed partial class AgentSandboxProvider
{
    private async Task<SandboxRuntimeHttpsEndpoint> ReadRuntimeEndpointAsync(
        JsonElement sandbox,
        JsonElement pod,
        string @namespace,
        AgentSandboxOptions options,
        CancellationToken cancellationToken)
    {
        if (options.AgentHost is null)
            throw RuntimeEndpointUnavailable();
        var serviceName = ReadOptionalString(sandbox, "status", "service");
        var fqdn = ReadOptionalString(sandbox, "status", "serviceFQDN");
        var podAddress = ReadOptionalString(pod, "status", "podIP");
        if (string.IsNullOrWhiteSpace(serviceName) || string.IsNullOrWhiteSpace(fqdn) ||
            string.IsNullOrWhiteSpace(podAddress) ||
            serviceName.Length > 63 ||
            serviceName.Split('.').Any(label => !IsRuntimeDnsLabel(label)) ||
            fqdn.Length > 253 ||
            !fqdn.StartsWith($"{serviceName}.{@namespace}.svc.", StringComparison.Ordinal) ||
            fqdn.Split('.').Any(label => !IsRuntimeDnsLabel(label)))
            throw RuntimeEndpointUnavailable();
        var service = await _client.GetAsync(
            string.Empty, "services", @namespace, serviceName, cancellationToken).ConfigureAwait(false)
            ?? throw RuntimeEndpointUnavailable();
        var metadata = RequiredObject(service, "metadata");
        var sandboxUid = RequiredString(RequiredObject(sandbox, "metadata"), "uid");
        var podUid = RequiredString(RequiredObject(pod, "metadata"), "uid");
        var spec = RequiredObject(service, "spec");
        var selector = ReadStringDictionary(spec, "selector");
        var owners = RequiredArray(metadata, "ownerReferences");
        var ports = RequiredArray(spec, "ports");
        if (RequiredString(metadata, "name") != serviceName ||
            RequiredString(metadata, "namespace") != @namespace ||
            metadata.TryGetProperty("deletionTimestamp", out var deleted) &&
                deleted.ValueKind != JsonValueKind.Null ||
            ReadOptionalString(spec, "type") != "ClusterIP" ||
            ports.GetArrayLength() != 1 ||
            !IsAgentHostServicePort(ports[0]) ||
            !owners.EnumerateArray().Any(owner =>
                ReadOptionalString(owner, "kind") == "Sandbox" &&
                ReadOptionalString(owner, "uid") == sandboxUid &&
                TryGetBoolean(owner, "controller", out var controller) && controller) ||
            selector.Count == 0 ||
            selector.Any(pair => !ReadLabels(pod).TryGetValue(pair.Key, out var value) || value != pair.Value))
            throw RuntimeEndpointUnavailable();
        var selectedPods = await _client.ListAsync(
            string.Empty, "pods", @namespace, selector, cancellationToken).ConfigureAwait(false);
        if (selectedPods.Count != 1 ||
            RequiredString(RequiredObject(selectedPods[0], "metadata"), "uid") != podUid ||
            ReadOptionalString(selectedPods[0], "status", "podIP") != podAddress)
            throw RuntimeEndpointUnavailable();
        try
        {
            var address = new Uri($"https://{fqdn}:8443/");
            var configureEndpoint = await ReadReceiverConfigureEndpointAsync(
                @namespace, options.AgentHost, cancellationToken).ConfigureAwait(false);
            var endpoint = new SandboxRuntimeHttpsEndpoint(
                address, podAddress, podUid, RequiredString(metadata, "uid"))
            {
                ReceiverConfigureEndpoint = configureEndpoint
            };
            return endpoint.ValidateForConfigureEndpoint(configureEndpoint);
        }
        catch (ArgumentException exception)
        {
            throw new SandboxProviderException(
                "sandbox_runtime_endpoint_invalid", exception.Message, effectMayHaveApplied: false, exception);
        }
    }

    private static bool IsAgentHostServicePort(JsonElement port) =>
        ReadOptionalString(port, "name") == "https" &&
        TryGetInt64(port, "port", out var servicePort) &&
        servicePort == 8443 &&
        ReadOptionalString(port, "protocol") == "TCP" &&
        (ReadOptionalString(port, "targetPort") == "https" ||
         TryGetInt64(port, "targetPort", out var targetPort) && targetPort == 8443);

    private static bool IsRuntimeDnsLabel(string label) =>
        label.Length is > 0 and <= 63 &&
        label[0] is >= 'a' and <= 'z' or >= '0' and <= '9' &&
        label[^1] is >= 'a' and <= 'z' or >= '0' and <= '9' &&
        label.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private async Task<Uri> ReadReceiverConfigureEndpointAsync(
        string @namespace, AgentSandboxOptions.AgentSandboxAgentHostProfile? host,
        CancellationToken cancellationToken)
    {
        if (host is null)
            throw RuntimeEndpointUnavailable();
        var configMap = await _client.GetAsync(
            string.Empty, "configmaps", @namespace, host.ConfigurationMapName, cancellationToken)
            .ConfigureAwait(false);
        if (configMap is null ||
            ReadOptionalString(configMap.Value, "metadata", "name") != host.ConfigurationMapName ||
            ReadOptionalString(configMap.Value, "metadata", "namespace") != @namespace ||
            !TryGetBoolean(configMap.Value, "immutable", out var immutable) || !immutable)
            throw RuntimeEndpointUnavailable();

        var appSettings = ReadOptionalString(configMap.Value, "data", "appsettings.json")
            ?? throw RuntimeEndpointUnavailable();
        try
        {
            using var document = JsonDocument.Parse(appSettings);
            var root = document.RootElement;
            var configure = ReadOptionalString(root, "AgentHost", "ConfigureEndpoint");
            var audience = ReadOptionalString(root, "AgentHost", "Authentication", "Audience");
            if (!Uri.TryCreate(configure, UriKind.Absolute, out var configureEndpoint) ||
                configureEndpoint.Scheme != Uri.UriSchemeHttps ||
                !string.IsNullOrEmpty(configureEndpoint.UserInfo) ||
                !string.IsNullOrEmpty(configureEndpoint.Fragment) ||
                !string.IsNullOrEmpty(configureEndpoint.Query) ||
                configureEndpoint.AbsolutePath != "/runtime/v1/configure" ||
                !Uri.TryCreate(audience, UriKind.Absolute, out var audienceEndpoint) ||
                audienceEndpoint != configureEndpoint)
                throw RuntimeEndpointUnavailable();
            return configureEndpoint;
        }
        catch (JsonException exception)
        {
            throw new SandboxProviderException(
                "sandbox_runtime_configuration_invalid",
                "The immutable AgentHost configuration is invalid.",
                effectMayHaveApplied: false,
                exception);
        }
    }

    private static SandboxProviderException RuntimeEndpointUnavailable() =>
        new("sandbox_runtime_endpoint_unavailable",
            "The exact Sandbox Pod and its owned HTTPS service are not available.", effectMayHaveApplied: false);
}
