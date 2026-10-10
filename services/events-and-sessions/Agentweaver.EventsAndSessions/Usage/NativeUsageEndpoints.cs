using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Abstractions;
using System.Collections.Immutable;

namespace Agentweaver.EventsAndSessions;

internal sealed class NativeUsageReceiptClient(HttpClient client, CoordinationOwnerClientOptions options)
{
    private Uri OwnerAddress => Uri.TryCreate(options.OwnerBaseAddress, UriKind.Absolute, out var address)
        ? RuntimeOwnerHttpTransport.RequireOwnerAddress(address)
        : throw new RuntimeAuthorizationException("runtime_usage_owner_configuration_invalid");

    internal Task<RuntimeUsageSourceReceipt> ReadAsync(
        RuntimeActorAuthorization actor, string projectId, string runId, string sessionId,
        Guid receiptId, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(options.OwnerBaseAddress, UriKind.Absolute, out var address))
            throw new RuntimeAuthorizationException("runtime_usage_owner_configuration_invalid");
        return RuntimeOwnerHttpTransport.SendAsync<RuntimeUsageSourceReceipt>(
            client, address,
            $"/internal/projects/{Uri.EscapeDataString(projectId)}/runs/{Uri.EscapeDataString(runId)}" +
            $"/coordination/sessions/{Uri.EscapeDataString(sessionId)}/usage-receipts/{receiptId:D}",
            actor, null, cancellationToken);
    }

    internal Task<UsageDispatchSourceCompletionManifest?> ReadDispatchCompletionAsync(
        RuntimeActorAuthorization actor, string projectId, string runId, string sessionId,
        Guid dispatchId, CancellationToken cancellationToken)
    {
        var address = OwnerAddress;
        return RuntimeOwnerHttpTransport.ReadOptionalAsync<UsageDispatchSourceCompletionManifest>(
            client, address,
            $"/internal/projects/{Uri.EscapeDataString(projectId)}/runs/{Uri.EscapeDataString(runId)}" +
            $"/coordination/sessions/{Uri.EscapeDataString(sessionId)}/usage-dispatch-completions/{dispatchId:D}",
            actor, cancellationToken);
    }

    internal async Task<NativeRuntimeUsageSourceSnapshot> ReadCurrentSourceAsync(
        Guid runtimeInstanceId,
        RuntimeActorAuthorization actor,
        CancellationToken cancellationToken)
    {
        var address = OwnerAddress;
        var registration = await new RuntimeRegistrationHttpClient(client, address)
            .ReadCurrentAsync(runtimeInstanceId, actor, cancellationToken).ConfigureAwait(false);
        var receipt = await RuntimeOwnerHttpTransport.SendAsync<RuntimeSdkSourceReceipt>(
            client, address, $"/internal/runtime/sources/{runtimeInstanceId:D}",
            actor, null, cancellationToken).ConfigureAwait(false);
        RuntimeUsageSourceReceiptContract.ValidateSource(registration, receipt.Source);
        if (receipt.RuntimeInstanceId != runtimeInstanceId ||
            receipt.RegistrationRevision != registration.Revision ||
            receipt.SourceGrantId == Guid.Empty ||
            receipt.CanonicalPayloadHash != RuntimeUsageSourceReceiptContract.HashSource(
                registration, receipt.Source))
            throw new RuntimeAuthorizationException("runtime_cost_preflight_source_invalid");
        return new(registration, receipt);
    }
}

internal sealed record NativeRuntimeUsageSourceSnapshot(
    RuntimeRegistration Registration,
    RuntimeSdkSourceReceipt Receipt);

