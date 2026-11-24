using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenIddict.Validation.AspNetCore;

namespace Agentweaver.Identity.Broker;

internal static class ModelProviderCredentialEndpoints
{
    private const int MaximumPutBodyBytes = 16 * 1024;
    private const int MaximumDeleteBodyBytes = 8 * 1024;
    private static readonly TimeSpan ByokCredentialExpiry = TimeSpan.FromDays(3650);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
    };

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record PutCredentialBody
    {
        public Guid OperationId { get; init; }
        public long ExpectedProviderRevision { get; init; }
        public long ExpectedCredentialRevision { get; init; }
        public string? ApiKey { get; init; }
        public string? RecoverySecretVersion { get; init; }
        public bool? RecheckExistingVersion { get; init; }
        public string? KeyVaultSecret { get; init; }
        public string? KeyVaultVersion { get; init; }
        public string? VaultSource { get; init; }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record DeleteCredentialBody
    {
        public Guid OperationId { get; init; }
        public long? ExpectedProviderRevision { get; init; }
        public long? ExpectedCredentialRevision { get; init; }
        public bool? Cancel { get; init; }
    }

    internal sealed record CredentialOperationResponse(
        Guid OperationId,
        string Status,
        string Scope,
        string? ProjectId,
        string ProviderId,
        long? ProviderRevision,
        long? CredentialRevision,
        string? CredentialVersion,
        string? ConfigurationHash,
        string? ProviderState = null);

    internal sealed record CredentialOperationStatusResponse(
        Guid OperationId,
        string Scope,
        string? ProjectId,
        string ProviderId,
        string Status,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        long? ProviderRevision = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        bool? HasCredential = null);

    internal static bool IsCredentialPath(PathString path)
    {
        var value = path.Value?.TrimEnd('/');
        if (string.IsNullOrEmpty(value) ||
            !value.EndsWith("/credential", StringComparison.OrdinalIgnoreCase))
            return false;
        return value.StartsWith("/api/platform/byok-providers/", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("/api/projects/", StringComparison.OrdinalIgnoreCase);
    }

    internal static void SuppressCredentialRequestLogs(IServiceCollection services, IHttpContextAccessor accessor) =>
        services.PostConfigure<LoggerFilterOptions>(options =>
        {
            bool Allowed(string? category) =>
                category != "Microsoft.AspNetCore.Hosting.Diagnostics" ||
                accessor.HttpContext is not { } context ||
                !IsCredentialPath(context.Request.Path);
            for (var index = 0; index < options.Rules.Count; index++)
            {
                var rule = options.Rules[index];
                var original = rule.Filter;
                options.Rules[index] = new LoggerFilterRule(rule.ProviderName, rule.CategoryName, rule.LogLevel,
                    (provider, category, level) =>
                        Allowed(category) && (original is null || original(provider, category, level)));
            }
            options.Rules.Insert(0, new LoggerFilterRule(null, null, null,
                (_, category, _) => Allowed(category)));
        });

    internal static IEndpointRouteBuilder MapModelProviderCredentialEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = new AuthorizeAttribute
        {
            AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme,
        };

        app.MapPut("/api/platform/byok-providers/{providerId}/credential", (
                string providerId,
                HttpContext context,
                IdentityBrokerOptions brokerOptions,
                ModelProviderCredentialStore store,
                IModelProviderByokVault byokVault,
                BrokerModelProviderCredentialPublicationV2Client publicationClient,
                TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
            HandlePutAsync(
                context, ModelProviderBindingScope.Platform, null, providerId,
                brokerOptions, store, byokVault, publicationClient, timeProvider, cancellationToken))
            .RequireAuthorization(auth)
            .WithName("BrokerSetPlatformByokProviderCredential");

        app.MapGet("/api/platform/byok-providers/{providerId}/credential/operations/{operationId:guid}", (
                string providerId,
                Guid operationId,
                HttpContext context,
                IdentityBrokerOptions brokerOptions,
                ModelProviderCredentialStore store,
                BrokerModelProviderCredentialPublicationV2Client publicationClient,
                CancellationToken cancellationToken) =>
            HandleReadAsync(
                context, ModelProviderBindingScope.Platform, null, providerId, operationId,
                brokerOptions, store, publicationClient, cancellationToken))
            .RequireAuthorization(auth)
            .WithName("BrokerReadPlatformByokProviderCredentialOperation");

        app.MapDelete("/api/platform/byok-providers/{providerId}/credential", (
                string providerId,
                HttpContext context,
                IdentityBrokerOptions brokerOptions,
                ModelProviderCredentialStore store,
                BrokerModelProviderCredentialPublicationV2Client publicationClient,
                CancellationToken cancellationToken) =>
            HandleDeleteAsync(
                context, ModelProviderBindingScope.Platform, null, providerId,
                brokerOptions, store, publicationClient, cancellationToken))
            .RequireAuthorization(auth)
            .WithName("BrokerDisablePlatformByokProviderCredential");

        app.MapPut("/api/projects/{projectId}/byok-providers/{providerId}/credential", (
                string projectId,
                string providerId,
                HttpContext context,
                IdentityBrokerOptions brokerOptions,
                ModelProviderCredentialStore store,
                IModelProviderByokVault byokVault,
                BrokerModelProviderCredentialPublicationV2Client publicationClient,
                TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
            HandlePutAsync(
                context, ModelProviderBindingScope.Project, projectId, providerId,
                brokerOptions, store, byokVault, publicationClient, timeProvider, cancellationToken))
            .RequireAuthorization(auth)
            .WithName("BrokerSetProjectByokProviderCredential");

        app.MapGet("/api/projects/{projectId}/byok-providers/{providerId}/credential/operations/{operationId:guid}", (
                string projectId,
                string providerId,
                Guid operationId,
                HttpContext context,
                IdentityBrokerOptions brokerOptions,
                ModelProviderCredentialStore store,
                BrokerModelProviderCredentialPublicationV2Client publicationClient,
                CancellationToken cancellationToken) =>
            HandleReadAsync(
                context, ModelProviderBindingScope.Project, projectId, providerId, operationId,
                brokerOptions, store, publicationClient, cancellationToken))
            .RequireAuthorization(auth)
            .WithName("BrokerReadProjectByokProviderCredentialOperation");

        app.MapDelete("/api/projects/{projectId}/byok-providers/{providerId}/credential", (
                string projectId,
                string providerId,
                HttpContext context,
                IdentityBrokerOptions brokerOptions,
                ModelProviderCredentialStore store,
                BrokerModelProviderCredentialPublicationV2Client publicationClient,
                CancellationToken cancellationToken) =>
            HandleDeleteAsync(
                context, ModelProviderBindingScope.Project, projectId, providerId,
                brokerOptions, store, publicationClient, cancellationToken))
            .RequireAuthorization(auth)
            .WithName("BrokerDisableProjectByokProviderCredential");

        return app;
    }

    private static async Task<IResult> HandleReadAsync(
        HttpContext context,
        ModelProviderBindingScope scope,
        string? projectId,
        string providerId,
        Guid operationId,
        IdentityBrokerOptions brokerOptions,
        ModelProviderCredentialStore store,
        BrokerModelProviderCredentialPublicationV2Client publicationClient,
        CancellationToken cancellationToken)
    {
        SetNoStore(context);
        if (!TryValidateCaller(context, brokerOptions, scope, projectId, out var caller, out var authError))
            return authError!;

        var hasHeaderlessBody =
            context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true &&
            await HasBodyAsync(context.Request.Body, cancellationToken).ConfigureAwait(false);
        if (operationId == Guid.Empty ||
            context.Request.Query.Count != 0 ||
            context.Request.ContentLength is > 0 ||
            context.Request.ContentType is not null ||
            context.Request.Headers.ContainsKey("Transfer-Encoding") ||
            hasHeaderlessBody ||
            !IsValidIdentifier(providerId, 128) ||
            (scope == ModelProviderBindingScope.Project && !IsValidIdentifier(projectId, 32)))
            return Error("credential_request_invalid", 400);

        try
        {
            var readRequest = new ModelProviderCredentialOperationReadRequest(
                operationId, scope, projectId, providerId, caller!.Issuer, caller.ActorId,
                caller.TenantSelector, caller.TenantClaim, caller.BoundProjectId);
            var receiptRead = await publicationClient.ReadOperationAsync(readRequest, cancellationToken)
                .ConfigureAwait(false);

            var credScope = scope == ModelProviderBindingScope.Platform
                ? ModelProviderCredentialScope.Platform
                : ModelProviderCredentialScope.Project;
            var claim = await store.ReadWriteClaimByOperationAsync(operationId, cancellationToken)
                .ConfigureAwait(false);
            var disable = await store.ReadDisableByOperationAsync(operationId, cancellationToken)
                .ConfigureAwait(false);

            if (claim is not null)
            {
                if (claim.Scope != credScope ||
                    claim.ProjectId != projectId ||
                    !StringComparer.Ordinal.Equals(claim.ProviderId, providerId) ||
                    !StringComparer.Ordinal.Equals(claim.ActorId, caller.ActorId) ||
                    !StringComparer.Ordinal.Equals(claim.Issuer, caller.Issuer))
                    return Error("credential_slot_conflict", 409);

                if (claim.Tenant is not null &&
                    !StringComparer.Ordinal.Equals(claim.Tenant, receiptRead.ResolvedTenant))
                    return Error("forbidden", 403);
            }

            if (disable is not null &&
                (disable.Scope != credScope ||
                 disable.ProjectId != projectId ||
                 !StringComparer.Ordinal.Equals(disable.ProviderId, providerId) ||
                 !StringComparer.Ordinal.Equals(disable.ActorId, caller.ActorId)))
                return Error("credential_slot_conflict", 409);

            var responseScope = scope == ModelProviderBindingScope.Platform ? "platform" : "project";

            if (claim is null)
            {
                if (disable is not null && receiptRead.Outcome == "disabled")
                {
                    return Results.Ok(new CredentialOperationStatusResponse(
                        operationId, responseScope, projectId, providerId, "disabled",
                        receiptRead.ProviderRevision));
                }
                return Error("credential_operation_not_found", 404);
            }

            // Require Broker completion before returning terminal published/disabled status.
            switch (claim.State)
            {
                case ModelProviderCredentialWriteClaimState.Published
                    when receiptRead.Outcome == "published" &&
                         receiptRead.PublicationReceipt is { } receipt &&
                         claim.ExpectedProviderRevision == receipt.ProviderRevision - 1 &&
                         receipt.OperationId == operationId &&
                         receipt.Scope == scope &&
                         receipt.ProjectId == projectId &&
                         StringComparer.Ordinal.Equals(receipt.ProviderId, providerId):
                    return Results.Ok(new CredentialOperationStatusResponse(
                        operationId, responseScope, projectId, providerId, "published",
                        receipt.ProviderRevision, true));

                case ModelProviderCredentialWriteClaimState.Published:
                case ModelProviderCredentialWriteClaimState.PublicationPending:
                case ModelProviderCredentialWriteClaimState.Completed:
                    return Results.Ok(new CredentialOperationStatusResponse(
                        operationId, responseScope, projectId, providerId, "publication_pending"));

                case ModelProviderCredentialWriteClaimState.Quarantined:
                    return Results.Ok(new CredentialOperationStatusResponse(
                        operationId, responseScope, projectId, providerId, "quarantined"));

                case ModelProviderCredentialWriteClaimState.DisablePending:
                    return Results.Ok(new CredentialOperationStatusResponse(
                        operationId, responseScope, projectId, providerId, "disable_pending"));

                case ModelProviderCredentialWriteClaimState.Disabled
                    when receiptRead.Outcome == "disabled":
                    return Results.Ok(new CredentialOperationStatusResponse(
                        operationId, responseScope, projectId, providerId, "disabled",
                        receiptRead.ProviderRevision));

                case ModelProviderCredentialWriteClaimState.Disabled:
                    return Results.Ok(new CredentialOperationStatusResponse(
                        operationId, responseScope, projectId, providerId, "disable_pending"));

                case ModelProviderCredentialWriteClaimState.Cancelled:
                    return Results.Ok(new CredentialOperationStatusResponse(
                        operationId, responseScope, projectId, providerId, "cancelled"));

                default:
                    return Results.Ok(new CredentialOperationStatusResponse(
                        operationId, responseScope, projectId, providerId, "pending"));
            }
        }
        catch (BrokerCredentialPublicationV2Exception ex)
        {
            return MapPublicationException(ex);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Results.StatusCode(499);
        }
    }

    private static async Task<bool> HasBodyAsync(Stream body, CancellationToken cancellationToken)
    {
        var probe = new byte[1];
        return await body.ReadAsync(probe, cancellationToken).ConfigureAwait(false) > 0;
    }

    private static async Task<IResult> HandlePutAsync(
        HttpContext context,
        ModelProviderBindingScope scope,
        string? projectId,
        string providerId,
        IdentityBrokerOptions brokerOptions,
        ModelProviderCredentialStore store,
        IModelProviderByokVault byokVault,
        BrokerModelProviderCredentialPublicationV2Client publicationClient,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        SetNoStore(context);
        if (!TryValidateCaller(context, brokerOptions, scope, projectId, out var caller, out var authError))
            return authError!;
        if (!IsValidIdentifier(providerId, 128) ||
            (scope == ModelProviderBindingScope.Project && !IsValidIdentifier(projectId, 32)))
            return Error("credential_request_invalid", 400);
        if (context.Request.Query.Count != 0 ||
            !string.Equals(context.Request.ContentType?.Split(';', 2)[0], "application/json", StringComparison.OrdinalIgnoreCase))
            return Error("credential_request_invalid", 400);

        var bodyBytes = await ReadBoundedAsync(context.Request, MaximumPutBodyBytes, cancellationToken)
            .ConfigureAwait(false);
        if (bodyBytes is null)
            return Error("request_body_too_large", 413);
        if (bodyBytes.Length == 0)
            return Error("credential_request_invalid", 400);

        PutCredentialBody? body;
        try
        {
            using var document = JsonDocument.Parse(bodyBytes, new JsonDocumentOptions { MaxDepth = JsonOptions.MaxDepth });
            if (document.RootElement.ValueKind != JsonValueKind.Object || HasDuplicateProperties(document.RootElement))
                return Error("credential_request_invalid", 400);
            body = JsonSerializer.Deserialize<PutCredentialBody>(document.RootElement.GetRawText(), JsonOptions);
        }
        catch (JsonException)
        {
            return Error("credential_request_invalid", 400);
        }

        if (body is null)
            return Error("credential_request_invalid", 400);

        if (body.KeyVaultSecret is not null || body.KeyVaultVersion is not null || body.VaultSource is not null)
            return Error("existing_vault_reference_not_supported", 400);

        if (body.OperationId == Guid.Empty ||
            body.ExpectedProviderRevision is <= 0 or long.MaxValue ||
            body.ExpectedCredentialRevision < 0 || body.ExpectedCredentialRevision == long.MaxValue)
            return Error("credential_request_invalid", 400);

        var hasApiKey = !string.IsNullOrWhiteSpace(body.ApiKey);
        var hasRecovery = body.RecoverySecretVersion is not null;
        var hasRecheck = body.RecheckExistingVersion == true;
        var modeCount = (hasApiKey ? 1 : 0) + (hasRecovery ? 1 : 0) + (hasRecheck ? 1 : 0);
        if (modeCount != 1 ||
            (body.ApiKey is not null && string.IsNullOrWhiteSpace(body.ApiKey)) ||
            (hasRecovery && !IsHexVersion(body.RecoverySecretVersion!)))
            return Error("credential_request_invalid", 400);

        var credScope = scope == ModelProviderBindingScope.Platform
            ? ModelProviderCredentialScope.Platform
            : ModelProviderCredentialScope.Project;
        var secretName = $"byok-{scope.ToString().ToLowerInvariant()}-{providerId}";
        if (secretName.Length > 127 || secretName.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            return Error("credential_request_invalid", 400);

        try
        {
            // Check existing claim first so cancelled, published, publication-pending, and quarantined
            // operations never re-send a Vault write.
            var existingClaim = await store.ReadWriteClaimByOperationAsync(body.OperationId, cancellationToken)
                .ConfigureAwait(false);
            if (existingClaim is not null)
            {
                if (existingClaim.Purpose == "credential-disable" ||
                    existingClaim.State is ModelProviderCredentialWriteClaimState.DisablePending or ModelProviderCredentialWriteClaimState.Disabled)
                    return Error("credential_slot_conflict", 409);

                if (existingClaim.ExpectedProviderRevision is null ||
                    existingClaim.Scope != credScope ||
                    existingClaim.ProjectId != projectId ||
                    !StringComparer.Ordinal.Equals(existingClaim.ProviderId, providerId) ||
                    !StringComparer.Ordinal.Equals(existingClaim.ActorId, caller!.ActorId) ||
                    !StringComparer.Ordinal.Equals(existingClaim.Issuer, caller.Issuer) ||
                    existingClaim.ExpectedProviderRevision != body.ExpectedProviderRevision ||
                    existingClaim.ExpectedRevision != body.ExpectedCredentialRevision)
                    return Error("credential_slot_conflict", 409);

                var authPreflight = await publicationClient.PreflightAsync(
                    new ModelProviderCredentialPublicationPreflightRequest(
                        body.OperationId, scope, projectId, providerId,
                        caller.Issuer, caller.ActorId, caller.TenantSelector, caller.TenantClaim,
                        caller.BoundProjectId, "authority-only"),
                    cancellationToken).ConfigureAwait(false);
                if (!StringComparer.Ordinal.Equals(authPreflight.ResolvedTenant, existingClaim.Tenant))
                    return Error("forbidden", 403);

                var pubClaim = ToPublicationClaim(
                    existingClaim,
                    hasRecheck && existingClaim.ExpectedRevision > 0);

                if (existingClaim.State == ModelProviderCredentialWriteClaimState.Cancelled)
                    return Error("credential_operation_cancelled", 409);

                if (existingClaim.State == ModelProviderCredentialWriteClaimState.Published)
                {
                    var replay = await store.ClaimPublicationWriteAsync(pubClaim, cancellationToken)
                        .ConfigureAwait(false);
                    return Results.Ok(ToResponse(body.OperationId, "published", scope, projectId, providerId,
                        replay.PublishedProviderRevision, replay.Credential?.Revision,
                        replay.Credential?.Reference.Version, replay.PublishedConfigurationHash,
                        replay.PublishedProviderState));
                }

                if (existingClaim.State == ModelProviderCredentialWriteClaimState.PublicationPending)
                {
                    var replay = await store.ClaimPublicationWriteAsync(pubClaim, cancellationToken)
                        .ConfigureAwait(false);
                    return await PublishAndAcknowledgeAsync(
                        pubClaim, replay.Credential!, caller, store, publicationClient, cancellationToken)
                        .ConfigureAwait(false);
                }

                if (existingClaim.State == ModelProviderCredentialWriteClaimState.Quarantined ||
                    existingClaim.DispatchedAt is not null)
                {
                    if (existingClaim.State == ModelProviderCredentialWriteClaimState.Claimed)
                        _ = await store.MarkPublicationWriteUncertainAsync(pubClaim, cancellationToken)
                            .ConfigureAwait(false);

                    if (!hasRecovery)
                    {
                        return Results.Json(new
                        {
                            code = "credential_write_quarantined",
                            operationId = body.OperationId,
                            status = "quarantined",
                        }, statusCode: 409);
                    }

                    if (!byokVault.IsConfigured)
                        return Error("credential_vault_unavailable", 503);

                    var verified = await byokVault.VerifyRecoveryVersionAsync(
                        secretName, body.RecoverySecretVersion!, body.OperationId,
                        existingClaim.CredentialSlotId, cancellationToken).ConfigureAwait(false);
                    if (verified is null)
                        return Error("credential_recovery_evidence_invalid", 409);

                    var recovered = await store.RecoverPublicationWriteAsync(pubClaim, verified, cancellationToken)
                        .ConfigureAwait(false);
                    return await PublishAndAcknowledgeAsync(
                        pubClaim, recovered.Credential!, caller, store, publicationClient, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            // New operation (or Claimed with DispatchedAt == null before any Vault dispatch).
            if ((!hasApiKey && !hasRecheck) ||
                (hasRecheck && body.ExpectedCredentialRevision <= 0))
                return Error("credential_request_invalid", 400);
            if (!byokVault.IsConfigured)
                return Error("credential_vault_unavailable", 503);

            if (hasRecheck)
            {
                if (await store.HasActiveWriteClaimForProviderAsync(providerId, cancellationToken)
                        .ConfigureAwait(false))
                    return Error("credential_slot_conflict", 409);

                var storedHead = await store.ReadHeadByProviderAsync(credScope, projectId, providerId, cancellationToken)
                    .ConfigureAwait(false);
                if (storedHead is null)
                    return Error("credential_slot_not_found", 404);
                if (storedHead.State != ModelProviderCredentialState.Enabled ||
                    storedHead.Revision != body.ExpectedCredentialRevision)
                    return Error("credential_slot_conflict", 409);

                var recheckPreflight = await publicationClient.PreflightAsync(
                    new ModelProviderCredentialPublicationPreflightRequest(
                        body.OperationId, scope, projectId, providerId,
                        caller!.Issuer, caller.ActorId, caller.TenantSelector, caller.TenantClaim,
                        caller.BoundProjectId, "draft-snapshot"),
                    cancellationToken).ConfigureAwait(false);
                if (recheckPreflight.IsCancelled)
                    return Error("credential_operation_cancelled", 409);
                if (recheckPreflight.ProviderRevision != body.ExpectedProviderRevision)
                    return Error("model_provider_draft_conflict", 409);

                var verifiedStored = await byokVault.VerifyStoredVersionAsync(
                    storedHead.SecretName, storedHead.CurrentVersion, storedHead.CredentialSlotId, cancellationToken)
                    .ConfigureAwait(false);
                if (verifiedStored is null)
                    return Error("credential_slot_conflict", 409);

                var recheckClaim = new ClaimModelProviderCredentialPublication(
                    new ClaimModelProviderCredentialWrite(
                        body.OperationId,
                        body.ExpectedCredentialRevision,
                        storedHead.CredentialSlotId,
                        credScope,
                        projectId,
                        providerId,
                        storedHead.VaultSource,
                        storedHead.SecretName,
                        caller.ActorId),
                    body.ExpectedProviderRevision,
                    BrokerModelProviderCredentialPublicationClient.Purpose,
                    caller.Issuer,
                    recheckPreflight.ResolvedTenant,
                    IsRecheck: true);

                var stagedRecheck = await store.ClaimRecheckPublicationAsync(
                    recheckClaim, verifiedStored, cancellationToken).ConfigureAwait(false);
                if (stagedRecheck.Status == ModelProviderCredentialWriteClaimStatus.Cancelled)
                    return Error("credential_operation_cancelled", 409);

                return await PublishAndAcknowledgeAsync(
                    recheckClaim, stagedRecheck.Credential!, caller, store, publicationClient, cancellationToken)
                    .ConfigureAwait(false);
            }

            var preflight1 = await publicationClient.PreflightAsync(
                new ModelProviderCredentialPublicationPreflightRequest(
                    body.OperationId, scope, projectId, providerId,
                    caller!.Issuer, caller.ActorId, caller.TenantSelector, caller.TenantClaim,
                    caller.BoundProjectId, "draft-snapshot"),
                cancellationToken).ConfigureAwait(false);

            if (preflight1.IsCancelled)
                return Error("credential_operation_cancelled", 409);
            if (preflight1.ProviderRevision != body.ExpectedProviderRevision)
                return Error("model_provider_draft_conflict", 409);

            var existingHead = await store.ReadHeadByProviderAsync(credScope, projectId, providerId, cancellationToken)
                .ConfigureAwait(false);
            var slotId = existingHead?.CredentialSlotId ?? preflight1.CredentialSlotId ?? Guid.NewGuid();
            var pubRequest = new ClaimModelProviderCredentialPublication(
                new ClaimModelProviderCredentialWrite(
                    body.OperationId,
                    body.ExpectedCredentialRevision,
                    slotId,
                    credScope,
                    projectId,
                    providerId,
                    ModelProviderCredentialVaultSource.Byok,
                    secretName,
                    caller.ActorId),
                body.ExpectedProviderRevision,
                BrokerModelProviderCredentialPublicationClient.Purpose,
                caller.Issuer,
                preflight1.ResolvedTenant);

            var claimResult = await store.ClaimPublicationWriteAsync(pubRequest, cancellationToken)
                .ConfigureAwait(false);
            if (claimResult.Status == ModelProviderCredentialWriteClaimStatus.Cancelled)
                return Error("credential_operation_cancelled", 409);
            if (claimResult.Status is ModelProviderCredentialWriteClaimStatus.Quarantined)
                return Results.Json(new
                {
                    code = "credential_write_quarantined",
                    operationId = body.OperationId,
                    status = "quarantined",
                }, statusCode: 409);

            // Second preflight immediately before Vault dispatch.
            var preflight2 = await publicationClient.PreflightAsync(
                new ModelProviderCredentialPublicationPreflightRequest(
                    body.OperationId, scope, projectId, providerId,
                    caller.Issuer, caller.ActorId, caller.TenantSelector, caller.TenantClaim,
                    caller.BoundProjectId, "draft-snapshot"),
                cancellationToken).ConfigureAwait(false);
            if (preflight2.IsCancelled)
                return Error("credential_operation_cancelled", 409);
            if (!StringComparer.Ordinal.Equals(preflight2.ResolvedTenant, preflight1.ResolvedTenant) ||
                preflight2.ProviderRevision != body.ExpectedProviderRevision)
                return Error("model_provider_draft_conflict", 409);

            // Commit dispatch marker before calling Key Vault so an ambiguous crash never retries Vault.
            await store.MarkPublicationDispatchedAsync(pubRequest, cancellationToken).ConfigureAwait(false);

            SecretRef writtenRef;
            var secretCredential = new SecretCredential(
                body.ApiKey!, timeProvider.GetUtcNow().Add(ByokCredentialExpiry), timeProvider);
            try
            {
                writtenRef = await byokVault.WriteVersionAsync(
                    secretName, secretCredential, body.OperationId, slotId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                _ = await store.MarkPublicationWriteUncertainAsync(pubRequest, CancellationToken.None)
                    .ConfigureAwait(false);
                return Results.Json(new
                {
                    code = "credential_write_uncertain",
                    operationId = body.OperationId,
                    status = "quarantined",
                }, statusCode: 503);
            }
            finally
            {
                secretCredential.Invalidate();
            }

            var pending = await store.CompletePublicationWriteAsync(pubRequest, writtenRef, cancellationToken)
                .ConfigureAwait(false);
            return await PublishAndAcknowledgeAsync(
                pubRequest, pending.Credential!, caller, store, publicationClient, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ModelProviderCredentialException ex)
        {
            return MapStoreException(ex);
        }
        catch (BrokerCredentialPublicationV2Exception ex)
        {
            return MapPublicationException(ex);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Results.StatusCode(499);
        }
    }

    private static async Task<IResult> HandleDeleteAsync(
        HttpContext context,
        ModelProviderBindingScope scope,
        string? projectId,
        string providerId,
        IdentityBrokerOptions brokerOptions,
        ModelProviderCredentialStore store,
        BrokerModelProviderCredentialPublicationV2Client publicationClient,
        CancellationToken cancellationToken)
    {
        SetNoStore(context);
        if (!TryValidateCaller(context, brokerOptions, scope, projectId, out var caller, out var authError))
            return authError!;
        if (!IsValidIdentifier(providerId, 128) ||
            (scope == ModelProviderBindingScope.Project && !IsValidIdentifier(projectId, 32)))
            return Error("credential_request_invalid", 400);

        DeleteCredentialBody? body = null;
        if (context.Request.ContentLength is > 0 ||
            context.Request.Headers.ContainsKey("Transfer-Encoding") ||
            string.Equals(context.Request.ContentType?.Split(';', 2)[0], "application/json", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = await ReadBoundedAsync(context.Request, MaximumDeleteBodyBytes, cancellationToken)
                .ConfigureAwait(false);
            if (bytes is null)
                return Error("request_body_too_large", 413);
            if (bytes.Length > 0)
            {
                try
                {
                    using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = JsonOptions.MaxDepth });
                    if (doc.RootElement.ValueKind != JsonValueKind.Object || HasDuplicateProperties(doc.RootElement))
                        return Error("credential_request_invalid", 400);
                    body = JsonSerializer.Deserialize<DeleteCredentialBody>(doc.RootElement.GetRawText(), JsonOptions);
                }
                catch (JsonException)
                {
                    return Error("credential_request_invalid", 400);
                }
            }
        }

        Guid operationId;
        long? expectedProviderRevision = body?.ExpectedProviderRevision;
        long? expectedCredentialRevision = body?.ExpectedCredentialRevision;
        var cancelRequested = body?.Cancel ?? false;

        if (body is not null)
        {
            operationId = body.OperationId;
        }
        else
        {
            if (!Guid.TryParseExact(context.Request.Query["operationId"].FirstOrDefault(), "D", out operationId))
                return Error("credential_request_invalid", 400);
            if (context.Request.Query.TryGetValue("expectedProviderRevision", out var prValues) &&
                prValues.Count == 1 &&
                long.TryParse(prValues[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pr))
                expectedProviderRevision = pr;
            if (context.Request.Query.TryGetValue("expectedCredentialRevision", out var crValues) &&
                crValues.Count == 1 &&
                long.TryParse(crValues[0], NumberStyles.None, CultureInfo.InvariantCulture, out var cr))
                expectedCredentialRevision = cr;
            if (context.Request.Query.TryGetValue("cancel", out var cValues) &&
                cValues.Count == 1 &&
                bool.TryParse(cValues[0], out var cancelBool))
                cancelRequested = cancelBool;
        }

        if (operationId == Guid.Empty ||
            expectedProviderRevision is <= 0 or long.MaxValue ||
            expectedCredentialRevision is <= 0 or long.MaxValue)
            return Error("credential_request_invalid", 400);

        var credScope = scope == ModelProviderBindingScope.Platform
            ? ModelProviderCredentialScope.Platform
            : ModelProviderCredentialScope.Project;

        try
        {
            var existingClaim = await store.ReadWriteClaimByOperationAsync(operationId, cancellationToken)
                .ConfigureAwait(false);
            if (existingClaim is not null && existingClaim.Purpose != "credential-disable")
            {
                if (!cancelRequested)
                    return Error("credential_slot_conflict", 409);

                if (existingClaim.ExpectedProviderRevision is null ||
                    existingClaim.Scope != credScope ||
                    existingClaim.ProjectId != projectId ||
                    !StringComparer.Ordinal.Equals(existingClaim.ProviderId, providerId) ||
                    !StringComparer.Ordinal.Equals(existingClaim.ActorId, caller!.ActorId) ||
                    !StringComparer.Ordinal.Equals(existingClaim.Issuer, caller.Issuer))
                    return Error("credential_slot_conflict", 409);

                var authPreflight = await publicationClient.PreflightAsync(
                    new ModelProviderCredentialPublicationPreflightRequest(
                        operationId, scope, projectId, providerId,
                        caller.Issuer, caller.ActorId, caller.TenantSelector, caller.TenantClaim,
                        caller.BoundProjectId, "authority-only"),
                    cancellationToken).ConfigureAwait(false);
                if (!StringComparer.Ordinal.Equals(authPreflight.ResolvedTenant, existingClaim.Tenant))
                    return Error("forbidden", 403);

                var pubClaim = ToPublicationClaim(existingClaim);

                if (existingClaim.State == ModelProviderCredentialWriteClaimState.Published)
                {
                    var replay = await store.ClaimPublicationWriteAsync(pubClaim, cancellationToken)
                        .ConfigureAwait(false);
                    return Results.Ok(ToResponse(operationId, "published", scope, projectId, providerId,
                        replay.PublishedProviderRevision, replay.Credential?.Revision,
                        replay.Credential?.Reference.Version, replay.PublishedConfigurationHash));
                }

                if (existingClaim.State == ModelProviderCredentialWriteClaimState.Cancelled)
                {
                    return Results.Ok(ToResponse(operationId, "cancelled", scope, projectId, providerId,
                        null, null, null, null));
                }

                var cancelResult = await publicationClient.CancelAsync(
                    new ModelProviderCredentialCancelRequest(
                        operationId, authPreflight.PreflightId, scope, projectId, providerId,
                        caller.Issuer, caller.ActorId, existingClaim.Tenant!,
                        authPreflight.MembershipId, authPreflight.MembershipRevision,
                        caller.TenantSelector, caller.TenantClaim),
                    cancellationToken).ConfigureAwait(false);

                if (cancelResult.Outcome == "published" && cancelResult.Receipt is { } receipt)
                {
                    var reconciled = await store.ReconcilePublishedFromReceiptAsync(
                        pubClaim, receipt, cancellationToken).ConfigureAwait(false);
                    return Results.Ok(ToResponse(operationId, "published", scope, projectId, providerId,
                        reconciled.PublishedProviderRevision, reconciled.Credential?.Revision,
                        reconciled.Credential?.Reference.Version, reconciled.PublishedConfigurationHash));
                }

                _ = await store.CancelPublicationClaimAsync(pubClaim, cancellationToken).ConfigureAwait(false);
                return Results.Ok(ToResponse(operationId, "cancelled", scope, projectId, providerId,
                    null, null, null, null));
            }

            if (cancelRequested)
            {
                if (existingClaim is null)
                    return Error("credential_slot_not_found", 404);
                if (existingClaim.Scope != credScope ||
                    existingClaim.ProjectId != projectId ||
                    !StringComparer.Ordinal.Equals(existingClaim.ProviderId, providerId) ||
                    !StringComparer.Ordinal.Equals(existingClaim.ActorId, caller!.ActorId) ||
                    !StringComparer.Ordinal.Equals(existingClaim.Issuer, caller.Issuer) ||
                    (expectedProviderRevision is not null && existingClaim.ExpectedProviderRevision != expectedProviderRevision) ||
                    (expectedCredentialRevision is not null && existingClaim.ExpectedRevision != expectedCredentialRevision))
                    return Error("credential_slot_conflict", 409);

                var authPreflight = await publicationClient.PreflightAsync(
                    new ModelProviderCredentialPublicationPreflightRequest(
                        operationId, scope, projectId, providerId,
                        caller.Issuer, caller.ActorId, caller.TenantSelector, caller.TenantClaim,
                        caller.BoundProjectId, "authority-only"),
                    cancellationToken).ConfigureAwait(false);
                if (existingClaim.Tenant is not null &&
                    !StringComparer.Ordinal.Equals(authPreflight.ResolvedTenant, existingClaim.Tenant))
                    return Error("forbidden", 403);

                if (existingClaim.State == ModelProviderCredentialWriteClaimState.Disabled)
                    return Error("credential_slot_conflict", 409);

                if (existingClaim.State == ModelProviderCredentialWriteClaimState.Cancelled)
                {
                    return Results.Ok(ToResponse(operationId, "cancelled", scope, projectId, providerId,
                        null, null, null, null));
                }

                var cancelResult = await publicationClient.CancelAsync(
                    new ModelProviderCredentialCancelRequest(
                        operationId, authPreflight.PreflightId, scope, projectId, providerId,
                        caller.Issuer, caller.ActorId, authPreflight.ResolvedTenant,
                        authPreflight.MembershipId, authPreflight.MembershipRevision,
                        caller.TenantSelector, caller.TenantClaim),
                    cancellationToken).ConfigureAwait(false);

                if (!string.Equals(cancelResult.Outcome, "cancelled", StringComparison.Ordinal))
                    return Error("credential_slot_conflict", 409);

                await store.CancelDisableClaimAsync(
                    operationId, authPreflight.ResolvedTenant, existingClaim.RequestFingerprint, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Ok(ToResponse(operationId, "cancelled", scope, projectId, providerId,
                    null, null, null, null));
            }

            var claim = await store.ClaimDisableAsync(
                new ClaimModelProviderCredentialDisable(
                    operationId, expectedProviderRevision, expectedCredentialRevision,
                    credScope, projectId, providerId, caller!.ActorId, caller.Issuer),
                cancellationToken).ConfigureAwait(false);

            if (claim.Status is ModelProviderCredentialWriteClaimStatus.Disabled or ModelProviderCredentialWriteClaimStatus.Cancelled)
            {
                var authPreflight = await publicationClient.PreflightAsync(
                    new ModelProviderCredentialPublicationPreflightRequest(
                        operationId, scope, projectId, providerId,
                        caller.Issuer, caller.ActorId, caller.TenantSelector, caller.TenantClaim,
                        caller.BoundProjectId, "authority-only"),
                    cancellationToken).ConfigureAwait(false);
                if (existingClaim?.Tenant is not null &&
                    !StringComparer.Ordinal.Equals(authPreflight.ResolvedTenant, existingClaim.Tenant))
                    return Error("forbidden", 403);

                if (claim.Status == ModelProviderCredentialWriteClaimStatus.Cancelled)
                    return Error("credential_operation_cancelled", 409);

                return Results.Ok(ToResponse(operationId, "disabled", scope, projectId, providerId,
                    claim.PublishedProviderRevision, claim.CompletedCredentialRevision,
                    null, claim.PublishedConfigurationHash, "Draft"));
            }

            // Projects blocks new pins under the run-selection provider lock and returns durable receipt.
            var disableReceipt = await publicationClient.DisableAsync(
                new ModelProviderCredentialDisableRequest(
                    operationId, expectedProviderRevision, claim.OriginalCredentialRevision,
                    claim.CredentialSlotId, claim.CurrentVersion,
                    scope, projectId, providerId,
                    caller.Issuer, caller.ActorId, caller.TenantSelector, caller.TenantClaim),
                cancellationToken).ConfigureAwait(false);

            if (claim.Status == ModelProviderCredentialWriteClaimStatus.Completed)
            {
                return Results.Ok(ToResponse(operationId, "disabled", scope, projectId, providerId,
                    disableReceipt.ProviderRevision, claim.CompletedCredentialRevision,
                    null, disableReceipt.ConfigurationHash, "Draft"));
            }

            var slotId = disableReceipt.CredentialSlotId ?? claim.CredentialSlotId;
            if (slotId == Guid.Empty)
                return Error("credential_slot_not_found", 404);

            var completed = await store.CompleteDisableAsync(
                new CompleteModelProviderCredentialDisable(
                    operationId, disableReceipt.OriginalCredentialRevision, slotId,
                    credScope, projectId, providerId, caller.ActorId, claim.RequestFingerprint,
                    disableReceipt.ProviderRevision, disableReceipt.ConfigurationHash, disableReceipt.ResolvedTenant),
                cancellationToken).ConfigureAwait(false);

            return Results.Ok(ToResponse(operationId, "disabled", scope, projectId, providerId,
                completed.PublishedProviderRevision, completed.CompletedCredentialRevision,
                null, completed.PublishedConfigurationHash, "Draft"));
        }
        catch (ModelProviderCredentialException ex)
        {
            return MapStoreException(ex);
        }
        catch (BrokerCredentialPublicationV2Exception ex)
        {
            return MapPublicationException(ex);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Results.StatusCode(499);
        }
    }

    private static async Task<IResult> PublishAndAcknowledgeAsync(
        ClaimModelProviderCredentialPublication pubClaim,
        ModelProviderCredentialReference credential,
        ValidatedCaller caller,
        ModelProviderCredentialStore store,
        BrokerModelProviderCredentialPublicationV2Client publicationClient,
        CancellationToken cancellationToken)
    {
        var preflight = await publicationClient.PreflightAsync(
            new ModelProviderCredentialPublicationPreflightRequest(
                pubClaim.Claim.OperationId,
                credential.Identity.Scope == ModelProviderCredentialScope.Platform
                    ? ModelProviderBindingScope.Platform
                    : ModelProviderBindingScope.Project,
                credential.Identity.ProjectId,
                credential.Identity.ProviderId,
                caller.Issuer, caller.ActorId, caller.TenantSelector, caller.TenantClaim,
                caller.BoundProjectId, "authority-only"),
            cancellationToken).ConfigureAwait(false);
        if (!StringComparer.Ordinal.Equals(preflight.ResolvedTenant, pubClaim.Tenant))
            return Error("forbidden", 403);

        var scope = credential.Identity.Scope == ModelProviderCredentialScope.Platform
            ? ModelProviderBindingScope.Platform
            : ModelProviderBindingScope.Project;
        ModelProviderCredentialPublicationReceipt receipt;
        try
        {
            receipt = await publicationClient.PublishAsync(
                new ModelProviderCredentialPublicationV2Request(
                    pubClaim.Claim.OperationId,
                    preflight.PreflightId,
                    pubClaim.ExpectedProviderRevision,
                    scope,
                    credential.Identity.ProjectId,
                    credential.Identity.ProviderId,
                    credential.Reference.Version,
                    new ModelCredentialIdentity(
                        credential.Identity.CredentialSlotId,
                        scope,
                        credential.Identity.ProjectId,
                        credential.Identity.ProviderId),
                    credential.Reference,
                    caller.Issuer,
                    caller.ActorId,
                    pubClaim.Tenant,
                    preflight.MembershipId,
                    preflight.MembershipRevision,
                    caller.TenantSelector,
                    caller.TenantClaim),
                cancellationToken).ConfigureAwait(false);
        }
        catch (BrokerCredentialPublicationV2Exception ex) when (ex.StatusCode is null or HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway)
        {
            return Results.Json(new
            {
                code = "credential_publication_pending",
                operationId = pubClaim.Claim.OperationId,
                status = "publication_pending",
                credentialRevision = credential.Revision,
                credentialVersion = credential.Reference.Version,
            }, statusCode: 503);
        }

        var ack = await store.AcknowledgePublicationAsync(pubClaim, receipt, cancellationToken)
            .ConfigureAwait(false);
        return Results.Ok(ToResponse(
            pubClaim.Claim.OperationId,
            "published",
            scope,
            credential.Identity.ProjectId,
            credential.Identity.ProviderId,
            ack.PublishedProviderRevision,
            ack.Credential?.Revision,
            ack.Credential?.Reference.Version,
            ack.PublishedConfigurationHash,
            ack.PublishedProviderState ?? receipt.ProviderState));
    }

    private static ClaimModelProviderCredentialPublication ToPublicationClaim(
        ModelProviderCredentialWriteClaim claim,
        bool isRecheck = false) =>
        new(
            new ClaimModelProviderCredentialWrite(
                claim.OperationId,
                claim.ExpectedRevision,
                claim.CredentialSlotId,
                claim.Scope,
                claim.ProjectId,
                claim.ProviderId,
                claim.VaultSource,
                claim.SecretName,
                claim.ActorId),
            claim.ExpectedProviderRevision!.Value,
            claim.Purpose!,
            claim.Issuer!,
            claim.Tenant!,
            isRecheck);

    private static CredentialOperationResponse ToResponse(
        Guid operationId,
        string status,
        ModelProviderBindingScope scope,
        string? projectId,
        string providerId,
        long? providerRevision,
        long? credentialRevision,
        string? credentialVersion,
        string? configurationHash,
        string? providerState = null) =>
        new(
            operationId,
            status,
            scope == ModelProviderBindingScope.Platform ? "platform" : "project",
            projectId,
            providerId,
            providerRevision,
            credentialRevision,
            credentialVersion,
            configurationHash,
            providerState);

    private sealed record ValidatedCaller(
        string Issuer,
        string ActorId,
        string? TenantSelector,
        string? TenantClaim,
        string? BoundProjectId);

    private static bool TryValidateCaller(
        HttpContext context,
        IdentityBrokerOptions brokerOptions,
        ModelProviderBindingScope scope,
        string? projectId,
        out ValidatedCaller? caller,
        out IResult? error)
    {
        caller = null;
        error = null;
        var principal = context.User;
        if (principal.Identity?.IsAuthenticated != true)
        {
            error = Results.Unauthorized();
            return false;
        }

        var sub = ReadSingle(principal, "sub");
        if (sub is null || !IsValidIdentifier(sub, 256))
        {
            error = Error("forbidden", 403);
            return false;
        }

        var scopes = principal.FindAll("scope").Concat(principal.FindAll("scp"))
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToHashSet(StringComparer.Ordinal);
        if (!scopes.Contains("api.read") || !scopes.Contains("projects.admin"))
        {
            error = Error("forbidden", 403);
            return false;
        }

        if (principal.FindAll("run_id").Any() || principal.FindAll("purpose").Any())
        {
            error = Error("forbidden", 403);
            return false;
        }

        var boundProject = ReadSingle(principal, "project_id");
        if (principal.FindAll("project_id").Skip(1).Any())
        {
            error = Error("forbidden", 403);
            return false;
        }

        if (scope == ModelProviderBindingScope.Platform)
        {
            if (boundProject is not null)
            {
                error = Error("forbidden", 403);
                return false;
            }
        }
        else
        {
            if (!StringComparer.Ordinal.Equals(boundProject, projectId))
            {
                error = Error("forbidden", 403);
                return false;
            }
        }

        var tenantClaims = principal.FindAll("tenant_id").Concat(principal.FindAll("tid"))
            .Select(c => c.Value).ToArray();
        if (tenantClaims.Length > 1 ||
            (tenantClaims.Length == 1 && !IsValidIdentifier(tenantClaims[0], 256)))
        {
            error = Error("forbidden", 403);
            return false;
        }

        var selectorHeaders = context.Request.Headers["X-Agentweaver-Tenant"];
        if (selectorHeaders.Count > 1)
        {
            error = Error("forbidden", 403);
            return false;
        }
        var selector = selectorHeaders.Count == 1 ? selectorHeaders[0] : null;
        if (selector is not null && !IsValidIdentifier(selector, 256))
        {
            error = Error("forbidden", 403);
            return false;
        }

        caller = new ValidatedCaller(
            new Uri(brokerOptions.Issuer).AbsoluteUri,
            sub,
            selector,
            tenantClaims.Length == 1 ? tenantClaims[0] : null,
            boundProject);
        return true;
    }

    private static IResult MapStoreException(ModelProviderCredentialException ex) => ex.Code switch
    {
        "credential_slot_conflict" => Error(ex.Code, 409),
        "credential_vault_unavailable" => Error(ex.Code, 503),
        _ => Error(ex.Code, 400),
    };

    private static IResult MapPublicationException(BrokerCredentialPublicationV2Exception ex) =>
        ex.StatusCode switch
        {
            HttpStatusCode.BadRequest => Error(ex.ErrorCode ?? "credential_request_invalid", 400),
            HttpStatusCode.Unauthorized => Error(ex.ErrorCode ?? "unauthorized", 401),
            HttpStatusCode.Forbidden => Error(ex.ErrorCode ?? "forbidden", 403),
            HttpStatusCode.NotFound => Error(ex.ErrorCode ?? "model_provider_draft_not_found", 404),
            HttpStatusCode.Conflict => Error(ex.ErrorCode ?? "model_provider_draft_conflict", 409),
            HttpStatusCode.RequestEntityTooLarge => Error(ex.ErrorCode ?? "request_body_too_large", 413),
            _ => Error(ex.ErrorCode ?? "credential_publication_unavailable", 503),
        };

    private static IResult Error(string code, int statusCode) =>
        Results.Json(new { code }, statusCode: statusCode);

    private static void SetNoStore(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers.XContentTypeOptions = "nosniff";
    }

    private static string? ReadSingle(ClaimsPrincipal principal, string claimType)
    {
        var claims = principal.FindAll(claimType).Take(2).ToArray();
        return claims.Length == 1 ? claims[0].Value : null;
    }

    private static bool IsValidIdentifier(string? value, int maxLength) =>
        value is { Length: > 0 } &&
        value.Length <= maxLength &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ':');

    private static bool IsHexVersion(string value) =>
        value is { Length: 32 } && value.All(Uri.IsHexDigit);

    private static bool HasDuplicateProperties(JsonElement element)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prop in element.EnumerateObject())
        {
            if (!names.Add(prop.Name))
                return true;
            if (prop.Value.ValueKind == JsonValueKind.Object && HasDuplicateProperties(prop.Value))
                return true;
        }
        return false;
    }

    private static async Task<byte[]?> ReadBoundedAsync(
        HttpRequest request,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var bounded = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var available = maximumBytes + 1 - (int)bounded.Length;
            var count = await request.Body.ReadAsync(
                buffer.AsMemory(0, Math.Min(buffer.Length, available)),
                cancellationToken).ConfigureAwait(false);
            if (count == 0)
                return bounded.ToArray();
            if (bounded.Length + count > maximumBytes)
                return null;
            await bounded.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }
    }
}
