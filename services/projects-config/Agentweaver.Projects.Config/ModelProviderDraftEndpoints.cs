using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace Agentweaver.Projects.Config;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ModelProviderDraftSaveBody
{
    [JsonPropertyName("operationId")]
    public required Guid OperationId { get; init; }

    [JsonPropertyName("expectedRevision")]
    public required long ExpectedRevision { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("base_url")]
    public required string BaseUrl { get; init; }

    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("wire_api")]
    public string? WireApi { get; init; }

    [JsonPropertyName("azure_api_version")]
    public string? AzureApiVersion { get; init; }

    [JsonPropertyName("reasoning_efforts")]
    public ImmutableArray<string> ReasoningEfforts { get; init; }

    [JsonPropertyName("prompt_capacity_tokens")]
    public int? PromptCapacityTokens { get; init; }

    [JsonPropertyName("rate_card_id")]
    public string? RateCardId { get; init; }

    [JsonPropertyName("rate_card_version")]
    public string? RateCardVersion { get; init; }

    [JsonIgnore]
    public bool RateCardIdSpecified { get; init; }

    [JsonIgnore]
    public bool RateCardVersionSpecified { get; init; }

    [JsonPropertyName("reference")]
    public string? Reference { get; init; }

    [JsonPropertyName("targetInstallationId")]
    public string? TargetInstallationId { get; init; }
}

internal sealed record ModelProviderDraftResponse(
    string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("base_url")] string BaseUrl,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("wire_api")] string? WireApi,
    [property: JsonPropertyName("azure_api_version")] string? AzureApiVersion,
    [property: JsonPropertyName("reasoning_efforts")] ImmutableArray<string> ReasoningEfforts,
    [property: JsonPropertyName("prompt_capacity_tokens")] int? PromptCapacityTokens,
    string Scope,
    string? ProjectId,
    string State,
    long Revision,
    string ConfigurationHash,
    [property: JsonPropertyName("rate_card_id")] string? RateCardId,
    [property: JsonPropertyName("rate_card_version")] string? RateCardVersion,
    [property: JsonPropertyName("has_api_key")] bool HasApiKey,
    [property: JsonPropertyName("is_active")] bool IsActive,
    bool HasCredential,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reference);

