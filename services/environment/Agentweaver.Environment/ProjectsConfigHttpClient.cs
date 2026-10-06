using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Agentweaver.Abstractions;

namespace Agentweaver.Environment;

public sealed class CurrentCallerRequest
{
    public CurrentCallerRequest(string bearerToken, string? tenantSelector = null)
    {
        if (string.IsNullOrWhiteSpace(bearerToken) ||
            bearerToken.Length > 16_384 ||
            bearerToken.Any(char.IsWhiteSpace))
            throw new ArgumentException("A single validated caller bearer token is required.", nameof(bearerToken));
        BearerToken = bearerToken;
        TenantSelector = tenantSelector;
    }

    internal string BearerToken { get; }
    public string? TenantSelector { get; }

    public override string ToString() => "CurrentCallerRequest [token redacted]";
}

public sealed class ProjectsConfigApiException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public interface IProjectsConfigClient
{
    Task<ProjectAuthorizationContextResponse> GetAuthorizationContextAsync(
        CurrentCallerRequest caller,
        CancellationToken cancellationToken);

    Task<EffectiveNetworkPolicySelection> GetRunSelectionAsync(
        CurrentCallerRequest caller,
        string projectId,
        string runId,
        CancellationToken cancellationToken);
}

public sealed class ProjectsConfigHttpClient(HttpClient httpClient) : IProjectsConfigClient
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public async Task<ProjectAuthorizationContextResponse> GetAuthorizationContextAsync(
        CurrentCallerRequest caller,
        CancellationToken cancellationToken)
    {
        using var response = await SendGetAsync(
            caller,
            "/api/authorization/context",
            cancellationToken).ConfigureAwait(false);
        RequireNoStore(response);
        var context = await ReadJsonAsync<ProjectAuthorizationContextResponse>(response, cancellationToken)
            .ConfigureAwait(false);
        if (context.ContractVersion != Agentweaver.Abstractions.ProjectAuthorizationContextContract.CurrentVersion ||
            string.IsNullOrWhiteSpace(context.Issuer) ||
            string.IsNullOrWhiteSpace(context.ActorId) ||
            string.IsNullOrWhiteSpace(context.TenantId) ||
            context.MembershipRevision < 1 ||
            context.EffectiveAuthority.IsDefault ||
            context.EffectiveAuthority.Any(resource => resource is null ||
                !Enum.IsDefined(resource.ResourceType) ||
                string.IsNullOrWhiteSpace(resource.ResourceId) ||
                resource.Permissions.IsDefault ||
                resource.Permissions.Any(permission => permission is null ||
                    !Enum.IsDefined(permission.Permission) || permission.RoleRevision < 1)))
            throw new ProjectsConfigApiException(
                "invalid_authorization_context",
                "Projects & Config returned an invalid authorization-context contract.");
        if (caller.TenantSelector is not null &&
            !string.Equals(caller.TenantSelector, context.TenantId, StringComparison.Ordinal))
            throw new ProjectsConfigApiException(
                "tenant_selector_mismatch",
                "Projects & Config did not authorize the checked tenant selector.");
        return context;
    }

    public async Task<EffectiveNetworkPolicySelection> GetRunSelectionAsync(
        CurrentCallerRequest caller,
        string projectId,
        string runId,
        CancellationToken cancellationToken)
    {
        ValidateOpaqueId(projectId, nameof(projectId));
        ValidateOpaqueId(runId, nameof(runId));
        var path = $"/api/projects/{Uri.EscapeDataString(projectId)}/runs/{Uri.EscapeDataString(runId)}/selection";
        using var response = await SendGetAsync(caller, path, cancellationToken).ConfigureAwait(false);
        RequireNoStore(response);
        var selection = await ReadJsonAsync<EffectiveNetworkPolicySelection>(response, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(selection.ProjectId, projectId, StringComparison.Ordinal) ||
            !string.Equals(selection.RunId, runId, StringComparison.Ordinal))
            throw new ProjectsConfigApiException(
                "run_selection_mismatch",
                "Projects & Config returned a selection for a different project or run.");
        if (selection.EgressBaseline.IsDefault ||
            selection.ProjectEgressNarrowing is { IsDefault: true } ||
            selection.RequiredEgress.IsDefault ||
            selection.EgressAllowlist.IsDefault ||
            selection.Providers.IsDefault)
            throw new ProjectsConfigApiException(
                "invalid_run_selection",
                "Projects & Config returned a malformed project/run selection.");
        return selection;
    }

    private async Task<HttpResponseMessage> SendGetAsync(
        CurrentCallerRequest caller,
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", caller.BearerToken);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        if (caller.TenantSelector is not null)
            request.Headers.TryAddWithoutValidation(
                Agentweaver.Abstractions.ProjectAuthorizationContextContract.TenantSelectorHeader,
                caller.TenantSelector);
        var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var statusCode = (int)response.StatusCode;
            response.Dispose();
            var failureCode = statusCode is 401 or 403
                ? path == "/api/authorization/context"
                    ? "authorization_context_denied"
                    : path.EndsWith("/selection", StringComparison.Ordinal)
                        ? "run_selection_not_authorized"
                        : "projects_config_request_failed"
                : "projects_config_request_failed";
            throw new ProjectsConfigApiException(
                failureCode,
                $"Projects & Config returned HTTP {statusCode}.");
        }
        return response;
    }

    private static async Task<T> ReadJsonAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken) where T : class
    {
        T? value;
        try
        {
            value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            throw new ProjectsConfigApiException(
                "invalid_projects_config_response",
                "Projects & Config returned a malformed JSON response.");
        }
        return value ?? throw new ProjectsConfigApiException(
            "invalid_projects_config_response",
            "Projects & Config returned an empty response.");
    }

    private static void RequireNoStore(HttpResponseMessage response)
    {
        if (response.Headers.CacheControl?.NoStore != true)
            throw new ProjectsConfigApiException(
                "cache_policy_missing",
                "Projects & Config authorization and run-selection responses must be no-store.");
    }

    private static void ValidateOpaqueId(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '_' or '-' or ':')))
            throw new ProjectsConfigApiException(
                "invalid_resource_id",
                $"The {name} is invalid.");
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(
            System.Text.Json.JsonNamingPolicy.CamelCase));
        return options;
    }
}
