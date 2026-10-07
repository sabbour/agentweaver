using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.EventsAndSessions;

public sealed record CoordinationOwnerClientOptions(
    string? OwnerBaseAddress,
    string? Audience,
    string? ExpectedIssuer);

public interface ICoordinationOwnerClient
{
    Task<MessageRouteBinding> ValidateMessageRouteAsync(
        HttpContext context,
        string projectId,
        string runId,
        MessageRouteValidationRequest request,
        CancellationToken cancellationToken = default);

    Task<CoordinationSessionBinding> GetSessionBindingAsync(
        HttpContext context,
        string projectId,
        string runId,
        string sessionId,
        CancellationToken cancellationToken = default);

    Task<SessionForkAdmissionReceipt> ValidateSessionForkAdmissionAsync(
        HttpContext context,
        SessionIdentity source,
        SessionForkRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class CoordinationOwnerClientException(
    string code,
    int statusCode,
    Exception? innerException = null) : Exception(code, innerException)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public sealed class CoordinationOwnerClient(
    HttpClient httpClient,
    CoordinationOwnerClientOptions options) : ICoordinationOwnerClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    public Task<MessageRouteBinding> ValidateMessageRouteAsync(
        HttpContext context,
        string projectId,
        string runId,
        MessageRouteValidationRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync<MessageRouteBinding>(
            context,
            HttpMethod.Post,
            $"/internal/projects/{Uri.EscapeDataString(projectId)}/runs/{Uri.EscapeDataString(runId)}/coordination/message-route",
            request,
            cancellationToken);

    public Task<CoordinationSessionBinding> GetSessionBindingAsync(
        HttpContext context,
        string projectId,
        string runId,
        string sessionId,
        CancellationToken cancellationToken = default) =>
        SendAsync<CoordinationSessionBinding>(
            context,
            HttpMethod.Get,
            $"/internal/projects/{Uri.EscapeDataString(projectId)}/runs/{Uri.EscapeDataString(runId)}/coordination/sessions/{Uri.EscapeDataString(sessionId)}/owner-binding",
            content: null,
            cancellationToken);

    public async Task<SessionForkAdmissionReceipt> ValidateSessionForkAdmissionAsync(
        HttpContext context,
        SessionIdentity source,
        SessionForkRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var receipt = await SendAsync<SessionForkAdmissionReceipt>(
            context,
            HttpMethod.Post,
            $"/internal/projects/{Uri.EscapeDataString(source.ProjectId)}/runs/{Uri.EscapeDataString(source.RunId)}/coordination/sessions/{Uri.EscapeDataString(source.SessionId)}/fork-admission",
            request,
            cancellationToken,
            conflictIsDenied: true).ConfigureAwait(false);
        var issuers = context.User.FindAll("iss").Take(2).ToArray();
        var subjects = context.User.FindAll("sub").Take(2).ToArray();
        if (issuers.Length != 1 || subjects.Length != 1 ||
            string.IsNullOrWhiteSpace(receipt.AcceptedSelectionHash) ||
            receipt.CommandId == Guid.Empty ||
            receipt.Source != source ||
            receipt.TargetSessionId != request.TargetSessionId ||
            receipt.SourceEventId != request.SourceEventId ||
            !string.Equals(receipt.SourceCursor, request.SourceCursor, StringComparison.Ordinal) ||
            !string.Equals(receipt.IdempotencyKey, request.IdempotencyKey, StringComparison.Ordinal) ||
            receipt.ExecutionFence < 1 ||
            !string.Equals(receipt.ActorIssuer, issuers[0].Value, StringComparison.Ordinal) ||
            !string.Equals(receipt.ActorSubject, subjects[0].Value, StringComparison.Ordinal) ||
            receipt.AcceptedSelectionHash.Length != 64 ||
            !receipt.AcceptedSelectionHash.All(Uri.IsHexDigit))
            throw new CoordinationOwnerClientException(
                "coordination_owner_contract_invalid", StatusCodes.Status502BadGateway);
        return receipt;
    }

    private async Task<T> SendAsync<T>(
        HttpContext context,
        HttpMethod method,
        string path,
        object? content,
        CancellationToken cancellationToken,
        bool conflictIsDenied = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        var owner = RequireOwnerUri();
        var authorization = RequireBearer(context);
        if (!context.User.FindAll("aud").Any(claim =>
                string.Equals(claim.Value, options.Audience, StringComparison.Ordinal)) ||
            !context.User.FindAll("iss").Any(claim =>
                string.Equals(claim.Value, options.ExpectedIssuer, StringComparison.Ordinal)))
            throw new CoordinationOwnerClientException(
                "coordination_owner_caller_mismatch", StatusCodes.Status403Forbidden);

        using var request = new HttpRequestMessage(method, new Uri(owner, path));
        request.Headers.Authorization = authorization;
        var tenant = ReadTenantSelector(context);
        if (tenant is not null)
            request.Headers.TryAddWithoutValidation("X-Agentweaver-Tenant", tenant);
        if (content is not null)
            request.Content = JsonContent.Create(content, options: JsonOptions);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw new CoordinationOwnerClientException(
                "coordination_owner_unavailable", StatusCodes.Status502BadGateway, exception);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new CoordinationOwnerClientException(
                    "coordination_owner_denied", StatusCodes.Status403Forbidden);
            if (conflictIsDenied && response.StatusCode == HttpStatusCode.Conflict)
                throw new CoordinationOwnerClientException(
                    "coordination_owner_admission_denied", StatusCodes.Status409Conflict);
            if (response.StatusCode != HttpStatusCode.OK ||
                response.Headers.CacheControl?.NoStore != true)
                throw new CoordinationOwnerClientException(
                    "coordination_owner_unavailable", StatusCodes.Status502BadGateway);
            try
            {
                return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new CoordinationOwnerClientException(
                        "coordination_owner_contract_invalid", StatusCodes.Status502BadGateway);
            }
            catch (JsonException exception)
            {
                throw new CoordinationOwnerClientException(
                    "coordination_owner_contract_invalid", StatusCodes.Status502BadGateway, exception);
            }
        }
    }

    private Uri RequireOwnerUri()
    {
        if (!Uri.TryCreate(options.OwnerBaseAddress, UriKind.Absolute, out var owner) ||
            owner.Scheme != Uri.UriSchemeHttps || owner.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(owner.Query) || !string.IsNullOrEmpty(owner.Fragment) ||
            !string.IsNullOrEmpty(owner.UserInfo) ||
            string.IsNullOrWhiteSpace(options.Audience) ||
            !Uri.TryCreate(options.ExpectedIssuer, UriKind.Absolute, out var issuer) ||
            issuer.Scheme != Uri.UriSchemeHttps)
            throw new CoordinationOwnerClientException(
                "coordination_owner_configuration_invalid", StatusCodes.Status503ServiceUnavailable);
        return owner;
    }

    private static AuthenticationHeaderValue RequireBearer(HttpContext context)
    {
        var values = context.Request.Headers.Authorization;
        if (values.Count != 1 ||
            !AuthenticationHeaderValue.TryParse(values[0], out var authorization) ||
            !string.Equals(authorization.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(authorization.Parameter))
            throw new CoordinationOwnerClientException(
                "caller_bearer_invalid", StatusCodes.Status401Unauthorized);
        return authorization;
    }

    private static string? ReadTenantSelector(HttpContext context)
    {
        var selectors = context.Request.Headers["X-Agentweaver-Tenant"];
        if (selectors.Count == 0)
            return null;
        if (selectors.Count != 1 || string.IsNullOrWhiteSpace(selectors[0]) ||
            selectors[0]!.Length > 256 ||
            selectors[0]!.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '_' or '-' or ':')))
            throw new CoordinationOwnerClientException(
                "tenant_selector_invalid", StatusCodes.Status403Forbidden);
        return selectors[0]!;
    }
}
