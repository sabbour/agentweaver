using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using OpenIddict.Abstractions;

namespace Agentweaver.Gateway;

/// <summary>Stores a bounded owner failure response for pass-through to the public caller.</summary>
internal sealed class GatewayOwnerFailureException(
    int statusCode,
    string contentType,
    byte[] body) : Exception("An owner API request failed.")
{
    public int StatusCode { get; } = statusCode;
    public string ContentType { get; } = contentType;
    public byte[] Body { get; } = body;
}

/// <summary>Forwards validated requests to owners with bounded bodies, deadlines, and contract checks.</summary>
public sealed partial class GatewayOwnerClient(
    IHttpClientFactory clients,
    GatewayOptions options)
{
    private const long MaximumProxyResponseBytes = 8 * 1024 * 1024;

    private static async Task<bool> HasP1SetupBodyAsync(
        HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.HttpContext.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody != true)
            return false;

        var probe = new byte[1];
        return await request.Body.ReadAsync(probe, cancellationToken).ConfigureAwait(false) > 0;
    }
    private const long MaximumOwnerErrorBytes = 64 * 1024;
    private const long MaximumRunEventPageBytes = 1024 * 1024;
    private const int MaximumSseCursorLength = 8192;
    private const string TenantSelectorHeader = ProjectAuthorizationContextContract.TenantSelectorHeader;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 16,
    };
    private static readonly JsonSerializerOptions AuthorizationContextJsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            MaxDepth = 16,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
        };

    /// <summary>Proxies one catalog route while preserving owner responses and validating event pages.</summary>
    public async Task ProxyAsync(
        HttpContext context,
        GatewayRoute route,
        CancellationToken cancellationToken)
    {
        try
        {
            if (route.OperationId is "createP1InvocationSetup" or "readP1InvocationSetup")
                context.Response.Headers.CacheControl = "no-store";
            var bearer = await RequireValidatedBearerAsync(context).ConfigureAwait(false);
            using var timeout = CreateOwnerTimeout(cancellationToken);
            if (route.OperationId is ("createP1InvocationSetup" or "readP1InvocationSetup") &&
                (context.Request.ContentLength is > 0 ||
                 context.Request.Headers.ContainsKey("Transfer-Encoding") ||
                 context.Request.QueryString.HasValue ||
                 await HasP1SetupBodyAsync(context.Request, timeout.Token).ConfigureAwait(false)))
            {
                await WriteProblemAsync(context, "invalid_project_setup_request", StatusCodes.Status400BadRequest)
                    .ConfigureAwait(false);
                return;
            }
            using var boundedBody = await BufferBoundedRequestAsync(context, route, timeout.Token).ConfigureAwait(false);
            using var request = CreateOwnerRequest(context, route, bearer, boundedBody);
            using var response = await SendAsync(route.Owner, request, timeout.Token).ConfigureAwait(false);
            if (IsRedirect(response.StatusCode))
            {
                await WriteProblemAsync(context, "owner_redirect_rejected", StatusCodes.Status502BadGateway)
                    .ConfigureAwait(false);
                return;
            }
            if (route.Owner == GatewayOwner.Events &&
                route.OperationId == "replayRunEvents" &&
                route.Method == "GET" &&
                HttpMethods.IsGet(context.Request.Method) &&
                response.IsSuccessStatusCode)
            {
                if (response.StatusCode != HttpStatusCode.OK ||
                    response.Content.Headers.ContentType?.MediaType is not { } mediaType ||
                    !mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase))
                    throw OwnerContractFailure("events_page_contract_invalid");

                var cursorValues = context.Request.Query["cursor"];
                var limitValues = context.Request.Query["limit"];
                if (cursorValues.Count > 1 || limitValues.Count > 1)
                    throw OwnerContractFailure("events_page_contract_invalid");
                var cursor = cursorValues.FirstOrDefault();
                var limitText = limitValues.FirstOrDefault();
                var limit = limitText is null
                    ? 100
                    : int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedLimit)
                        ? parsedLimit
                        : 0;
                if (limit is < 1 or > 200 || cursor is not null && !IsSafeSseValue(cursor))
                    throw OwnerContractFailure("events_page_contract_invalid");

                var ownerBody = await ReadBoundedAsync(
                    response.Content, MaximumRunEventPageBytes, timeout.Token).ConfigureAwait(false);
                byte[] projectedBody;
                try
                {
                    projectedBody = GatewayRunEventProjection.ProjectPage(ownerBody, cursor, limit);
                }
                catch (GatewayEventProjectionException exception)
                {
                    throw OwnerContractFailure(exception.Code);
                }

                context.Response.StatusCode = (int)response.StatusCode;
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.XContentTypeOptions = "nosniff";
                CopyResponseHeaders(context, response, route.Owner, route.ForwardSetCookie);
                context.Response.ContentType = response.Content.Headers.ContentType!.ToString();
                context.Response.ContentLength = projectedBody.Length;
                await context.Response.Body.WriteAsync(projectedBody, timeout.Token).ConfigureAwait(false);
                return;
            }
            if (response.Content.Headers.ContentLength is { } ownerLength && ownerLength > MaximumResponseBytes(route))
            {
                await WriteProblemAsync(context, "owner_response_too_large", StatusCodes.Status502BadGateway)
                    .ConfigureAwait(false);
                return;
            }

            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            CopyResponseHeaders(context, response, route.Owner, route.ForwardSetCookie);
            CopyExposedResponseHeaders(context, response, route);
            if (response.Content.Headers.ContentType is { } contentType)
                context.Response.ContentType = contentType.ToString();
            if (response.Content.Headers.ContentLength is { } contentLength)
                context.Response.ContentLength = contentLength;

            if (!HttpMethods.IsHead(context.Request.Method) &&
                response.StatusCode is not HttpStatusCode.NoContent and not HttpStatusCode.NotModified)
                await CopyBoundedAsync(
                    await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false),
                    context.Response.Body,
                    MaximumResponseBytes(route),
                    timeout.Token).ConfigureAwait(false);
        }
        catch (GatewayOwnerFailureException exception)
        {
            await WriteOwnerFailureAsync(context, exception).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await WriteProblemAsync(context, "owner_timeout", StatusCodes.Status504GatewayTimeout)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            await WriteProblemAsync(context, "owner_unavailable", StatusCodes.Status502BadGateway)
                .ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            if (context.Response.HasStarted)
                context.Abort();
            else
                await WriteProblemAsync(context, "owner_response_too_large", StatusCodes.Status502BadGateway)
                    .ConfigureAwait(false);
        }
        catch (IOException)
        {
            await WriteProblemAsync(context, "owner_unavailable", StatusCodes.Status502BadGateway)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Stages an AI proposal through Projects and executes it only in its accepted run.</summary>
    public async Task ProxyCastingGenerationAsync(
        HttpContext context,
        GatewayRoute route,
        CancellationToken cancellationToken)
    {
        try
        {
            if (route.OperationId is not ("formulateCastingProposal" or "analyzeCastingProposal") ||
                context.Request.Query.Count != 0)
                throw CastingGenerationRequestFailure("casting_generation_request_invalid");

            var runBearer = await RequireValidatedBearerAsync(context).ConfigureAwait(false);
            var projectBearer = RequireProjectAuthorizationBearer(context);
            var tenantSelector = ReadTenantSelector(context);
            using var timeout = CreateOwnerTimeout(cancellationToken);
            using var requestBody = await BufferBoundedRequestAsync(context, route, timeout.Token).ConfigureAwait(false);
            RequireJsonRequestContentType(context);
            var input = ParseCastingGenerationRequest(route.OperationId, requestBody);
            var mode = route.OperationId == "formulateCastingProposal" ? "formulate" : "analyze";
            GatewayCastingGenerationContract.ValidateRequest(
                input.ExpectedConfigurationRevision,
                input.RunId,
                input.SessionId,
                input.IdempotencyKey,
                input.Prompt,
                input.TeamSize,
                input.Intent,
                input.PreferredRoleIds,
                input.Universe,
                input.Goal,
                requireGoal: mode == "formulate");

            // The owner-issued contexts prove the project and run bearers have the same actor and tenant.
            var projectAuthorization = await ReadCastingAuthorizationContextAsync(
                context, projectBearer, tenantSelector, timeout.Token).ConfigureAwait(false);
            var runAuthorization = await ReadCastingAuthorizationContextAsync(
                context, runBearer, tenantSelector, timeout.Token).ConfigureAwait(false);
            ValidateCastingGenerationAuthorities(
                context,
                input,
                projectAuthorization,
                runAuthorization,
                tenantSelector);

            var projectId = context.Request.RouteValues["projectId"]?.ToString()
                ?? throw CastingGenerationRequestFailure("casting_generation_request_invalid");
            var current = await ReadCastingProjectConfigurationAsync(
                context,
                projectBearer,
                projectId,
                input.ExpectedConfigurationRevision,
                timeout.Token).ConfigureAwait(false);
            var roles = await ReadCastingRoleCatalogAsync(
                context, projectBearer, timeout.Token).ConfigureAwait(false);
            var availableRoleIds = roles.Select(role => role.Id).ToImmutableHashSet(StringComparer.Ordinal);
            if (input.PreferredRoleIds.Any(roleId => !availableRoleIds.Contains(roleId)) ||
                input.Intent == "augment" && current.Casting.IsEmpty)
                throw CastingGenerationRequestFailure("casting_generation_request_invalid");

            var universes = await ReadCastingUniversesAsync(
                context, projectBearer, projectId, timeout.Token).ConfigureAwait(false);
            if (input.Universe is not null &&
                !universes.Contains(input.Universe, StringComparer.OrdinalIgnoreCase))
                throw CastingGenerationRequestFailure("unknown_casting_universe");

            var existingRoleIds = current.Casting
                .OrderBy(item => item.Order)
                .Select(item => item.Role)
                .Distinct(StringComparer.Ordinal)
                .ToImmutableArray();
            var expectedPrompt = GatewayCastingGenerationContract.BuildPrompt(
                mode,
                input.TeamSize,
                input.Intent,
                input.PreferredRoleIds,
                input.Universe,
                input.Goal,
                existingRoleIds,
                roles);
            if (!string.Equals(expectedPrompt, input.Prompt, StringComparison.Ordinal))
                throw CastingGenerationRequestFailure("casting_generation_prompt_mismatch");

            var requestDigest = ComputeCastingGenerationDigest(mode, input);
            var startBody = JsonSerializer.SerializeToUtf8Bytes(new
            {
                expectedConfigurationRevision = input.ExpectedConfigurationRevision,
                mode,
                runId = input.RunId,
                idempotencyKey = input.IdempotencyKey,
                requestDigest,
            }, JsonOptions);
            var startBytes = await SendCastingOwnerJsonAsync(
                context,
                GatewayOwner.Projects,
                HttpMethod.Post,
                $"/api/projects/{Uri.EscapeDataString(projectId)}/casting/proposal-generations",
                projectBearer,
                startBody,
                HttpStatusCode.Created,
                timeout.Token).ConfigureAwait(false);
            var generation = ParseCastingGenerationRequest(startBytes);
            if (generation.ProjectId != projectId ||
                generation.RunId != input.RunId ||
                generation.BaseConfigurationRevision != input.ExpectedConfigurationRevision ||
                generation.Mode != mode)
                throw OwnerContractFailure("casting_generation_owner_contract_invalid");

            // Replaying a completed generation returns its stored proposal instead of running the model again.
            if (generation.State == "completed")
            {
                await WriteCastingProposalAsync(
                    context, projectBearer, projectId, generation, mode, false, timeout.Token)
                    .ConfigureAwait(false);
                return;
            }
            if (generation.State != "pending")
                throw CastingGenerationConflict("casting_generation_not_pending");

            // Replays use the same owner request; a timeout or lost response must not make it terminally failed.
            var executionBody = JsonSerializer.SerializeToUtf8Bytes(
                new { requestId = generation.RequestId, prompt = input.Prompt },
                JsonOptions);
            var executionBytes = await SendCastingOwnerJsonAsync(
                context,
                GatewayOwner.Orchestrator,
                HttpMethod.Post,
                $"/api/projects/{Uri.EscapeDataString(projectId)}/runs/{Uri.EscapeDataString(input.RunId)}/coordination/sessions/{Uri.EscapeDataString(input.SessionId)}/casting-generation",
                runBearer,
                executionBody,
                HttpStatusCode.OK,
                timeout.Token).ConfigureAwait(false);
            var execution = ParseCastingGenerationExecutionResponse(executionBytes);
            if (execution.RequestId != generation.RequestId || execution.RunId != input.RunId)
                throw OwnerContractFailure("casting_generation_owner_contract_invalid");

            var selectableRoleIds = roles
                .Where(role => !GatewayCastingGenerationContract.IsReservedRole(role.Id))
                .Select(role => role.Id)
                .Where(roleId => input.Intent != "augment" || !existingRoleIds.Contains(roleId, StringComparer.Ordinal))
                .ToImmutableHashSet(StringComparer.Ordinal);
            GatewayCastingGenerationModelOutput modelOutput;
            try
            {
                modelOutput = GatewayCastingGenerationContract.ParseModelOutput(
                    execution.Output, input.TeamSize, selectableRoleIds);
            }
            catch (GatewayOwnerFailureException)
            {
                // A received but invalid model result is definitive; transport failures remain replayable.
                await MarkCastingGenerationFailedAsync(
                    context, projectBearer, projectId, generation, timeout.Token).ConfigureAwait(false);
                throw;
            }

            var proposedTeam = BuildCastingProposalTeam(
                current,
                roles,
                modelOutput.RoleIds,
                input.Intent);
            var completeBody = JsonSerializer.SerializeToUtf8Bytes(new
            {
                runId = input.RunId,
                agentCharters = proposedTeam.AgentCharters,
                casting = proposedTeam.Casting,
                castingUniverse = input.Universe,
            }, JsonOptions);
            var completeBytes = await SendCastingOwnerJsonAsync(
                context,
                GatewayOwner.Projects,
                HttpMethod.Post,
                $"/api/projects/{Uri.EscapeDataString(projectId)}/casting/proposal-generations/{generation.RequestId:D}/complete",
                projectBearer,
                completeBody,
                HttpStatusCode.OK,
                timeout.Token).ConfigureAwait(false);
            var completed = ParseCastingGenerationRequest(completeBytes);
            if (completed.State == "stale")
                throw CastingGenerationConflict("casting_generation_stale");
            if (completed.State != "completed")
                throw OwnerContractFailure("casting_generation_owner_contract_invalid");

            await WriteCastingProposalAsync(
                context, projectBearer, projectId, completed, mode, true, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (GatewayOwnerFailureException exception)
        {
            await WriteOwnerFailureAsync(context, exception).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            await WriteProblemAsync(context, "owner_contract_invalid", StatusCodes.Status502BadGateway)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            await WriteProblemAsync(context, "owner_timeout", StatusCodes.Status504GatewayTimeout)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            await WriteProblemAsync(context, "owner_unavailable", StatusCodes.Status502BadGateway)
                .ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            await WriteProblemAsync(context, "owner_response_too_large", StatusCodes.Status502BadGateway)
                .ConfigureAwait(false);
        }
        catch (IOException)
        {
            await WriteProblemAsync(context, "owner_unavailable", StatusCodes.Status502BadGateway)
                .ConfigureAwait(false);
        }
    }

    private async Task<ProjectAuthorizationContextResponse> ReadCastingAuthorizationContextAsync(
        HttpContext context,
        AuthenticationHeaderValue bearer,
        string? tenantSelector,
        CancellationToken cancellationToken)
    {
        var body = await SendCastingOwnerJsonAsync(
            context,
            GatewayOwner.Projects,
            HttpMethod.Get,
            "/api/authorization/context",
            bearer,
            null,
            HttpStatusCode.OK,
            cancellationToken,
            tenantSelector).ConfigureAwait(false);
        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
        if (!HasExactAuthorizationContextShape(document.RootElement))
            throw OwnerContractFailure("projects_authorization_context_invalid");
        return document.RootElement.Deserialize<ProjectAuthorizationContextResponse>(
                AuthorizationContextJsonOptions)
            ?? throw OwnerContractFailure("projects_authorization_context_invalid");
    }

    private void ValidateCastingGenerationAuthorities(
        HttpContext context,
        CastingGenerationInput input,
        ProjectAuthorizationContextResponse projectAuthorization,
        ProjectAuthorizationContextResponse runAuthorization,
        string? tenantSelector)
    {
        // Require one current writer identity while keeping the generation credential bound to this exact run.
        var projectId = context.Request.RouteValues["projectId"]?.ToString();
        var runIsValid = IsValidAuthorizationContext(runAuthorization, context.User, tenantSelector) &&
            runAuthorization.BoundProjectId == projectId &&
            runAuthorization.BoundRunId == input.RunId;
        var projectHasWriteAuthority = projectAuthorization.EffectiveAuthority.Any(grant =>
            grant.ResourceType == ProjectAuthorityResourceType.Project &&
            grant.ResourceId == projectId &&
            grant.Permissions.Any(permission =>
                permission.Permission == ProjectAuthorizationPermission.WriteProjects));
        if (!runIsValid ||
            projectAuthorization.BoundProjectId is not null ||
            projectAuthorization.BoundRunId is not null ||
            !string.Equals(projectAuthorization.Issuer, runAuthorization.Issuer, StringComparison.Ordinal) ||
            !string.Equals(projectAuthorization.ActorId, runAuthorization.ActorId, StringComparison.Ordinal) ||
            !string.Equals(projectAuthorization.TenantId, runAuthorization.TenantId, StringComparison.Ordinal) ||
            !projectHasWriteAuthority)
            throw new GatewayOwnerFailureException(
                StatusCodes.Status403Forbidden,
                "application/problem+json",
                ProblemBody("casting_generation_authority_mismatch", StatusCodes.Status403Forbidden));
    }

    private async Task<GatewayCastingProjectConfiguration> ReadCastingProjectConfigurationAsync(
        HttpContext context,
        AuthenticationHeaderValue projectBearer,
        string projectId,
        long revision,
        CancellationToken cancellationToken)
    {
        // Read the revision supplied by the caller so prompt construction cannot silently use a different snapshot.
        var body = await SendCastingOwnerJsonAsync(
            context,
            GatewayOwner.Projects,
            HttpMethod.Get,
            $"/api/projects/{Uri.EscapeDataString(projectId)}/configuration?revision={revision.ToString(CultureInfo.InvariantCulture)}",
            projectBearer,
            null,
            HttpStatusCode.OK,
            cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        if (!HasExactPropertyNames(root, ["projectId", "revision", "configuration", "updatedByActorId", "createdAt"]) ||
            root.GetProperty("projectId").GetString() != projectId ||
            root.GetProperty("revision").GetInt64() != revision)
            throw OwnerContractFailure("projects_configuration_contract_invalid");

        var configuration = root.GetProperty("configuration");
        if (configuration.ValueKind != JsonValueKind.Object ||
            !configuration.TryGetProperty("agentCharters", out var chartersElement) ||
            !configuration.TryGetProperty("casting", out var castingElement) ||
            chartersElement.ValueKind != JsonValueKind.Array ||
            castingElement.ValueKind != JsonValueKind.Array)
            throw OwnerContractFailure("projects_configuration_contract_invalid");

        var charters = JsonSerializer.Deserialize<GatewayCastingAgentCharter[]>(
            chartersElement.GetRawText(), GatewayCastingGenerationContract.StrictJsonOptions);
        var casting = JsonSerializer.Deserialize<GatewayCastingAgentCast[]>(
            castingElement.GetRawText(), GatewayCastingGenerationContract.StrictJsonOptions);
        if (charters is null || casting is null ||
            charters.Any(item => item is null) || casting.Any(item => item is null))
            throw OwnerContractFailure("projects_configuration_contract_invalid");
        return new(charters.ToImmutableArray(), casting.ToImmutableArray());
    }

    private async Task<ImmutableArray<GatewayCastingRoleDefinition>> ReadCastingRoleCatalogAsync(
        HttpContext context,
        AuthenticationHeaderValue projectBearer,
        CancellationToken cancellationToken)
    {
        // Validate the complete catalog shape before using role text in a model prompt.
        var body = await SendCastingOwnerJsonAsync(
            context,
            GatewayOwner.Projects,
            HttpMethod.Get,
            "/api/catalog/roles",
            projectBearer,
            null,
            HttpStatusCode.OK,
            cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw OwnerContractFailure("projects_casting_catalog_invalid");
        foreach (var role in document.RootElement.EnumerateArray())
        {
            if (!HasExactPropertyNames(role, ["id", "title", "summary", "responsibilities", "boundaries"]))
                throw OwnerContractFailure("projects_casting_catalog_invalid");
        }
        var roles = JsonSerializer.Deserialize<GatewayCastingRoleDefinition[]>(
            body, GatewayCastingGenerationContract.StrictJsonOptions);
        if (roles is null ||
            roles.Any(role =>
                role is null ||
                string.IsNullOrWhiteSpace(role.Id) ||
                string.IsNullOrWhiteSpace(role.Title) ||
                role.Responsibilities.IsDefault ||
                role.Boundaries.IsDefault) ||
            roles.Select(role => role.Id).Distinct(StringComparer.Ordinal).Count() != roles.Length)
            throw OwnerContractFailure("projects_casting_catalog_invalid");
        // Reserved orchestration roles stay outside both the model menu and its accepted output.
        return roles.Where(role => !GatewayCastingGenerationContract.IsReservedRole(role.Id)).ToImmutableArray();
    }

    private async Task<ImmutableArray<string>> ReadCastingUniversesAsync(
        HttpContext context,
        AuthenticationHeaderValue projectBearer,
        string projectId,
        CancellationToken cancellationToken)
    {
        // Treat the owner list as authoritative and reject malformed entries rather than inventing options.
        var body = await SendCastingOwnerJsonAsync(
            context,
            GatewayOwner.Projects,
            HttpMethod.Get,
            $"/api/projects/{Uri.EscapeDataString(projectId)}/casting/universes",
            projectBearer,
            null,
            HttpStatusCode.OK,
            cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
        if (!HasExactPropertyNames(document.RootElement, ["universes"]) ||
            document.RootElement.GetProperty("universes").ValueKind != JsonValueKind.Array)
            throw OwnerContractFailure("projects_casting_universes_invalid");
        var universes = document.RootElement.GetProperty("universes").EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String
                ? item.GetString()
                : throw OwnerContractFailure("projects_casting_universes_invalid"))
            .ToArray();
        if (universes.Any(string.IsNullOrWhiteSpace))
            throw OwnerContractFailure("projects_casting_universes_invalid");
        return universes.Select(item => item!).ToImmutableArray();
    }

    private async Task<byte[]> SendCastingOwnerJsonAsync(
        HttpContext context,
        GatewayOwner owner,
        HttpMethod method,
        string pathAndQuery,
        AuthenticationHeaderValue bearer,
        byte[]? body,
        HttpStatusCode expectedStatus,
        CancellationToken cancellationToken,
        string? tenantSelector = null)
    {
        using var request = new HttpRequestMessage(method, BuildOwnerUri(owner, pathAndQuery));
        request.Headers.Authorization = bearer;
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (tenantSelector is null)
            _ = CopyTenantSelector(context, request);
        else if (!request.Headers.TryAddWithoutValidation(TenantSelectorHeader, tenantSelector))
            throw CastingGenerationRequestFailure("tenant_selector_invalid");
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        using var response = await SendAsync(owner, request, cancellationToken).ConfigureAwait(false);
        if (IsRedirect(response.StatusCode))
            throw OwnerContractFailure("owner_redirect_rejected");
        if (response.StatusCode != expectedStatus)
        {
            if ((int)response.StatusCode is >= 200 and <= 299)
                throw OwnerContractFailure("casting_generation_owner_contract_invalid");
            throw await ToOwnerFailureAsync(response, cancellationToken).ConfigureAwait(false);
        }
        if (response.Headers.CacheControl?.NoStore != true ||
            response.Content.Headers.ContentType?.MediaType is not { } mediaType ||
            !(mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
              mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)))
            throw OwnerContractFailure("casting_generation_owner_contract_invalid");
        return await ReadBoundedAsync(response.Content, MaximumProxyResponseBytes, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task WriteCastingProposalAsync(
        HttpContext context,
        AuthenticationHeaderValue projectBearer,
        string projectId,
        CastingGenerationOwnerRequest generation,
        string mode,
        bool requirePending,
        CancellationToken cancellationToken)
    {
        // Re-read the owner proposal so the Gateway never assembles a client-facing draft from model output itself.
        var body = await SendCastingOwnerJsonAsync(
            context,
            GatewayOwner.Projects,
            HttpMethod.Get,
            $"/api/projects/{Uri.EscapeDataString(projectId)}/casting/proposals/{generation.ProposalId:D}",
            projectBearer,
            body: null,
            HttpStatusCode.OK,
            cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
        var proposal = document.RootElement;
        if (!HasExactPropertyNames(proposal,
                ["proposalId", "projectId", "baseConfigurationRevision", "draftRevision", "state", "draft",
                    "confirmedConfigurationRevision", "result", "transferProvenance", "createdByActorId",
                    "createdAt", "updatedByActorId", "updatedAt", "generation"]) ||
            proposal.GetProperty("proposalId").GetGuid() != generation.ProposalId ||
            proposal.GetProperty("projectId").GetString() != projectId ||
            proposal.GetProperty("baseConfigurationRevision").GetInt64() != generation.BaseConfigurationRevision ||
            proposal.GetProperty("draftRevision").GetInt64() < 1 ||
            proposal.GetProperty("generation").ValueKind != JsonValueKind.Object)
            throw OwnerContractFailure("casting_generation_proposal_contract_invalid");
        if (requirePending && proposal.GetProperty("state").GetString() != "pending")
            throw CastingGenerationConflict("casting_generation_proposal_not_pending");

        var generationView = proposal.GetProperty("generation");
        if (!HasExactPropertyNames(generationView,
                ["requestId", "mode", "state", "failureCode", "createdAt", "updatedAt"]) ||
            generationView.GetProperty("requestId").GetGuid() != generation.RequestId ||
            generationView.GetProperty("mode").GetString() != mode ||
            generationView.GetProperty("state").GetString() != "completed")
            throw OwnerContractFailure("casting_generation_proposal_contract_invalid");

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength = body.Length;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        await context.Response.Body.WriteAsync(body, cancellationToken).ConfigureAwait(false);
    }

    private async Task MarkCastingGenerationFailedAsync(
        HttpContext context,
        AuthenticationHeaderValue projectBearer,
        string projectId,
        CastingGenerationOwnerRequest generation,
        CancellationToken cancellationToken)
    {
        // Persist only a bounded failure code; do not store model output or exception text as proposal metadata.
        if (cancellationToken.IsCancellationRequested)
            return;
        using var timeout = CreateOwnerTimeout(cancellationToken);
        var failureBody = JsonSerializer.SerializeToUtf8Bytes(new
        {
            runId = generation.RunId,
            failureCode = "casting_generation_failed",
        }, JsonOptions);
        var bytes = await SendCastingOwnerJsonAsync(
            context,
            GatewayOwner.Projects,
            HttpMethod.Post,
            $"/api/projects/{Uri.EscapeDataString(projectId)}/casting/proposal-generations/{generation.RequestId:D}/fail",
            projectBearer,
            failureBody,
            HttpStatusCode.OK,
            timeout.Token).ConfigureAwait(false);
        var failed = ParseCastingGenerationRequest(bytes);
        if (failed.State != "failed")
            throw OwnerContractFailure("casting_generation_owner_contract_invalid");
    }

    private static CastingGenerationInput ParseCastingGenerationRequest(
        string operationId,
        MemoryStream? requestBody)
    {
        // Enforce the mode-specific exact field set before deserializing or contacting either owner.
        if (requestBody is null)
            throw CastingGenerationRequestFailure("casting_generation_request_invalid");
        var body = requestBody.ToArray();
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
        }
        catch (JsonException)
        {
            throw CastingGenerationRequestFailure("casting_generation_request_invalid");
        }
        using (document)
        {
        var formulate = operationId == "formulateCastingProposal";
        var required = formulate
            ? new[] { "expectedConfigurationRevision", "runId", "sessionId", "idempotencyKey", "prompt", "goal", "teamSize", "intent", "preferredRoleIds" }
            : new[] { "expectedConfigurationRevision", "runId", "sessionId", "idempotencyKey", "prompt", "teamSize", "intent", "preferredRoleIds" };
        var allowed = required.Append("universe").ToArray();
        if (!HasRequiredAllowedPropertyNames(document.RootElement, required, allowed))
            throw CastingGenerationRequestFailure("casting_generation_request_invalid");

        try
        {
            if (formulate)
            {
                var request = document.RootElement.Deserialize<FormulateCastingProposalRequest>(
                    GatewayCastingGenerationContract.StrictJsonOptions)
                    ?? throw CastingGenerationRequestFailure("casting_generation_request_invalid");
                return new(
                    request.ExpectedConfigurationRevision,
                    request.RunId,
                    request.SessionId,
                    request.IdempotencyKey,
                    request.Prompt,
                    request.Goal,
                    request.TeamSize,
                    request.Intent,
                    request.PreferredRoleIds,
                    request.Universe);
            }
            var analyzeRequest = document.RootElement.Deserialize<AnalyzeCastingProposalRequest>(
                GatewayCastingGenerationContract.StrictJsonOptions)
                ?? throw CastingGenerationRequestFailure("casting_generation_request_invalid");
            return new(
                analyzeRequest.ExpectedConfigurationRevision,
                analyzeRequest.RunId,
                analyzeRequest.SessionId,
                analyzeRequest.IdempotencyKey,
                analyzeRequest.Prompt,
                null,
                analyzeRequest.TeamSize,
                analyzeRequest.Intent,
                analyzeRequest.PreferredRoleIds,
                analyzeRequest.Universe);
        }
        catch (JsonException)
        {
            throw CastingGenerationRequestFailure("casting_generation_request_invalid");
        }
        }
    }

    private static CastingGenerationOwnerRequest ParseCastingGenerationRequest(byte[] body)
    {
        // Owner response fields are exact because they control replay, completion, and proposal identity.
        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
        if (!HasExactPropertyNames(document.RootElement,
                ["requestId", "proposalId", "projectId", "baseConfigurationRevision", "runId", "mode",
                    "state", "failureCode", "createdAt", "updatedAt"]))
            throw OwnerContractFailure("casting_generation_owner_contract_invalid");
        return document.RootElement.Deserialize<CastingGenerationOwnerRequest>(
                GatewayCastingGenerationContract.StrictJsonOptions)
            ?? throw OwnerContractFailure("casting_generation_owner_contract_invalid");
    }

    private static CastingGenerationExecutionOwnerResponse ParseCastingGenerationExecutionResponse(byte[] body)
    {
        // Accept only the response shape that binds generated output to this request and run.
        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
        if (!HasExactPropertyNames(document.RootElement, ["requestId", "runId", "output"]))
            throw OwnerContractFailure("casting_generation_owner_contract_invalid");
        return document.RootElement.Deserialize<CastingGenerationExecutionOwnerResponse>(
                GatewayCastingGenerationContract.StrictJsonOptions)
            ?? throw OwnerContractFailure("casting_generation_owner_contract_invalid");
    }

    private static (ImmutableArray<GatewayCastingAgentCharter> AgentCharters, ImmutableArray<GatewayCastingAgentCast> Casting)
        BuildCastingProposalTeam(
            GatewayCastingProjectConfiguration current,
            ImmutableArray<GatewayCastingRoleDefinition> roles,
            ImmutableArray<string> roleIds,
            string intent)
    {
        // Augment copies the current roster; recast starts empty, and both paths add catalog-backed roles in order.
        var charters = (intent == "augment"
            ? current.AgentCharters
            : ImmutableArray<GatewayCastingAgentCharter>.Empty).ToBuilder();
        var casting = (intent == "augment"
            ? current.Casting
            : ImmutableArray<GatewayCastingAgentCast>.Empty).ToBuilder();
        var takenAgentIds = charters.Select(item => item.AgentId).ToHashSet(StringComparer.Ordinal);
        foreach (var roleId in roleIds)
        {
            // Keep the Projects catalog ID as the role; only the agent ID receives a suffix on collision.
            var role = roles.Single(item => item.Id == roleId);
            var agentId = role.Id;
            for (var suffix = 2; takenAgentIds.Contains(agentId); suffix++)
                agentId = $"{role.Id}-{suffix}";
            takenAgentIds.Add(agentId);
            charters.Add(new GatewayCastingAgentCharter(agentId, role.Title, role.Id, BuildCastingRoleCharter(role)));
            var order = casting.Count == 0 ? 0 : checked(casting.Max(item => item.Order) + 1);
            casting.Add(new GatewayCastingAgentCast(agentId, role.Id, order));
        }
        return (charters.ToImmutable(), casting.ToImmutable());
    }

    private static string BuildCastingRoleCharter(GatewayCastingRoleDefinition role)
    {
        // Use the catalog's reviewed summary, responsibilities, and boundaries as the proposed charter.
        var builder = new StringBuilder(role.Summary);
        builder.Append("\n\nResponsibilities:\n");
        foreach (var responsibility in role.Responsibilities)
            builder.Append("- ").Append(responsibility).Append('\n');
        builder.Append("\nBoundaries:\n");
        foreach (var boundary in role.Boundaries)
            builder.Append("- ").Append(boundary).Append('\n');
        return builder.ToString().TrimEnd();
    }

    private static string ComputeCastingGenerationDigest(string mode, CastingGenerationInput input)
    {
        // Bind every mode-specific input into the owner idempotency digest.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { mode, input }, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static AuthenticationHeaderValue RequireProjectAuthorizationBearer(HttpContext context)
    {
        // The second credential must be one bounded bearer value, not an authorization context or ambient cookie.
        var values = context.Request.Headers["X-Agentweaver-Project-Authorization"];
        if (values.Count != 1 ||
            !AuthenticationHeaderValue.TryParse(values[0], out var bearer) ||
            !string.Equals(bearer.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(bearer.Parameter) ||
            bearer.Parameter.Length > 16_384 ||
            bearer.Parameter.Any(char.IsWhiteSpace))
            throw new GatewayOwnerFailureException(
                StatusCodes.Status401Unauthorized,
                "application/problem+json",
                ProblemBody("unauthenticated", StatusCodes.Status401Unauthorized));
        return bearer;
    }

    private static void RequireJsonRequestContentType(HttpContext context)
    {
        // This operation accepts JSON only; reject form and vendor media types before parsing.
        var contentType = context.Request.ContentType;
        if (contentType is null ||
            !MediaTypeHeaderValue.TryParse(contentType, out var parsed) ||
            parsed.MediaType is null ||
            !parsed.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase))
            throw new GatewayOwnerFailureException(
                StatusCodes.Status415UnsupportedMediaType,
                "application/problem+json",
                ProblemBody("request_content_type_invalid", StatusCodes.Status415UnsupportedMediaType));
    }

    private static string? ReadTenantSelector(HttpContext context)
    {
        // Preserve the selector as a hint only; the owner-issued contexts decide the actual tenant authority.
        var values = context.Request.Headers[TenantSelectorHeader];
        if (values.Count == 0)
            return null;
        var value = values.Count == 1 ? values[0] : null;
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Contains(',') ||
            value.Any(char.IsControl))
            throw CastingGenerationRequestFailure("tenant_selector_invalid");
        return value;
    }

    private static bool HasRequiredAllowedPropertyNames(
        JsonElement value,
        string[] required,
        string[] allowed)
    {
        // Reject duplicate names, missing required fields, and every unrecognized member.
        if (value.ValueKind != JsonValueKind.Object)
            return false;
        var names = value.EnumerateObject().Select(property => property.Name).ToArray();
        return names.Length == names.Distinct(StringComparer.Ordinal).Count() &&
            names.All(name => allowed.Contains(name, StringComparer.Ordinal)) &&
            required.All(name => names.Contains(name, StringComparer.Ordinal));
    }

    private static GatewayOwnerFailureException CastingGenerationRequestFailure(string code) =>
        new(StatusCodes.Status400BadRequest, "application/problem+json",
            ProblemBody(code, StatusCodes.Status400BadRequest));

    private static GatewayOwnerFailureException CastingGenerationConflict(string code) =>
        new(StatusCodes.Status409Conflict, "application/problem+json",
            ProblemBody(code, StatusCodes.Status409Conflict));

    /// <summary>Validates the owner's live authorization context before returning it without caching.</summary>
    public async Task ProxyAuthorizationContextAsync(
        HttpContext context,
        GatewayRoute route,
        CancellationToken cancellationToken)
    {
        try
        {
            if (context.Request.Query.Count != 0 ||
                context.Request.ContentLength is > 0 ||
                context.Request.ContentType is not null)
            {
                await WriteProblemAsync(context, "request_shape_invalid", StatusCodes.Status400BadRequest)
                    .ConfigureAwait(false);
                return;
            }

            var bearer = await RequireValidatedBearerAsync(context).ConfigureAwait(false);
            using var timeout = CreateOwnerTimeout(cancellationToken);
            using var request = CreateOwnerRequest(context, route, bearer, out var tenantSelector);
            using var response = await SendAsync(
                GatewayOwner.Projects,
                request,
                timeout.Token).ConfigureAwait(false);
            if (IsRedirect(response.StatusCode))
            {
                await WriteProblemAsync(context, "owner_redirect_rejected", StatusCodes.Status502BadGateway)
                    .ConfigureAwait(false);
                return;
            }
            if (response.StatusCode != HttpStatusCode.OK)
            {
                await WriteOwnerFailureAsync(
                    context,
                    await ToOwnerFailureAsync(response, timeout.Token).ConfigureAwait(false))
                    .ConfigureAwait(false);
                return;
            }
            if (response.Headers.CacheControl?.NoStore != true)
            {
                await WriteProblemAsync(
                    context,
                    "owner_contract_invalid",
                    StatusCodes.Status502BadGateway).ConfigureAwait(false);
                return;
            }
            if (response.Content.Headers.ContentLength is > 64 * 1024)
            {
                await WriteProblemAsync(
                    context,
                    "owner_response_too_large",
                    StatusCodes.Status502BadGateway).ConfigureAwait(false);
                return;
            }
            if (response.Content.Headers.ContentType?.MediaType is not { } mediaType ||
                !(mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
                  mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)))
            {
                await WriteProblemAsync(
                    context,
                    "owner_contract_invalid",
                    StatusCodes.Status502BadGateway).ConfigureAwait(false);
                return;
            }

            var body = await ReadBoundedAsync(
                response.Content,
                64 * 1024,
                timeout.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
            if (!HasExactAuthorizationContextShape(document.RootElement))
            {
                await WriteProblemAsync(
                    context,
                    "owner_contract_invalid",
                    StatusCodes.Status502BadGateway).ConfigureAwait(false);
                return;
            }
            var authorization = document.RootElement.Deserialize<ProjectAuthorizationContextResponse>(
                AuthorizationContextJsonOptions);
            if (!IsValidAuthorizationContext(authorization, context.User, tenantSelector))
            {
                await WriteProblemAsync(
                    context,
                    "owner_contract_invalid",
                    StatusCodes.Status502BadGateway).ConfigureAwait(false);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = mediaType;
            context.Response.ContentLength = body.Length;
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            await context.Response.Body.WriteAsync(body, timeout.Token).ConfigureAwait(false);
        }
        catch (GatewayOwnerFailureException exception)
        {
            await WriteOwnerFailureAsync(context, exception).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            await WriteProblemAsync(
                context,
                "owner_contract_invalid",
                StatusCodes.Status502BadGateway).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            await WriteProblemAsync(context, "owner_timeout", StatusCodes.Status504GatewayTimeout)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            await WriteProblemAsync(context, "owner_unavailable", StatusCodes.Status502BadGateway)
                .ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            await WriteProblemAsync(
                context,
                "owner_response_too_large",
                StatusCodes.Status502BadGateway).ConfigureAwait(false);
        }
        catch (IOException)
        {
            await WriteProblemAsync(context, "owner_unavailable", StatusCodes.Status502BadGateway)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Matches owner-issued actor, tenant, run, membership, and authority fields to the caller.</summary>
    private bool IsValidAuthorizationContext(
        ProjectAuthorizationContextResponse? value,
        ClaimsPrincipal caller,
        string? tenantSelector)
    {
        var subjects = caller.FindAll("sub").Take(2).ToArray();
        return value is not null &&
        value.ContractVersion == ProjectAuthorizationContextContract.CurrentVersion &&
        string.Equals(value.Issuer, options.IdentityIssuer.AbsoluteUri, StringComparison.Ordinal) &&
        subjects.Length == 1 &&
        !string.IsNullOrWhiteSpace(subjects[0].Value) &&
        string.Equals(value.ActorId, subjects[0].Value, StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(value.TenantId) &&
        (tenantSelector is null ||
            string.Equals(value.TenantId, tenantSelector, StringComparison.Ordinal)) &&
        MatchesOptionalCallerClaim(caller, "project_id", value.BoundProjectId) &&
        MatchesOptionalCallerClaim(caller, "run_id", value.BoundRunId) &&
        value.MembershipRevision > 0 &&
        (value.BoundRunId is null || value.BoundProjectId is not null) &&
        !value.EffectiveAuthority.IsDefault &&
        value.EffectiveAuthority.All(grant =>
            !string.IsNullOrWhiteSpace(grant.ResourceId) &&
            !grant.Permissions.IsDefault &&
            grant.Permissions.All(permission => permission.RoleRevision > 0));
    }

    private static bool MatchesOptionalCallerClaim(
        ClaimsPrincipal caller,
        string claimType,
        string? responseValue)
    {
        var claims = caller.FindAll(claimType).Take(2).ToArray();
        return claims.Length switch
        {
            0 => responseValue is null,
            1 => !string.IsNullOrWhiteSpace(claims[0].Value) &&
                string.Equals(responseValue, claims[0].Value, StringComparison.Ordinal),
            _ => false,
        };
    }

    private static bool HasExactAuthorizationContextShape(JsonElement value)
    {
        if (!HasExactPropertyNames(value,
                [
                    "contractVersion",
                    "issuer",
                    "actorId",
                    "tenantId",
                    "membershipRevision",
                    "boundProjectId",
                    "boundRunId",
                    "effectiveAuthority",
                ]) ||
            value.GetProperty("effectiveAuthority").ValueKind != JsonValueKind.Array)
            return false;

        foreach (var grant in value.GetProperty("effectiveAuthority").EnumerateArray())
        {
            if (!HasExactPropertyNames(grant, ["resourceType", "resourceId", "permissions"]) ||
                grant.GetProperty("permissions").ValueKind != JsonValueKind.Array)
                return false;
            foreach (var permission in grant.GetProperty("permissions").EnumerateArray())
            {
                if (!HasExactPropertyNames(permission, ["permission", "roleRevision"]))
                    return false;
            }
        }
        return true;
    }

    private static bool HasExactPropertyNames(JsonElement value, string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var remaining = new HashSet<string>(expected, StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!remaining.Remove(property.Name)) return false;
        }
        return remaining.Count == 0;
    }

    /// <summary>Forwards Copilot connection requests and only passes the link cookie for completion.</summary>
    public Task ProxyCopilotConnectionAsync(
        HttpContext context,
        string method,
        string ownerPath,
        bool forwardLinkCookie,
        CancellationToken cancellationToken) =>
        ProxyIdentityBrokerApiAsync(
            context,
            method,
            ownerPath,
            hasJsonBody: HttpMethods.IsPost(method),
            forwardTenantSelector: true,
            forwardCookieName: forwardLinkCookie ? "__Host-agentweaver-copilot-link" : null,
            cookieErrorCode: "copilot_connection_cookie_invalid",
            cancellationToken);

    /// <summary>Forwards JSON identity APIs with validated bearer, optional tenant scope, and selected cookie.</summary>
    public async Task ProxyIdentityBrokerApiAsync(
        HttpContext context,
        string method,
        string ownerPath,
        bool hasJsonBody,
        bool forwardTenantSelector,
        string? forwardCookieName,
        string cookieErrorCode,
        CancellationToken cancellationToken)
    {
        try
        {
            if (context.Request.Query.Count > 0)
                throw new GatewayOwnerFailureException(
                    StatusCodes.Status400BadRequest,
                    "application/problem+json",
                    ProblemBody("request_query_invalid", StatusCodes.Status400BadRequest));

            var bearer = await RequireValidatedBearerAsync(context).ConfigureAwait(false);
            using var timeout = CreateOwnerTimeout(cancellationToken);
            using var request = new HttpRequestMessage(
                new HttpMethod(method),
                BuildOwnerUri(GatewayOwner.IdentityBroker, ownerPath));
            request.Headers.Authorization = bearer;
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (forwardTenantSelector)
                CopyTenantSelector(context, request);
            if (hasJsonBody)
            {
                var requestContentType = context.Request.ContentType;
                if (requestContentType is not null &&
                    (!MediaTypeHeaderValue.TryParse(requestContentType, out var parsed) ||
                     parsed.MediaType is null ||
                     !(parsed.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
                       parsed.MediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase))))
                {
                    throw new GatewayOwnerFailureException(
                        StatusCodes.Status415UnsupportedMediaType,
                        "application/problem+json",
                        ProblemBody("request_content_type_invalid", StatusCodes.Status415UnsupportedMediaType));
                }
                request.Content = new StreamContent(context.Request.Body);
                if (context.Request.ContentLength is { } requestContentLength)
                    request.Content.Headers.ContentLength = requestContentLength;
                request.Content.Headers.ContentType = requestContentType is null
                    ? new MediaTypeHeaderValue("application/json")
                    : MediaTypeHeaderValue.Parse(requestContentType);
            }
            if (forwardCookieName is not null)
                CopyNamedCookie(context, request, forwardCookieName, cookieErrorCode);

            using var response = await SendAsync(
                GatewayOwner.IdentityBroker,
                request,
                timeout.Token).ConfigureAwait(false);
            if (IsRedirect(response.StatusCode))
            {
                await WriteProblemAsync(context, "owner_redirect_rejected", StatusCodes.Status502BadGateway)
                    .ConfigureAwait(false);
                return;
            }
            if (response.Content.Headers.ContentLength is > MaximumProxyResponseBytes)
            {
                await WriteProblemAsync(context, "owner_response_too_large", StatusCodes.Status502BadGateway)
                    .ConfigureAwait(false);
                return;
            }

            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            CopyHeader(response.Headers, context.Response.Headers, "Set-Cookie");
            CopyHeader(response.Headers, context.Response.Headers, "Retry-After");
            if (response.Content.Headers.ContentType is { } contentType)
                context.Response.ContentType = contentType.ToString();
            if (response.Content.Headers.ContentLength is { } responseContentLength)
                context.Response.ContentLength = responseContentLength;
            if (response.StatusCode is not HttpStatusCode.NoContent and not HttpStatusCode.NotModified)
                await CopyBoundedAsync(
                    await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false),
                    context.Response.Body,
                    MaximumProxyResponseBytes,
                    timeout.Token).ConfigureAwait(false);
        }
        catch (GatewayOwnerFailureException exception)
        {
            await WriteOwnerFailureAsync(context, exception).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            await WriteProblemAsync(context, "owner_timeout", StatusCodes.Status504GatewayTimeout)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            await WriteProblemAsync(context, "owner_unavailable", StatusCodes.Status502BadGateway)
                .ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            if (context.Response.HasStarted)
                context.Abort();
            else
                await WriteProblemAsync(context, "owner_response_too_large", StatusCodes.Status502BadGateway)
                    .ConfigureAwait(false);
        }
        catch (IOException)
        {
            await WriteProblemAsync(context, "owner_unavailable", StatusCodes.Status502BadGateway)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Forwards allowlisted GitHub callback query parameters and exactly one transaction cookie.</summary>
    public async Task ProxyGitHubRepoAppCallbackAsync(
        HttpContext context,
        string ownerPath,
        string cookieName,
        string cookieErrorCode,
        IReadOnlyCollection<string> allowedQueryParameters,
        CancellationToken cancellationToken)
    {
        try
        {
            var allowedQuery = new HashSet<string>(allowedQueryParameters, StringComparer.Ordinal);
            if (context.Request.Query.Keys.Any(key =>
                    !allowedQuery.Contains(key) || context.Request.Query[key].Count != 1))
                throw new GatewayOwnerFailureException(
                    StatusCodes.Status400BadRequest,
                    "application/problem+json",
                    ProblemBody("callback_query_invalid", StatusCodes.Status400BadRequest));

            using var timeout = CreateOwnerTimeout(cancellationToken);
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                BuildOwnerUri(GatewayOwner.IdentityBroker, ownerPath + context.Request.QueryString));
            CopyNamedCookie(context, request, cookieName, cookieErrorCode);
            using var response = await SendAsync(
                GatewayOwner.IdentityBroker,
                request,
                timeout.Token).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is > MaximumProxyResponseBytes)
            {
                await WriteProblemAsync(context, "owner_response_too_large", StatusCodes.Status502BadGateway)
                    .ConfigureAwait(false);
                return;
            }

            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            CopyHeader(response.Headers, context.Response.Headers, "Set-Cookie");
            CopyHeader(response.Headers, context.Response.Headers, "Location");
            if (response.Content.Headers.ContentType is { } contentType)
                context.Response.ContentType = contentType.ToString();
            if (response.Content.Headers.ContentLength is { } responseContentLength)
                context.Response.ContentLength = responseContentLength;
            if (response.StatusCode is not HttpStatusCode.NoContent and not HttpStatusCode.NotModified)
                await CopyBoundedAsync(
                    await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false),
                    context.Response.Body,
                    MaximumProxyResponseBytes,
                    timeout.Token).ConfigureAwait(false);
        }
        catch (GatewayOwnerFailureException exception)
        {
            await WriteOwnerFailureAsync(context, exception).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            await WriteProblemAsync(context, "owner_timeout", StatusCodes.Status504GatewayTimeout)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            await WriteProblemAsync(context, "owner_unavailable", StatusCodes.Status502BadGateway)
                .ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            if (context.Response.HasStarted)
                context.Abort();
            else
                await WriteProblemAsync(context, "owner_response_too_large", StatusCodes.Status502BadGateway)
                    .ConfigureAwait(false);
        }
        catch (IOException)
        {
            await WriteProblemAsync(context, "owner_unavailable", StatusCodes.Status502BadGateway)
                .ConfigureAwait(false);
        }
    }

    private static void CopyNamedCookie(
        HttpContext context,
        HttpRequestMessage request,
        string cookieName,
        string errorCode)
    {
        var matchingCookies = context.Request.Headers.Cookie
            .SelectMany(value => (value ?? string.Empty).Split(';'))
            .Select(value => value.Trim())
            .Where(value => value.StartsWith(cookieName + "=", StringComparison.Ordinal))
            .ToArray();
        if (matchingCookies.Length != 1)
            throw new GatewayOwnerFailureException(
                StatusCodes.Status400BadRequest,
                "application/problem+json",
                ProblemBody(errorCode, StatusCodes.Status400BadRequest));

        var cookieValue = matchingCookies[0][(cookieName.Length + 1)..];
        if (cookieValue.Length is 0 or > 4096 ||
            cookieValue.Any(char.IsControl) ||
            !request.Headers.TryAddWithoutValidation("Cookie", $"{cookieName}={cookieValue}"))
            throw new GatewayOwnerFailureException(
                StatusCodes.Status400BadRequest,
                "application/problem+json",
                ProblemBody(errorCode, StatusCodes.Status400BadRequest));
    }

    /// <summary>Streams ordered run events as SSE while rechecking project read access before each event.</summary>
    public async Task SubscribeRunEventsAsync(
        HttpContext context,
        string projectId,
        string runId,
        CancellationToken cancellationToken)
    {
        try
        {
            var cursor = ReadSseCursor(context);
            await EnsureCurrentProjectReadAsync(context, projectId, runId, cancellationToken)
                .ConfigureAwait(false);
            var page = await ReadRunEventPageAsync(
                context, projectId, runId, cursor, cancellationToken).ConfigureAwait(false);
            await EnsureCurrentProjectReadAsync(context, projectId, runId, cancellationToken)
                .ConfigureAwait(false);

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/event-stream; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            await context.Response.StartAsync(cancellationToken).ConfigureAwait(false);
            if (page.Event is not null)
            {
                await WriteEventAsync(context, page, cancellationToken).ConfigureAwait(false);
                cursor = page.NextCursor;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                await EnsureCurrentProjectReadAsync(context, projectId, runId, cancellationToken)
                    .ConfigureAwait(false);
                page = await ReadRunEventPageAsync(
                    context, projectId, runId, cursor, cancellationToken).ConfigureAwait(false);
                if (page.Event is not null)
                {
                    await EnsureCurrentProjectReadAsync(context, projectId, runId, cancellationToken)
                        .ConfigureAwait(false);
                    await WriteEventAsync(context, page, cancellationToken).ConfigureAwait(false);
                    cursor = page.NextCursor;
                }
                else
                {
                    await context.Response.WriteAsync(": keep-alive\n\n", cancellationToken).ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
                    await Task.Delay(
                        GatewayOptions.EventPollIntervalMilliseconds,
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (GatewayOwnerFailureException exception)
        {
            if (context.Response.HasStarted)
                context.Abort();
            else
                await WriteOwnerFailureAsync(context, exception).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            if (context.Response.HasStarted)
                context.Abort();
            else
                await WriteProblemAsync(context, "owner_timeout", StatusCodes.Status504GatewayTimeout)
                    .ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            if (context.Response.HasStarted)
                context.Abort();
            else
                await WriteProblemAsync(context, "owner_unavailable", StatusCodes.Status502BadGateway)
                    .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            if (context.Response.HasStarted)
                context.Abort();
            else
                await WriteProblemAsync(context, "owner_contract_invalid", StatusCodes.Status502BadGateway)
                    .ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            if (context.Response.HasStarted)
                context.Abort();
            else
                await WriteProblemAsync(context, "owner_response_too_large", StatusCodes.Status502BadGateway)
                    .ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            if (context.Response.HasStarted)
                context.Abort();
            else
                await WriteProblemAsync(context, "invalid_cursor", StatusCodes.Status400BadRequest)
                    .ConfigureAwait(false);
        }
        catch (IOException)
        {
            if (context.Response.HasStarted)
                context.Abort();
            else
                await WriteProblemAsync(context, "owner_unavailable", StatusCodes.Status502BadGateway)
                    .ConfigureAwait(false);
        }
    }

    /// <summary>Revalidates project access through the owner before streaming or emitting an event.</summary>
    private async Task EnsureCurrentProjectReadAsync(
        HttpContext context,
        string projectId,
        string runId,
        CancellationToken cancellationToken)
    {
        var bearer = await RequireValidatedBearerAsync(context).ConfigureAwait(false);
        using var timeout = CreateOwnerTimeout(cancellationToken);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            BuildOwnerUri(
                GatewayOwner.Projects,
                $"/api/projects/{Uri.EscapeDataString(projectId)}?runId={Uri.EscapeDataString(runId)}"));
        request.Headers.Authorization = bearer;
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        CopyTenantSelector(context, request);
        using var response = await SendAsync(GatewayOwner.Projects, request, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
            throw await ToOwnerFailureAsync(response, timeout.Token).ConfigureAwait(false);
        if (response.Headers.CacheControl?.NoStore != true)
            throw OwnerContractFailure("projects_read_cache_policy_missing");
        await response.Content.CopyToAsync(Stream.Null, timeout.Token).ConfigureAwait(false);
    }

    /// <summary>Reads one bounded owner page, validates its projection, and returns its event cursor.</summary>
    private async Task<GatewayRunEventPage> ReadRunEventPageAsync(
        HttpContext context,
        string projectId,
        string runId,
        string? cursor,
        CancellationToken cancellationToken)
    {
        var bearer = await RequireValidatedBearerAsync(context).ConfigureAwait(false);
        var query = cursor is null
            ? "?limit=1"
            : $"?cursor={Uri.EscapeDataString(cursor)}&limit=1";
        var path = $"/internal/projects/{Uri.EscapeDataString(projectId)}/runs/{Uri.EscapeDataString(runId)}/events{query}";
        using var timeout = CreateOwnerTimeout(cancellationToken);
        using var request = new HttpRequestMessage(
            HttpMethod.Get, BuildOwnerUri(GatewayOwner.Events, path));
        request.Headers.Authorization = bearer;
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        CopyTenantSelector(context, request);
        using var response = await SendAsync(GatewayOwner.Events, request, timeout.Token).ConfigureAwait(false);
        if (IsRedirect(response.StatusCode))
            throw OwnerContractFailure("events_owner_redirect_rejected");
        if (response.StatusCode != HttpStatusCode.OK)
            throw await ToOwnerFailureAsync(response, timeout.Token).ConfigureAwait(false);
        var body = await ReadBoundedAsync(
            response.Content,
            MaximumRunEventPageBytes,
            timeout.Token).ConfigureAwait(false);
        byte[] projectedBody;
        try
        {
            projectedBody = GatewayRunEventProjection.ProjectPage(body, cursor, 1);
        }
        catch (GatewayEventProjectionException exception)
        {
            throw OwnerContractFailure(exception.Code);
        }
        using var document = JsonDocument.Parse(projectedBody, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        var events = root.GetProperty("events");
        var nextCursor = root.GetProperty("nextCursor").ValueKind == JsonValueKind.Null
            ? null : root.GetProperty("nextCursor").GetString();
        if (events.GetArrayLength() == 0)
            return new GatewayRunEventPage(null, cursor);
        return new GatewayRunEventPage(events[0].Clone(), nextCursor);
    }

    private async Task<GatewayOwnerFailureException> ToOwnerFailureAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (IsRedirect(response.StatusCode))
            return OwnerContractFailure("owner_redirect_rejected");
        var body = await ReadBoundedAsync(
            response.Content,
            MaximumOwnerErrorBytes,
            cancellationToken).ConfigureAwait(false);
        return new GatewayOwnerFailureException(
            (int)response.StatusCode,
            response.Content.Headers.ContentType?.ToString() ?? "application/problem+json",
            body);
    }

    /// <summary>Buffers every explicitly bounded route, including 3 MiB skill content, before contacting an owner.</summary>
    private static async Task<MemoryStream?> BufferBoundedRequestAsync(
        HttpContext context,
        GatewayRoute route,
        CancellationToken cancellationToken)
    {
        if (!route.HasJsonBody || route.MaximumRequestBodyBytes is not { } maximumBytes)
            return null;

        if (context.Request.ContentLength > maximumBytes)
            throw RequestBodyTooLarge();

        var body = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                var count = await context.Request.Body.ReadAsync(
                    buffer.AsMemory(0, (int)Math.Min(buffer.Length, maximumBytes - body.Length + 1)),
                    cancellationToken).ConfigureAwait(false);
                if (count == 0)
                    break;
                if (body.Length + count > maximumBytes)
                    throw RequestBodyTooLarge();
                await body.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            }
            body.Position = 0;
            return body;
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            body.Dispose();
            throw RequestBodyTooLarge();
        }
        catch
        {
            body.Dispose();
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Uses the Gateway problem contract when an inbound request exceeds its route bound.</summary>
    private static GatewayOwnerFailureException RequestBodyTooLarge() =>
        new(StatusCodes.Status413PayloadTooLarge, "application/problem+json",
            ProblemBody("request_body_too_large", StatusCodes.Status413PayloadTooLarge));

    private HttpRequestMessage CreateOwnerRequest(
        HttpContext context,
        GatewayRoute route,
        AuthenticationHeaderValue bearer,
        Stream? requestBody = null) =>
        CreateOwnerRequest(context, route, bearer, out _, requestBody);

    private HttpRequestMessage CreateOwnerRequest(
        HttpContext context,
        GatewayRoute route,
        AuthenticationHeaderValue bearer,
        out string? tenantSelector,
        Stream? requestBody = null)
    {
        tenantSelector = null;
        var path = ExpandOwnerPath(route.OwnerPath, context);
        var target = BuildOwnerUri(route.Owner, path + context.Request.QueryString);
        var request = new HttpRequestMessage(new HttpMethod(route.Method), target);
        request.Headers.Authorization = bearer;
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (route.ForwardTenantSelector)
            tenantSelector = CopyTenantSelector(context, request);
        CopySingleRequestHeader(context, request, "Idempotency-Key");
        CopySingleRequestHeader(context, request, "If-Match");
        if (!route.ForwardRequestHeaders.IsDefaultOrEmpty)
        {
            foreach (var header in route.ForwardRequestHeaders)
                CopySingleRequestHeader(context, request, header);
        }
        var hasRequestBody = context.Request.ContentLength is > 0 ||
            context.Request.ContentType is not null ||
            context.Request.Headers.ContainsKey("Transfer-Encoding");
        if (route.HasJsonBody && (route.JsonBodyRequired || hasRequestBody))
        {
            var contentType = context.Request.ContentType;
            if (contentType is not null &&
                (!MediaTypeHeaderValue.TryParse(contentType, out var parsed) ||
                    parsed.MediaType is null ||
                    !(parsed.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
                      parsed.MediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase))))
            {
                request.Dispose();
                throw new GatewayOwnerFailureException(
                    StatusCodes.Status415UnsupportedMediaType,
                    "application/problem+json",
                    ProblemBody("request_content_type_invalid", StatusCodes.Status415UnsupportedMediaType));
            }
            request.Content = new StreamContent(requestBody ?? context.Request.Body);
            if (context.Request.ContentLength is { } contentLength)
                request.Content.Headers.ContentLength = contentLength;
            request.Content.Headers.ContentType = contentType is null
                ? new MediaTypeHeaderValue("application/json")
                : MediaTypeHeaderValue.Parse(contentType);
        }
        return request;
    }

    /// <summary>Requires one authenticated, unexpired bearer matching the validated request.</summary>
    private static async Task<AuthenticationHeaderValue> RequireValidatedBearerAsync(HttpContext context)
    {
        var authentication = await context.AuthenticateAsync().ConfigureAwait(false);
        var expiresAt = authentication.Principal?.GetExpirationDate();
        if (expiresAt is null &&
            long.TryParse(
                authentication.Principal?.FindFirst("exp")?.Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var unixExpiration) &&
            unixExpiration >= DateTimeOffset.MinValue.ToUnixTimeSeconds() &&
            unixExpiration <= DateTimeOffset.MaxValue.ToUnixTimeSeconds())
            expiresAt = DateTimeOffset.FromUnixTimeSeconds(unixExpiration);
        var values = context.Request.Headers.Authorization;
        if (!authentication.Succeeded ||
            expiresAt is null ||
            expiresAt.Value <= DateTimeOffset.UtcNow ||
            context.User.Identity?.IsAuthenticated != true ||
            values.Count != 1 ||
            !AuthenticationHeaderValue.TryParse(values[0], out var bearer) ||
            !string.Equals(bearer.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(bearer.Parameter) ||
            bearer.Parameter.Length > 16_384 ||
            bearer.Parameter.Any(char.IsWhiteSpace))
            throw new GatewayOwnerFailureException(
                StatusCodes.Status401Unauthorized,
                "application/problem+json",
                ProblemBody("unauthenticated", StatusCodes.Status401Unauthorized));
        return bearer;
    }

    /// <summary>Combines query and reconnect cursors only when they are singular, safe, and identical.</summary>
    private static string? ReadSseCursor(HttpContext context)
    {
        if (context.Request.Query.Keys.Any(key => key != "cursor") ||
            context.Request.Query["cursor"].Count > 1)
            throw new ArgumentException("The event stream accepts only one cursor.");
        var queryCursor = context.Request.Query["cursor"].FirstOrDefault();
        var headerValues = context.Request.Headers["Last-Event-ID"];
        if (headerValues.Count > 1)
            throw new ArgumentException("Only one Last-Event-ID value is permitted.");
        var headerCursor = headerValues.FirstOrDefault();
        if (queryCursor is not null && headerCursor is not null &&
            !string.Equals(queryCursor, headerCursor, StringComparison.Ordinal))
            throw new ArgumentException("The event cursor sources must match.");
        var cursor = headerCursor ?? queryCursor;
        if (cursor is not null &&
            (cursor.Length > MaximumSseCursorLength || !IsSafeSseValue(cursor)))
            throw new ArgumentException("The event cursor is invalid.");
        return cursor;
    }

    private static bool IsSafeSseValue(string value) =>
        value.Length > 0 &&
        value.All(character => character is not '\r' and not '\n' and not '\0');

    /// <summary>Revalidates the caller and emits a projected event with its opaque journal cursor.</summary>
    private async Task WriteEventAsync(
        HttpContext context,
        GatewayRunEventPage page,
        CancellationToken cancellationToken)
    {
        _ = await RequireValidatedBearerAsync(context).ConfigureAwait(false);
        JsonElement projectedEvent;
        try
        {
            projectedEvent = GatewayRunEventProjection.ProjectEvent(
                page.Event ?? throw new GatewayEventProjectionException("events_page_contract_invalid"));
        }
        catch (GatewayEventProjectionException exception)
        {
            throw OwnerContractFailure(exception.Code);
        }
        var cursor = page.NextCursor
            ?? throw new InvalidDataException("Events owner omitted the cursor for a journal event.");
        var eventText = $"id: {cursor}\nevent: session-event\n" +
            $"data: {JsonSerializer.Serialize(projectedEvent, JsonOptions)}\n\n";
        await context.Response.WriteAsync(eventText, cancellationToken).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ExpandOwnerPath(string template, HttpContext context)
    {
        var result = System.Text.RegularExpressions.Regex.Replace(
            template,
            @"\{(?<name>[A-Za-z][A-Za-z0-9]*)(?::[^}]+)?\}",
            match =>
            {
                var key = match.Groups["name"].Value;
                var value = context.Request.RouteValues[key]?.ToString()
                    ?? throw new InvalidOperationException($"Missing route value '{key}'.");
                return Uri.EscapeDataString(value);
            },
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return result;
    }

    private Uri BuildOwnerUri(GatewayOwner owner, string pathAndQuery)
    {
        // The Environment owner address is optional configuration. Without it, return a clear 503 to the
        // client instead of letting GetOwnerBaseAddress throw and surface as a 500.
        if (owner == GatewayOwner.Environment && options.EnvironmentOwnerBaseAddress is null)
            throw new GatewayOwnerFailureException(
                StatusCodes.Status503ServiceUnavailable,
                "application/problem+json",
                ProblemBody("owner_not_configured", StatusCodes.Status503ServiceUnavailable));
        if (owner == GatewayOwner.AgentHost && options.AgentHostOwnerBaseAddress is null)
            throw new GatewayOwnerFailureException(
                StatusCodes.Status503ServiceUnavailable,
                "application/problem+json",
                ProblemBody("owner_not_configured", StatusCodes.Status503ServiceUnavailable));
        return new(options.GetOwnerBaseAddress(owner), pathAndQuery);
    }

    private HttpClient OwnerHttpClient(GatewayOwner owner) =>
        clients.CreateClient(owner.ToString());

    private CancellationTokenSource CreateOwnerTimeout(CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.OwnerRequestTimeout);
        return timeout;
    }

    private Task<HttpResponseMessage> SendAsync(
        GatewayOwner owner,
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        OwnerHttpClient(owner).SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

    private static string? CopyTenantSelector(HttpContext context, HttpRequestMessage request)
    {
        var values = context.Request.Headers[TenantSelectorHeader];
        if (values.Count == 0) return null;
        var value = values.Count == 1 ? values[0] : null;
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Contains(',') ||
            value.Any(char.IsControl) ||
            !request.Headers.TryAddWithoutValidation(TenantSelectorHeader, value))
            throw new GatewayOwnerFailureException(
                StatusCodes.Status400BadRequest,
                "application/problem+json",
                ProblemBody("tenant_selector_invalid", StatusCodes.Status400BadRequest));
        return value;
    }

    private static void CopySingleRequestHeader(
        HttpContext context,
        HttpRequestMessage request,
        string name)
    {
        var values = context.Request.Headers[name];
        if (values.Count > 1)
            throw new GatewayOwnerFailureException(
                StatusCodes.Status400BadRequest,
                "application/problem+json",
                ProblemBody("request_header_invalid", StatusCodes.Status400BadRequest));
        if (values.Count == 1 && values[0] is { } value)
            request.Headers.TryAddWithoutValidation(name, value);
    }

    /// <summary>Returns the route's response size limit, or the default proxy limit.</summary>
    private static long MaximumResponseBytes(GatewayRoute route) =>
        route.MaximumResponseBytes ?? MaximumProxyResponseBytes;

    /// <summary>Copies the owner response headers that the route declares as part of its public contract.</summary>
    private static void CopyExposedResponseHeaders(
        HttpContext context,
        HttpResponseMessage response,
        GatewayRoute route)
    {
        if (route.ExposedResponseHeaders.IsDefaultOrEmpty)
            return;
        foreach (var name in route.ExposedResponseHeaders)
            CopyHeader(response.Headers, context.Response.Headers, name);
    }

    private void CopyResponseHeaders(
        HttpContext context,
        HttpResponseMessage response,
        GatewayOwner owner,
        bool forwardSetCookie)
    {
        CopyHeader(response.Headers, context.Response.Headers, "ETag");
        CopyHeader(response.Content.Headers, context.Response.Headers, "Last-Modified");
        CopyHeader(response.Headers, context.Response.Headers, "Retry-After");
        if (forwardSetCookie)
            CopyHeader(response.Headers, context.Response.Headers, "Set-Cookie");
        if (response.Headers.Location is { } location &&
            RewriteLocation(location, owner) is { } rewritten)
            context.Response.Headers.Location = rewritten;
    }

    private string? RewriteLocation(Uri location, GatewayOwner owner)
    {
        string pathAndQuery;
        if (location.IsAbsoluteUri)
        {
            var baseAddress = options.GetOwnerBaseAddress(owner);
            if (!baseAddress.IsBaseOf(location))
                return null;
            pathAndQuery = location.PathAndQuery;
        }
        else
        {
            pathAndQuery = location.OriginalString;
        }
        if (!pathAndQuery.StartsWith("/api/", StringComparison.Ordinal) ||
            pathAndQuery.StartsWith("//", StringComparison.Ordinal) ||
            pathAndQuery.Contains('\r') || pathAndQuery.Contains('\n'))
            return null;
        return VersionPrefixLocation(pathAndQuery);
    }

    private static string VersionPrefixLocation(string pathAndQuery) =>
        GatewayRouteCatalog.VersionPrefix + pathAndQuery[4..];

    private static void CopyHeader(
        HttpHeaders source,
        IHeaderDictionary destination,
        string name)
    {
        if (source.TryGetValues(name, out var values))
            destination[name] = values.ToArray();
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        (int)status is >= 300 and <= 399;

    private static async Task CopyBoundedAsync(
        Stream source,
        Stream destination,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        long total = 0;
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                    return;
                total += read;
                if (total > maximumBytes)
                    throw new InvalidDataException("The owner response exceeded the Gateway limit.");
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is { } length && length > maximumBytes)
            throw new InvalidDataException("The owner response exceeded the Gateway limit.");
        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var buffer = new MemoryStream();
        await CopyBoundedAsync(source, buffer, maximumBytes, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private static async Task WriteOwnerFailureAsync(
        HttpContext context,
        GatewayOwnerFailureException exception)
    {
        if (context.Response.HasStarted)
        {
            context.Abort();
            return;
        }
        context.Response.StatusCode = exception.StatusCode;
        context.Response.ContentType = exception.ContentType;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.ContentLength = exception.Body.Length;
        await context.Response.Body.WriteAsync(exception.Body, context.RequestAborted).ConfigureAwait(false);
    }

    private static Task WriteProblemAsync(HttpContext context, string code, int statusCode) =>
        context.Response.HasStarted
            ? AbortResponseAsync(context)
            : Results.Problem(
                title: code,
                statusCode: statusCode,
                extensions: new Dictionary<string, object?> { ["code"] = code })
            .ExecuteAsync(context);

    private static Task AbortResponseAsync(HttpContext context)
    {
        context.Abort();
        return Task.CompletedTask;
    }

    private static GatewayOwnerFailureException OwnerContractFailure(string code) =>
        new(
            StatusCodes.Status502BadGateway,
            "application/problem+json",
            ProblemBody(code, StatusCodes.Status502BadGateway));

    internal static byte[] ProblemBody(string code, int statusCode) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "about:blank",
            title = code,
            status = statusCode,
            code,
        }, JsonOptions);

    private sealed record CastingGenerationInput(
        long ExpectedConfigurationRevision,
        string RunId,
        string SessionId,
        string IdempotencyKey,
        string Prompt,
        string? Goal,
        int TeamSize,
        string Intent,
        ImmutableArray<string> PreferredRoleIds,
        string? Universe);

    private sealed record CastingGenerationOwnerRequest(
        Guid RequestId,
        Guid ProposalId,
        string ProjectId,
        long BaseConfigurationRevision,
        string RunId,
        string Mode,
        string State,
        string? FailureCode,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);

    private sealed record CastingGenerationExecutionOwnerResponse(
        Guid RequestId,
        string RunId,
        string Output);

    private sealed record GatewayCastingProjectConfiguration(
        ImmutableArray<GatewayCastingAgentCharter> AgentCharters,
        ImmutableArray<GatewayCastingAgentCast> Casting);

    private sealed record GatewayRunEventPage(JsonElement? Event, string? NextCursor);
}