internal sealed record ModelProviderDraftPageResponse(
    string? SwitchMode,
    string? ActiveProviderId,
    long? SettingsRevision,
    IReadOnlyList<ModelProviderDraftResponse> Providers,
    string? NextAfterProviderId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ActivatePlatformModelProviderRequest(
    long ExpectedSettingsRevision,
    long ExpectedProviderRevision);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DeactivatePlatformModelProviderRequest(long ExpectedSettingsRevision);

internal sealed class ModelProviderDraftUnsafeMetadataException(string providerId, long revision) : Exception
{
    public string ProviderId { get; } = providerId;
    public long Revision { get; } = revision;
}

internal static class ModelProviderDraftEndpoints
{
    internal const int MaximumSaveBodyBytes = 32 * 1024;
    internal const string InvalidRequestCode = "model_provider_draft_invalid_request";
    private const int DefaultPageSize = 50;
    private static readonly JsonSerializerOptions RequestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
    };

    private static readonly HashSet<string> EndpointNames = new(StringComparer.Ordinal)
    {
        "readPlatformByokDeploymentBindings",
        "listPlatformByokProviderDrafts", "readPlatformByokProviderDraft",
        "createPlatformByokProviderDraft", "updatePlatformByokProviderDraft",
        "activatePlatformByokProviderDraft", "deactivatePlatformByokProviderDraft",
        "listProjectByokProviderDrafts", "readProjectByokProviderDraft",
        "createProjectByokProviderDraft", "updateProjectByokProviderDraft",
    };

    public static IEndpointRouteBuilder MapModelProviderDraftEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/platform/byok-deployment-bindings", ReadDeploymentBindingsAsync)
            .RequireAuthorization().WithName("readPlatformByokDeploymentBindings");
        var platform = app.MapGroup("/api/platform/byok-providers").RequireAuthorization();
        platform.MapGet("", (HttpContext context, ProjectAuthorizationOwner owner,
            ModelProviderDraftService service, CancellationToken cancellationToken) =>
            ListAsync(context, owner, service, ModelProviderBindingScope.Platform, null, cancellationToken))
            .WithName("listPlatformByokProviderDrafts");
        platform.MapGet("/{providerId}", (HttpContext context, string providerId,
            ProjectAuthorizationOwner owner, ModelProviderDraftService service, CancellationToken cancellationToken) =>
            ReadAsync(context, owner, service, ModelProviderBindingScope.Platform, null, providerId, cancellationToken))
            .WithName("readPlatformByokProviderDraft");
        platform.MapPost("", (HttpContext context, ProjectAuthorizationOwner owner,
            ModelProviderDraftService service, CancellationToken cancellationToken) =>
            SaveAsync(context, owner, service, ModelProviderBindingScope.Platform, null, null, true, cancellationToken))
            .WithName("createPlatformByokProviderDraft");
        platform.MapPut("/{providerId}", (HttpContext context, string providerId,
            ProjectAuthorizationOwner owner, ModelProviderDraftService service, CancellationToken cancellationToken) =>
            SaveAsync(context, owner, service, ModelProviderBindingScope.Platform, null, providerId, false, cancellationToken))
            .WithName("updatePlatformByokProviderDraft");
        platform.MapPost("/{providerId}/activate", ActivateAsync)
            .WithName("activatePlatformByokProviderDraft");
        platform.MapPost("/deactivate", DeactivateAsync)
            .WithName("deactivatePlatformByokProviderDraft");

        var project = app.MapGroup("/api/projects/{projectId}/byok-providers").RequireAuthorization();
        project.MapGet("", (HttpContext context, string projectId, ProjectAuthorizationOwner owner,
            ModelProviderDraftService service, CancellationToken cancellationToken) =>
            ListAsync(context, owner, service, ModelProviderBindingScope.Project, projectId, cancellationToken))
            .WithName("listProjectByokProviderDrafts");
        project.MapGet("/{providerId}", (HttpContext context, string projectId, string providerId,
            ProjectAuthorizationOwner owner, ModelProviderDraftService service, CancellationToken cancellationToken) =>
            ReadAsync(context, owner, service, ModelProviderBindingScope.Project, projectId, providerId, cancellationToken))
            .WithName("readProjectByokProviderDraft");
        project.MapPost("", (HttpContext context, string projectId, ProjectAuthorizationOwner owner,
            ModelProviderDraftService service, CancellationToken cancellationToken) =>
            SaveAsync(context, owner, service, ModelProviderBindingScope.Project, projectId, null, true, cancellationToken))
            .WithName("createProjectByokProviderDraft");
        project.MapPut("/{providerId}", (HttpContext context, string projectId, string providerId,
            ProjectAuthorizationOwner owner, ModelProviderDraftService service, CancellationToken cancellationToken) =>
            SaveAsync(context, owner, service, ModelProviderBindingScope.Project, projectId, providerId, false, cancellationToken))
            .WithName("updateProjectByokProviderDraft");
        return app;
    }

    public static IApplicationBuilder UseModelProviderDraftNoStore(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (!IsDraftEndpoint(context))
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            SetNoStore(context);
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeFeature)
                sizeFeature.MaxRequestBodySize = MaximumSaveBodyBytes;
            if (IsSaveEndpoint(context) && context.Request.ContentLength > MaximumSaveBodyBytes)
            {
                await WriteErrorAsync(context, StatusCodes.Status413PayloadTooLarge, "request_body_too_large")
                    .ConfigureAwait(false);
                return;
            }
            if (HttpMethods.IsGet(context.Request.Method) &&
                (context.Request.ContentLength is > 0 ||
                 context.Request.Headers.ContainsKey("Transfer-Encoding")))
            {
                await WriteErrorAsync(context, StatusCodes.Status400BadRequest, InvalidRequestCode)
                    .ConfigureAwait(false);
                return;
            }

            await next(context).ConfigureAwait(false);
        });

    public static IApplicationBuilder UseModelProviderDraftBodyLimit(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (!IsDraftEndpoint(context) || !IsSaveEndpoint(context))
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            try
            {
                await using var bounded = new MemoryStream();
                var buffer = new byte[4096];
                while (true)
                {
                    var count = await context.Request.Body.ReadAsync(
                        buffer.AsMemory(0, Math.Min(buffer.Length, MaximumSaveBodyBytes + 1 - (int)bounded.Length)),
                        context.RequestAborted).ConfigureAwait(false);
                    if (count == 0)
                        break;
                    if (bounded.Length + count > MaximumSaveBodyBytes)
                    {
                        await WriteErrorAsync(context, StatusCodes.Status413PayloadTooLarge, "request_body_too_large")
                            .ConfigureAwait(false);
                        return;
                    }
                    await bounded.WriteAsync(buffer.AsMemory(0, count), context.RequestAborted).ConfigureAwait(false);
                }

                bounded.Position = 0;
                var original = context.Request.Body;
                context.Request.Body = bounded;
                try
                {
                    await next(context).ConfigureAwait(false);
                }
                finally
                {
                    context.Request.Body = original;
                }
            }
            catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                await WriteErrorAsync(context, StatusCodes.Status413PayloadTooLarge, "request_body_too_large")
                    .ConfigureAwait(false);
            }
        });

    private static async Task<IResult> ListAsync(
        HttpContext context,
        ProjectAuthorizationOwner owner,
        ModelProviderDraftService service,
        ModelProviderBindingScope scope,
        string? projectId,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!TryReadPage(context.Request, out var pageSize, out var afterProviderId))
                return InvalidRequest();
            var caller = await ResolveCallerAsync(context, owner, cancellationToken).ConfigureAwait(false);
            var page = await service.ListPageAsync(
                caller, scope, projectId, pageSize, afterProviderId, cancellationToken).ConfigureAwait(false);
            var providers = page.Providers
                .Select(item => Project(item, page.ActiveProviderId))
                .ToArray();
            return Results.Ok(new ModelProviderDraftPageResponse(
                page.SwitchMode, page.ActiveProviderId, page.SettingsRevision,
                providers, page.NextAfterProviderId));
        }

        catch (ModelProviderDraftUnsafeMetadataException exception)
        {
            return UnsafeMetadata(exception);
        }
        catch (ModelProviderDraftException exception)
        {
            return DraftError(exception);
        }
        catch (ProjectConfigException exception)
        {
            return OwnerError(exception);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Unavailable();
        }
    }

    private static async Task<IResult> ReadDeploymentBindingsAsync(
        HttpContext context, ProjectAuthorizationOwner owner, ModelProviderDraftService service,
        CancellationToken cancellationToken)
    {
        try
        {
            if (context.Request.Query.Count != 0)
                return InvalidRequest();
            var caller = await ResolveCallerAsync(context, owner, cancellationToken).ConfigureAwait(false);
            var snapshot = await service.ReadDeploymentBindingsAsync(caller, cancellationToken).ConfigureAwait(false);
            if (!snapshot.IsAvailable)
                return Unavailable();
            return Results.Ok(new
            {
                revision = snapshot.Revision,
                bindings = snapshot.Bindings
                    .Where(binding => binding.SourceMode == "Byok")
                    .Select(binding => new
                    {
                        binding.Reference,
                        binding.Enabled,
                        reasoningEfforts = binding.SupportedReasoningEfforts,
                    }).ToArray(),
            });
        }
        catch (ProjectConfigException exception)
        {
            return OwnerError(exception);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Unavailable();
        }
    }

    private static async Task<IResult> ReadAsync(
        HttpContext context,
        ProjectAuthorizationOwner owner,
        ModelProviderDraftService service,
        ModelProviderBindingScope scope,
        string? projectId,
        string providerId,
        CancellationToken cancellationToken)
    {
        try
        {
            if (context.Request.Query.Count != 0 || !IsProviderId(providerId))
                return InvalidRequest();
            var caller = await ResolveCallerAsync(context, owner, cancellationToken).ConfigureAwait(false);
            return Results.Ok(Project(await service.ReadAsync(
                caller, scope, projectId, providerId, cancellationToken).ConfigureAwait(false)));
        }
        catch (ModelProviderDraftUnsafeMetadataException exception)
        {
            return UnsafeMetadata(exception);
        }
        catch (ModelProviderDraftException exception)
        {
            return DraftError(exception);
        }
        catch (ProjectConfigException exception)
        {
            return OwnerError(exception);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Unavailable();
        }
    }

    private static async Task<IResult> SaveAsync(
        HttpContext context,
        ProjectAuthorizationOwner owner,
        ModelProviderDraftService service,
        ModelProviderBindingScope scope,
        string? projectId,
        string? providerId,
        bool create,
        CancellationToken cancellationToken)
    {
        try
        {
            if (context.Request.Query.Count != 0 || (!create && !IsProviderId(providerId)))
                return InvalidRequest();
            var caller = await ResolveCallerAsync(context, owner, cancellationToken).ConfigureAwait(false);
            if (!IsJsonContentType(context.Request.ContentType))
                return UnsupportedMediaType();
            var body = await ReadSaveBodyAsync(
                context.Request, cancellationToken, create && scope == ModelProviderBindingScope.Platform)
                .ConfigureAwait(false);
            if (body is null)
                return InvalidRequest();
            if (ValidateSaveBody(body, create) is { } invalid)
                return invalid;
            if (scope == ModelProviderBindingScope.Platform && !create &&
                body.RateCardIdSpecified != body.RateCardVersionSpecified)
                return InvalidRequest();
            if (!IsSafePublicUrl(body.BaseUrl, body.Type))
                return InvalidRequest();

            var id = create ? "byok-" + body.OperationId.ToString("N") : providerId!;
            var request = new SaveModelProviderDraftRequest(
                body.OperationId, body.ExpectedRevision, body.Name, body.Type, body.BaseUrl, body.Model,
                body.WireApi, body.AzureApiVersion,
                body.ReasoningEfforts.IsDefault ? ImmutableArray<string>.Empty : body.ReasoningEfforts,
                body.PromptCapacityTokens, body.RateCardId, body.RateCardVersion)
            {
                Reference = body.Reference,
                TargetInstallationId = body.TargetInstallationId,
                PreserveRateCardLink = scope == ModelProviderBindingScope.Platform && !create &&
                    !body.RateCardIdSpecified && !body.RateCardVersionSpecified,
            };
            var saved = await service.SaveAsync(
                caller, scope, projectId, id, request, cancellationToken).ConfigureAwait(false);
            return Results.Ok(Project(saved));
        }
        catch (ModelProviderDraftUnsafeMetadataException exception)
        {
            return UnsafeMetadata(exception);
        }
        catch (ModelProviderDraftException exception)
        {
            return DraftError(exception);
        }
        catch (ProjectConfigException exception)
        {
            return OwnerError(exception);
        }
        catch (JsonException)
        {
            return InvalidRequest();
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Unavailable();
        }
    }

    internal static async Task<ModelProviderDraftSaveBody?> ReadSaveBodyAsync(
        HttpRequest request,
        CancellationToken cancellationToken,
        bool allowReference = false)
    {
        if (request.ContentType is null ||
            !Microsoft.Net.Http.Headers.MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType))
            return null;
        var mediaType = contentType.MediaType.ToString();
        if (!(mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
              mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)))
            return null;

        using var document = await JsonDocument.ParseAsync(
            request.Body, new JsonDocumentOptions { MaxDepth = 16 }, cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
            if (!seen.Add(property.Name))
                return null;
        if (document.RootElement.TryGetProperty("reference", out var reference) &&
            (!allowReference || reference.ValueKind != JsonValueKind.String ||
             reference.GetString() is not { } value || !ModelProviderDraftService.IsValidReference(value)))
            return null;
        var hasReference = document.RootElement.TryGetProperty("reference", out _);
        var hasTarget = document.RootElement.TryGetProperty("targetInstallationId", out var target);
        if (hasReference != hasTarget ||
            (hasTarget && (!allowReference || target.ValueKind != JsonValueKind.String ||
                !ModelProviderDraftService.IsValidInstallationId(target.GetString()))))
            return null;
        var body = JsonSerializer.Deserialize<ModelProviderDraftSaveBody>(
            document.RootElement.GetRawText(), RequestJsonOptions);
        return body is null
            ? null
            : body with
            {
                RateCardIdSpecified = document.RootElement.TryGetProperty("rate_card_id", out _),
                RateCardVersionSpecified = document.RootElement.TryGetProperty("rate_card_version", out _),
            };
    }

    internal static IResult? ValidateSaveBody(ModelProviderDraftSaveBody? body, bool create) =>
        body is null || body.OperationId == Guid.Empty ||
        (create ? body.ExpectedRevision != 0 : body.ExpectedRevision is < 1 or >= long.MaxValue)
            ? InvalidRequest()
            : null;

    internal static bool TryReadPage(HttpRequest request, out int pageSize, out string? afterProviderId)
    {
        pageSize = DefaultPageSize;
        afterProviderId = null;
        foreach (var key in request.Query.Keys)
        {
            var values = request.Query[key];
            if (values.Count != 1 || string.IsNullOrEmpty(values[0]))
                return false;
            if (key == "pageSize")
            {
                if (!int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out pageSize) ||
                    pageSize is < 1 or > 100)
                    return false;
            }
            else if (key == "afterProviderId")
            {
                afterProviderId = values[0];
                if (!IsProviderId(afterProviderId))
                    return false;
            }
            else
                return false;
        }
        return true;
    }

    internal static ModelProviderDraftResponse Project(
        ModelProviderDraft draft,
        string? activeProviderId = null)
    {
        var binding = draft.Binding;
        ValidatePublicUrl(binding.BaseUrl, binding.ProviderType, binding.ProviderId, binding.ProviderRevision);
        return new ModelProviderDraftResponse(
            binding.ProviderId, binding.DisplayName, binding.ProviderType, binding.BaseUrl, binding.Deployment,
            binding.WireApi, binding.AzureApiVersion, binding.ReasoningEfforts, binding.PromptCapacityTokens,
            binding.Scope == ModelProviderBindingScope.Platform ? "platform" : "project",
            binding.ProjectId, draft.State.ToString(), binding.ProviderRevision, binding.ConfigurationHash,
            binding.Scope == ModelProviderBindingScope.Platform ? binding.RateCardId : null,
            binding.Scope == ModelProviderBindingScope.Platform ? binding.RateCardVersion : null,
            false,
            binding.ProviderId == activeProviderId,
            binding.CredentialVersion is not null,
            draft.Reference);
    }

    private static async Task<IResult> ActivateAsync(
        HttpContext context,
        string providerId,
        ActivatePlatformModelProviderRequest request,
        ProjectAuthorizationOwner owner,
        ProjectsConfigService service,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            if (context.Request.Query.Count != 0 || !IsProviderId(providerId))
                return InvalidRequest();
            var caller = await ResolveCallerAsync(context, owner, cancellationToken).ConfigureAwait(false);
            return Results.Ok(await service.ActivatePlatformModelProviderAsync(
                caller, providerId, request.ExpectedSettingsRevision,
                request.ExpectedProviderRevision, cancellationToken).ConfigureAwait(false));
        }
        catch (ProjectConfigException exception)
        {
            return OwnerError(exception);
        }
        catch (ModelProviderDraftException exception)
        {
            return DraftError(exception);
        }
        catch (JsonException)
        {
            return InvalidRequest();
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
    }

    private static async Task<IResult> DeactivateAsync(
        HttpContext context,
        DeactivatePlatformModelProviderRequest request,
        ProjectAuthorizationOwner owner,
        ProjectsConfigService service,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            if (context.Request.Query.Count != 0)
                return InvalidRequest();
            var caller = await ResolveCallerAsync(context, owner, cancellationToken).ConfigureAwait(false);
            return Results.Ok(await service.DeactivatePlatformModelProviderAsync(
                caller, request.ExpectedSettingsRevision, cancellationToken).ConfigureAwait(false));
        }
        catch (ProjectConfigException exception)
        {
            return OwnerError(exception);
        }
        catch (JsonException)
        {
            return InvalidRequest();
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
    }

    private static void ValidatePublicUrl(string value, string providerType, string providerId, long revision)
    {
        if (!IsSafePublicUrl(value, providerType))
            throw new ModelProviderDraftUnsafeMetadataException(providerId, revision);
    }

    internal static bool IsSafePublicUrl(string value, string providerType) =>
        ModelProviderPublicUrl.IsSafe(value, providerType);

    private static IResult DraftError(ModelProviderDraftException exception) =>
        Results.Json(new { code = exception.Code }, statusCode: exception.Code switch
        {
            "model_provider_draft_not_found" => StatusCodes.Status404NotFound,
            "model_provider_draft_conflict" => StatusCodes.Status409Conflict,
            "reference_conflict" => StatusCodes.Status409Conflict,
            "target_installation_mismatch" => StatusCodes.Status409Conflict,
            "reference_reconciliation_required" => StatusCodes.Status409Conflict,
            "model_provider_draft_unavailable" => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status400BadRequest,
        });

    private static IResult OwnerError(ProjectConfigException exception) =>
        Results.Json(new { code = exception.Code }, statusCode: exception.StatusCode);

    private static IResult UnsafeMetadata(ModelProviderDraftUnsafeMetadataException exception) =>
        Results.Json(new
        {
            code = "model_provider_draft_metadata_requires_update",
            id = exception.ProviderId,
            revision = exception.Revision,
        }, statusCode: StatusCodes.Status409Conflict);

    private static IResult InvalidRequest() =>
        Results.Json(new { code = InvalidRequestCode }, statusCode: StatusCodes.Status400BadRequest);

    private static IResult UnsupportedMediaType() =>
        Results.Json(new { code = "model_provider_draft_unsupported_media_type" },
            statusCode: StatusCodes.Status415UnsupportedMediaType);

    private static IResult Unavailable() =>
        Results.Json(new { code = "model_provider_draft_unavailable" },
            statusCode: StatusCodes.Status503ServiceUnavailable);

    private static bool IsJsonContentType(string? value) =>
        value is not null &&
        Microsoft.Net.Http.Headers.MediaTypeHeaderValue.TryParse(value, out var contentType) &&
        (contentType.MediaType.ToString().Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
         contentType.MediaType.ToString().EndsWith("+json", StringComparison.OrdinalIgnoreCase));

    private static Task<ProjectAuthorizationContext> ResolveCallerAsync(
        HttpContext context,
        ProjectAuthorizationOwner owner,
        CancellationToken cancellationToken) =>
        owner.ResolveAsync(
            context.User,
            context.Request.Headers[ProjectAuthorizationOwner.TenantSelectorHeader].ToArray(),
            cancellationToken);

    private static bool IsDraftEndpoint(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName is { } name &&
        EndpointNames.Contains(name);

    private static bool IsSaveEndpoint(HttpContext context) =>
        context.Request.Method is "POST" or "PUT";

    private static bool IsProviderId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value is not ("." or "..") &&
        value.Length <= ModelProviderDraftLimits.ProviderId &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':');

    private static void SetNoStore(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.OnStarting(() => {
            context.Response.Headers.CacheControl = "no-store";
            return Task.CompletedTask;
        });
    }

    private static async Task WriteErrorAsync(HttpContext context, int statusCode, string code)
    {
        SetNoStore(context);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new { code }, context.RequestAborted).ConfigureAwait(false);
    }
}