internal sealed class NativeUsageApplicationService(
    NativeUsageReceiptClient source, IProjectsAuthorizationContextClient projects,
    NativeUsageReceiptConsumer consumer, PostgresUsageLedger ledger, TimeProvider timeProvider)
{
    internal async Task<RuntimeUsageAccountingAcknowledgment> AppendAsync(
        HttpContext context, string sessionId, RuntimeUsageReceiptReferenceRequest reference,
        CancellationToken cancellationToken)
    {
        RuntimeContractValidation.ValidateIdentifier(sessionId);
        if (reference.ReceiptId == Guid.Empty)
            throw new ArgumentException("A native source receipt identity is required.");
        var current = await projects.GetCurrentAsync(context, cancellationToken).ConfigureAwait(false);
        RequireReadAuthority(current);
        var bearer = await BearerAsync(context, timeProvider).ConfigureAwait(false);
        try
        {
            var actor = new RuntimeActorAuthorization(bearer, current.TenantId);
            var receipt = await source.ReadAsync(
                actor, current.BoundProjectId!, current.BoundRunId!, sessionId, reference.ReceiptId,
                cancellationToken).ConfigureAwait(false);
            RuntimeUsageSourceReceiptContract.Validate(receipt);
            var binding = receipt.Registration.Binding;
            if (receipt.ReceiptId != reference.ReceiptId || binding.TenantId != current.TenantId ||
                binding.ProjectId != current.BoundProjectId || binding.RunId != current.BoundRunId ||
                binding.SessionId != sessionId)
                throw new RuntimeAuthorizationException("runtime_usage_receipt_scope_invalid");
            return await consumer.AppendAsync(receipt, async token =>
            {
                var final = await projects.GetCurrentAsync(context, token).ConfigureAwait(false);
                RequireReadAuthority(final);
                if (final.TenantId != binding.TenantId || final.BoundProjectId != binding.ProjectId ||
                    final.BoundRunId != binding.RunId || !bearer.IsUsable())
                    throw new RuntimeAuthorizationException("runtime_usage_authority_changed");
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            bearer.Invalidate();
        }
    }

    internal async Task<RuntimeUsageCostPreflightReceipt> PreflightAsync(
        HttpContext context,
        Guid runtimeInstanceId,
        RuntimeUsageCostPreflightRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null || request.ContractVersion != 1 || runtimeInstanceId == Guid.Empty)
            throw new ArgumentException("A current runtime source is required.", nameof(runtimeInstanceId));
        var first = await projects.GetCurrentAsync(context, cancellationToken).ConfigureAwait(false);
        RequireReadAuthority(first);
        var bearer = await BearerAsync(context, timeProvider).ConfigureAwait(false);
        try
        {
            var actor = new RuntimeActorAuthorization(bearer, first.TenantId);
            var sourceSnapshot = await source.ReadCurrentSourceAsync(
                runtimeInstanceId, actor, cancellationToken).ConfigureAwait(false);
            var binding = sourceSnapshot.Registration.Binding;
            RequireSourceScope(first, runtimeInstanceId, sourceSnapshot);

            var preflight = await consumer.PreflightAsync(
                binding.TenantId, binding.ProjectId, binding.RunId,
                sourceSnapshot.Receipt.Source.MeterSource, sourceSnapshot.Receipt.Source.ModelId,
                async token =>
                {
                    var current = await projects.GetCurrentAsync(context, token).ConfigureAwait(false);
                    RequireReadAuthority(current);
                    if (current.ActorId != first.ActorId || current.TenantId != binding.TenantId ||
                        current.BoundProjectId != binding.ProjectId || current.BoundRunId != binding.RunId ||
                        !bearer.IsUsable())
                        throw new RuntimeAuthorizationException("runtime_usage_authority_changed");
                    var currentSource = await source.ReadCurrentSourceAsync(
                        runtimeInstanceId, actor, token).ConfigureAwait(false);
                    if (currentSource != sourceSnapshot)
                        throw new RuntimeAuthorizationException("runtime_cost_preflight_source_changed");
                }, cancellationToken).ConfigureAwait(false);

            var receipt = new RuntimeUsageCostPreflightReceipt(
                1,
                runtimeInstanceId,
                sourceSnapshot.Registration.Revision,
                binding.ExecutionFence,
                sourceSnapshot.Receipt.Source.ModelSelectionReference,
                sourceSnapshot.Receipt.Source.ModelId,
                sourceSnapshot.Receipt.Source.MeterSource,
                binding.AcceptedSelectionHash,
                sourceSnapshot.Receipt.CanonicalPayloadHash,
                preflight.Binding,
                preflight.IsPriced,
                preflight.UnpricedReason);
            RuntimeUsageCostPreflightContract.Validate(receipt);
            return receipt;
        }
        finally
        {
            bearer.Invalidate();
        }
    }

    internal async Task<RuntimeUsageCostReconciliationReceipt> ReconcileCostTurnAsync(
        HttpContext context,
        string projectId,
        string runId,
        RuntimeUsageCostReconciliationRequest request,
        CancellationToken cancellationToken)
    {
        RuntimeContractValidation.ValidateIdentifier(projectId);
        RuntimeContractValidation.ValidateIdentifier(runId);
        RuntimeUsageCostReconciliationContract.ValidateRequest(request);
        var first = await projects.GetCurrentAsync(context, cancellationToken).ConfigureAwait(false);
        RequireReadAuthority(first);
        if (first.BoundProjectId != projectId || first.BoundRunId != runId)
            throw new RuntimeAuthorizationException("runtime_usage_receipt_scope_invalid");

        return await consumer.ReconcileAsync(first.TenantId, projectId, runId, request, async token =>
        {
            var current = await projects.GetCurrentAsync(context, token).ConfigureAwait(false);
            RequireReadAuthority(current);
            if (current.ActorId != first.ActorId || current.TenantId != first.TenantId ||
                current.BoundProjectId != projectId || current.BoundRunId != runId)
                throw new RuntimeAuthorizationException("runtime_usage_authority_changed");
        }, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<UsageDispatchAccountingWitness> GetDispatchAccountingWitnessAsync(
        HttpContext context,
        string projectId,
        string runId,
        string sessionId,
        Guid dispatchId,
        CancellationToken cancellationToken)
    {
        RuntimeContractValidation.ValidateIdentifier(projectId);
        RuntimeContractValidation.ValidateIdentifier(runId);
        RuntimeContractValidation.ValidateIdentifier(sessionId);
        if (dispatchId == Guid.Empty)
            throw new ArgumentException("A dispatch identity is required.", nameof(dispatchId));

        var first = await projects.GetCurrentAsync(context, cancellationToken).ConfigureAwait(false);
        RequireReadAuthority(first);
        if (first.BoundProjectId != projectId || first.BoundRunId != runId)
            throw new RuntimeAuthorizationException("runtime_usage_receipt_scope_invalid");

        var bearer = await BearerAsync(context, timeProvider).ConfigureAwait(false);
        try
        {
            var actor = new RuntimeActorAuthorization(bearer, first.TenantId);
            var manifest = await source.ReadDispatchCompletionAsync(
                actor, projectId, runId, sessionId, dispatchId, cancellationToken).ConfigureAwait(false)
                ?? throw new NativeUsageSourceCompletionUnavailableException();
            UsageDispatchSourceCompletionManifestContract.Validate(manifest);
            if (manifest.DispatchId != dispatchId.ToString("D") ||
                manifest.TenantId != first.TenantId || manifest.ProjectId != projectId ||
                manifest.RunId != runId || manifest.SessionId != sessionId)
                throw new RuntimeAuthorizationException("runtime_usage_source_completion_scope_invalid");
            if (manifest.SourceReceipts.Length > RuntimeUsageCostReconciliationContract.MaximumReceiptReferences)
                throw new NativeUsageSourceCompletionUnavailableException();

            var sourceReceipts = ImmutableArray.CreateBuilder<RuntimeUsageSourceReceipt>(
                manifest.SourceReceipts.Length);
            foreach (var identity in manifest.SourceReceipts)
            {
                var receipt = await source.ReadAsync(
                    actor, projectId, runId, sessionId, identity.SourceReceiptId, cancellationToken)
                    .ConfigureAwait(false);
                sourceReceipts.Add(receipt);
            }

            var canonicalManifest = UsageDispatchSourceCompletionManifestContract.SerializeCanonical(manifest);
            return await consumer.RecordDispatchSourceCompletionAsync(
                manifest, sourceReceipts.ToImmutable(), async token =>
                {
                    var current = await projects.GetCurrentAsync(context, token).ConfigureAwait(false);
                    RequireReadAuthority(current);
                    if (current.ActorId != first.ActorId || current.TenantId != first.TenantId ||
                        current.BoundProjectId != projectId || current.BoundRunId != runId ||
                        !bearer.IsUsable())
                        throw new RuntimeAuthorizationException("runtime_usage_authority_changed");
                    var currentManifest = await source.ReadDispatchCompletionAsync(
                        actor, projectId, runId, sessionId, dispatchId, token).ConfigureAwait(false);
                    if (currentManifest is null ||
                        UsageDispatchSourceCompletionManifestContract.SerializeCanonical(currentManifest) !=
                        canonicalManifest)
                        throw new RuntimeAuthorizationException("runtime_usage_source_completion_changed");
                }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            bearer.Invalidate();
        }
    }

    internal async Task<UsageRunTotals> GetTotalsAsync(
        HttpContext context, string projectId, string runId, CancellationToken cancellationToken)
    {
        var first = await projects.GetCurrentAsync(context, cancellationToken).ConfigureAwait(false);
        RequireReadAuthority(first);
        if (first.BoundProjectId != projectId || first.BoundRunId != runId)
            throw new RuntimeAuthorizationException("runtime_usage_receipt_scope_invalid");
        var result = await ledger.GetRunTotalsAsync(first.TenantId, projectId, runId, cancellationToken)
            .ConfigureAwait(false);
        var current = await projects.GetCurrentAsync(context, cancellationToken).ConfigureAwait(false);
        RequireReadAuthority(current);
        if (current.TenantId != first.TenantId || current.BoundProjectId != projectId || current.BoundRunId != runId)
            throw new RuntimeAuthorizationException("runtime_usage_authority_changed");
        return result;
    }

    private static void RequireSourceScope(
        ProjectsAuthorizationContextResponse authority,
        Guid runtimeInstanceId,
        NativeRuntimeUsageSourceSnapshot source)
    {
        var binding = source.Registration.Binding;
        var receipt = source.Receipt;
        if (source.Registration.RuntimeInstanceId != runtimeInstanceId ||
            binding.TenantId != authority.TenantId ||
            binding.ProjectId != authority.BoundProjectId ||
            binding.RunId != authority.BoundRunId ||
            receipt.Source.RuntimeInstanceId != runtimeInstanceId ||
            receipt.Source.RegistrationRevision != source.Registration.Revision ||
            receipt.Source.ModelSelectionReference != binding.ModelSelectionReference ||
            receipt.Source.AcceptedSelectionHash != binding.AcceptedSelectionHash ||
            receipt.Source.MeterSource is not (SdkMeterSources.CopilotNanoAiu or SdkMeterSources.ByokTokens))
            throw new RuntimeAuthorizationException("runtime_cost_preflight_source_scope_invalid");
    }

    internal static async Task<SecretCredential> BearerAsync(HttpContext context, TimeProvider timeProvider)
    {
        var authentication = await context.AuthenticateAsync().ConfigureAwait(false);
        var expiry = context.User.GetExpirationDate() ?? authentication.Properties?.ExpiresUtc;
        var headers = ForwardedHeaders.Read(context);
        if (!authentication.Succeeded || expiry is null || expiry <= timeProvider.GetUtcNow() ||
            headers.Error is not null || headers.Authorization?.Parameter is not { } value)
            throw new RuntimeAuthorizationException("runtime_usage_actor_expired");
        return new(value, expiry.Value, timeProvider);
    }

    internal static void RequireReadAuthority(ProjectsAuthorizationContextResponse authority)
    {
        if (authority.BoundProjectId is null || authority.BoundRunId is null ||
            !authority.EffectiveAuthority.Any(resource =>
                resource.ResourceType == "project" && resource.ResourceId == authority.BoundProjectId &&
                !resource.Permissions.IsDefault && resource.Permissions.Any(permission =>
                    permission.Permission == "readRunSelection" && permission.RoleRevision > 0)))
            throw new RuntimeAuthorizationException("runtime_usage_authority_denied");
    }
}

public static class NativeUsageEndpoints
{
    public static bool AddNativeUsageConsumer(this IServiceCollection services, IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>("EventsAndSessions:RuntimeUsage:Enabled"))
            return false;
        services.AddSingleton<PostgresUsageLedger>(services => new(
            services.GetRequiredService<Npgsql.NpgsqlDataSource>(),
            services.GetRequiredService<PostgresSessionsProviderOptions>().Schema));
        services.AddSingleton<NativeUsageReceiptConsumer>();
        services.AddScoped<NativeUsageApplicationService>();
        services.AddHttpClient<NativeUsageReceiptClient>(client => client.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(RuntimeOwnerHttpTransport.CreateHandler);
        return true;
    }

    public static IEndpointRouteBuilder MapNativeUsageEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/internal/sessions/{sessionId}/usage-receipts",
            (string sessionId, RuntimeUsageReceiptReferenceRequest request, HttpContext context,
                [FromServices] NativeUsageApplicationService service, CancellationToken cancellationToken) =>
                ExecuteAsync(context, () => service.AppendAsync(context, sessionId, request, cancellationToken)))
            .RequireAuthorization();
        endpoints.MapPost("/internal/runtime/sources/{runtimeInstanceId:guid}/cost-preflight",
            (Guid runtimeInstanceId, RuntimeUsageCostPreflightRequest request, HttpContext context,
                [FromServices] NativeUsageApplicationService service, CancellationToken cancellationToken) =>
                ExecuteAsync(context, () => service.PreflightAsync(
                    context, runtimeInstanceId, request, cancellationToken)))
            .RequireAuthorization();
        endpoints.MapGet("/internal/projects/{projectId}/runs/{runId}/usage",
            (string projectId, string runId, HttpContext context,
                [FromServices] NativeUsageApplicationService service, CancellationToken cancellationToken) =>
                ExecuteAsync(context, () => service.GetTotalsAsync(context, projectId, runId, cancellationToken)))
            .RequireAuthorization();
        endpoints.MapPost("/internal/projects/{projectId}/runs/{runId}/usage-cost-reconciliation",
            (string projectId, string runId, RuntimeUsageCostReconciliationRequest request, HttpContext context,
                [FromServices] NativeUsageApplicationService service, CancellationToken cancellationToken) =>
                ExecuteAsync(context, () => service.ReconcileCostTurnAsync(
                    context, projectId, runId, request, cancellationToken)))
            .RequireAuthorization();
        endpoints.MapGet(
            "/internal/projects/{projectId}/runs/{runId}/sessions/{sessionId}/dispatches/{dispatchId:guid}/usage-accounting",
            (string projectId, string runId, string sessionId, Guid dispatchId, HttpContext context,
                [FromServices] NativeUsageApplicationService service, CancellationToken cancellationToken) =>
                ExecuteAsync(context, () => service.GetDispatchAccountingWitnessAsync(
                    context, projectId, runId, sessionId, dispatchId, cancellationToken)))
            .RequireAuthorization();
        return endpoints;
    }

    private static async Task<IResult> ExecuteAsync<T>(HttpContext context, Func<Task<T>> action)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            return Results.Ok(await action().ConfigureAwait(false));
        }
        catch (RuntimeAuthorizationException exception) when (
            exception.Code.StartsWith("runtime_cost_preflight_", StringComparison.Ordinal))
        {
            return Results.Json(new { error = exception.Code }, statusCode:
                exception.Code.EndsWith("_changed", StringComparison.Ordinal)
                    ? StatusCodes.Status409Conflict : StatusCodes.Status422UnprocessableEntity);
        }
        catch (RuntimeAuthorizationException exception)
        {
            return Results.Json(new { error = exception.Code }, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (ProjectsAuthorizationContextException exception)
        {
            return Results.Json(new { error = exception.Code }, statusCode: (int)exception.StatusCode);
        }
        catch (UsageLedgerConflictException)
        {
            return Results.Conflict(new { error = "runtime_usage_receipt_conflict" });
        }
        catch (NativeUsageReconciliationException exception)
        {
            return Results.Json(new
            {
                error = exception.Code,
                sourceReceiptIds = exception.SourceReceiptIds.IsDefault
                    ? Array.Empty<Guid>()
                    : exception.SourceReceiptIds.ToArray()
            }, statusCode: StatusCodes.Status409Conflict);
        }
        catch (NativeUsageSourceCompletionUnavailableException)
        {
            return Results.Json(new { error = "runtime_usage_source_completion_unavailable" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new { error = "runtime_usage_request_invalid" });
        }
        catch (HttpRequestException)
        {
            return Results.Json(new { error = "runtime_usage_owner_unavailable" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            return Results.Json(new { error = "runtime_usage_owner_timeout" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
