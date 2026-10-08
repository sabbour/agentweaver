using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Abstractions;

namespace Agentweaver.EventsAndSessions;

internal sealed class NativeUsageReceiptClient(HttpClient client, CoordinationOwnerClientOptions options)
{
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
}

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
        endpoints.MapGet("/internal/projects/{projectId}/runs/{runId}/usage",
            (string projectId, string runId, HttpContext context,
                [FromServices] NativeUsageApplicationService service, CancellationToken cancellationToken) =>
                ExecuteAsync(context, () => service.GetTotalsAsync(context, projectId, runId, cancellationToken)))
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
