using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;

namespace Agentweaver.Gateway;

public sealed partial class GatewayOwnerClient
{
    internal async Task ProxyAuthorizationTargetContextAsync(
        HttpContext context, GatewayRoute route, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        try
        {
            if (context.Request.Query.Count != 0 ||
                context.Request.ContentLength is > 0 ||
                context.Request.Headers.ContainsKey("Transfer-Encoding") ||
                context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true)
            {
                await WriteTargetErrorAsync(context, 400, "request_shape_invalid").ConfigureAwait(false);
                return;
            }

            var bearer = await RequireValidatedBearerAsync(context).ConfigureAwait(false);
            var scopes = context.User.FindAll("scope").Concat(context.User.FindAll("scp"))
                .SelectMany(claim => claim.Value.Split([' ', '\t', '\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries));
            if (!scopes.Contains("api.read", StringComparer.Ordinal) ||
                context.User.HasClaim(claim => claim.Type is "purpose" or "project_id" or "run_id"))
            {
                await WriteTargetErrorAsync(context, 403, "target_context_forbidden").ConfigureAwait(false);
                return;
            }
            var selector = ReadTenantSelector(context);
            if (selector is not null && !IsTargetIdentity(selector))
            {
                await WriteTargetErrorAsync(context, 403, "target_context_forbidden").ConfigureAwait(false);
                return;
            }
            using var timeout = CreateOwnerTimeout(cancellationToken);
            using var request = CreateOwnerRequest(context, route, bearer, out _);
            using var response = await SendAsync(GatewayOwner.Projects, request, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                var (status, code) = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => (401, "authentication_required"),
                    HttpStatusCode.Forbidden => (403, "target_context_forbidden"),
                    HttpStatusCode.ServiceUnavailable => (503, "target_context_unavailable"),
                    _ when IsRedirect(response.StatusCode) => (502, "owner_contract_invalid"),
                    _ => (502, "owner_unavailable"),
                };
                await WriteTargetErrorAsync(context, status, code).ConfigureAwait(false);
                return;
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (response.Headers.CacheControl?.NoStore != true ||
                mediaType is null ||
                !(mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
                  mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)))
            {
                await WriteTargetErrorAsync(context, 502, "owner_contract_invalid").ConfigureAwait(false);
                return;
            }
            var body = await ReadBoundedAsync(response.Content, 64 * 1024, timeout.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
            bool valid;
            try
            {
                valid = IsValidTargetContext(document.RootElement, context, selector);
            }
            catch (InvalidOperationException)
            {
                valid = false;
            }
            if (!valid)
            {
                await WriteTargetErrorAsync(context, 502, "owner_contract_invalid").ConfigureAwait(false);
                return;
            }
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength = body.Length;
            await context.Response.Body.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        }
        catch (GatewayOwnerFailureException exception)
        {
            if (exception.StatusCode == 401)
                await WriteTargetErrorAsync(context, 401, "authentication_required").ConfigureAwait(false);
            else
                await WriteTargetErrorAsync(context, 403, "target_context_forbidden").ConfigureAwait(false);
        }
        catch (JsonException)
        {
            await WriteTargetErrorAsync(context, 502, "owner_contract_invalid").ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            await WriteTargetErrorAsync(context, 502, "owner_contract_invalid").ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            await WriteTargetErrorAsync(context, 504, "owner_timeout").ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            await WriteTargetErrorAsync(context, 502, "owner_unavailable").ConfigureAwait(false);
        }
        catch (IOException)
        {
            await WriteTargetErrorAsync(context, 502, "owner_unavailable").ConfigureAwait(false);
        }
    }

    private bool IsValidTargetContext(JsonElement root, HttpContext context, string? selector)
    {
        if (!HasExactPropertyNames(root, ["contractVersion", "issuer", "actorId", "tenantId",
                "membershipRevision", "boundProjectId", "boundRunId", "effectiveAuthority", "installationId"]) ||
            root.GetProperty("contractVersion").ValueKind != JsonValueKind.Number ||
            !root.GetProperty("contractVersion").TryGetInt32(out var version) || version != 1 ||
            !PositiveInteger(root.GetProperty("membershipRevision")) ||
            root.GetProperty("boundProjectId").ValueKind != JsonValueKind.Null ||
            root.GetProperty("boundRunId").ValueKind != JsonValueKind.Null ||
            !Nonempty(root.GetProperty("issuer"), out var issuer) ||
            !Nonempty(root.GetProperty("actorId"), out var actor) ||
            !Nonempty(root.GetProperty("tenantId"), out var tenant) ||
            !IsTargetIdentity(actor!) || !IsTargetIdentity(tenant!) ||
            !Nonempty(root.GetProperty("installationId"), out var installation) ||
            !Guid.TryParseExact(installation, "D", out var id) || id == Guid.Empty ||
            installation != id.ToString("D") ||
            issuer != options.IdentityIssuer.AbsoluteUri ||
            root.GetProperty("effectiveAuthority").ValueKind != JsonValueKind.Array)
            return false;

        var subjects = context.User.FindAll("sub").Take(2).ToArray();
        var assertions = context.User.FindAll("tenant_id").Concat(context.User.FindAll("tid"))
            .Concat(context.User.FindAll("http://schemas.microsoft.com/identity/claims/tenantid")).Take(2).ToArray();
        if (subjects.Length != 1 || subjects[0].Value != actor ||
            (selector is not null && selector != tenant) ||
            assertions.Length > 1 || assertions.Any(item => item.Value != tenant))
            return false;
        foreach (var grant in root.GetProperty("effectiveAuthority").EnumerateArray())
        {
            if (!HasExactPropertyNames(grant, ["resourceType", "resourceId", "permissions"]) ||
                !Nonempty(grant.GetProperty("resourceType"), out var resourceType) ||
                resourceType is not ("platform" or "tenant" or "project") ||
                !Nonempty(grant.GetProperty("resourceId"), out var resourceId) ||
                !IsTargetResourceId(resourceId!) ||
                grant.GetProperty("permissions").ValueKind != JsonValueKind.Array)
                return false;
            foreach (var permission in grant.GetProperty("permissions").EnumerateArray())
            {
                if (!HasExactPropertyNames(permission, ["permission", "roleRevision"]) ||
                    !Nonempty(permission.GetProperty("permission"), out var name) ||
                    name is not ("readProjects" or "writeProjects" or "createProjects" or
                        "readRunSelection" or "acceptRunSelection" or "accessPrivateKnowledge" or
                        "readPlatformRuntimeDefaults" or "writePlatformRuntimeDefaults" or "cancelRun") ||
                    !PositiveInteger(permission.GetProperty("roleRevision")))
                    return false;
            }
        }
        return true;
    }

    private static bool IsTargetIdentity(string value) =>
        value.Length is > 0 and <= 256 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':');

    private static bool IsTargetResourceId(string value) =>
        value.Length is > 0 and <= 512 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':' or '/');

    private static bool PositiveInteger(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number > 0;

    private static bool Nonempty(JsonElement value, out string? text)
    {
        text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return !string.IsNullOrWhiteSpace(text);
    }

    private static Task WriteTargetErrorAsync(HttpContext context, int status, string code)
    {
        if (status == 401)
            context.Response.Headers.WWWAuthenticate = "Bearer";
        return WriteOwnerFailureAsync(context,
            new GatewayOwnerFailureException(status, "application/problem+json", ProblemBody(code, status)));
    }
}
