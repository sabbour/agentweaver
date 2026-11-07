using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace Agentweaver.Orchestrator;

internal sealed record RuntimeRunStartOptions(Uri EnvironmentOwnerAddress, Uri BrokerOwnerAddress);

internal static class RuntimeRunStartComposition
{
    public static bool AddRuntimeRunStart(
        this IServiceCollection services,
        IConfiguration configuration,
        OrchestratorOptions orchestrator,
        bool runtimeRegistrationEnabled)
    {
        var environmentAddress = configuration["Orchestrator:RuntimeRegistration:EnvironmentOwnerAddress"];
        var brokerAddress = configuration["Orchestrator:RuntimeUsage:BrokerOwnerAddress"];
        if (!runtimeRegistrationEnabled || environmentAddress is null || brokerAddress is null)
            return false;
        if (!Uri.TryCreate(environmentAddress, UriKind.Absolute, out var environmentUri) ||
            !Uri.TryCreate(brokerAddress, UriKind.Absolute, out var brokerUri))
            throw new InvalidOperationException("Runtime start owner addresses must be absolute HTTPS URIs.");

        services.AddSingleton(new RuntimeRunStartOptions(
            RuntimeOwnerHttpTransport.RequireOwnerAddress(environmentUri),
            RuntimeOwnerHttpTransport.RequireOwnerAddress(brokerUri)));
        services.AddSingleton(provider => new CoordinationRuntimeRunStartStore(
            provider.GetRequiredService<Npgsql.NpgsqlDataSource>(), orchestrator.Schema));
        services.AddSingleton<RuntimeRunStartWorkQueue>();
        services.AddScoped<RuntimeRunStartDispatcher>();
        services.AddScoped<RuntimeRunStartContinuation>();
        services.AddScoped<RuntimeRunStartCleanupContinuation>();
        services.AddHttpClient("runtime-run-start-environment", client =>
            client.Timeout = TimeSpan.FromMinutes(10))
            .ConfigurePrimaryHttpMessageHandler(RuntimeOwnerHttpTransport.CreateTrustedOwnerHandler);
        services.AddHttpClient("runtime-run-start-broker", client => client.Timeout = TimeSpan.FromMinutes(2))
            .ConfigurePrimaryHttpMessageHandler(RuntimeOwnerHttpTransport.CreateTrustedOwnerHandler);
        services.AddHostedService<RuntimeRunStartBackgroundService>();
        return true;
    }
}

internal abstract record RuntimeRunStartWork(RuntimeActorAuthorization Actor);

internal sealed record RuntimeRunStartContinueWork(
    RuntimeOwnerContext Owner,
    RuntimeActorAuthorization Actor) : RuntimeRunStartWork(Actor);

internal sealed record RuntimeRunStartCleanupWork(
    string ProjectId,
    string RunId,
    string TenantId,
    CoordinationActor Caller,
    RuntimeActorAuthorization Actor) : RuntimeRunStartWork(Actor);

internal sealed class RuntimeRunStartWorkQueue
{
    internal const int MaximumConcurrentWork = 4;

    private readonly Channel<RuntimeRunStartWork> _channel = Channel.CreateBounded<RuntimeRunStartWork>(
        new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });

    public bool TryEnqueue(RuntimeRunStartWork work) => _channel.Writer.TryWrite(work);
    public int PendingCount => _channel.Reader.CanCount ? _channel.Reader.Count : -1;

    public IAsyncEnumerable<RuntimeRunStartWork> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}

internal sealed class RuntimeRunStartBackgroundService(
    RuntimeRunStartWorkQueue queue,
    IServiceScopeFactory scopes,
    ILogger<RuntimeRunStartBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ProcessAsync(
            queue.ReadAllAsync(stoppingToken),
            async (work, token) =>
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    switch (work)
                    {
                        case RuntimeRunStartContinueWork start:
                            await scope.ServiceProvider.GetRequiredService<RuntimeRunStartContinuation>()
                                .ContinueAsync(start.Owner, start.Actor, token).ConfigureAwait(false);
                            break;
                        case RuntimeRunStartCleanupWork cleanup:
                            await scope.ServiceProvider.GetRequiredService<RuntimeRunStartCleanupContinuation>()
                                .ContinueAsync(cleanup, token).ConfigureAwait(false);
                            break;
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning("Runtime first-start continuation failed with {FailureCode}.",
                        RuntimeRunStartContinuation.FailureCode(exception));
                }
                finally
                {
                    work.Actor.Bearer.Invalidate();
                }
            },
            stoppingToken).ConfigureAwait(false);
    }

    internal static Task ProcessAsync(
        IAsyncEnumerable<RuntimeRunStartWork> work,
        Func<RuntimeRunStartWork, CancellationToken, ValueTask> process,
        CancellationToken cancellationToken) =>
        Parallel.ForEachAsync(
            work,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = RuntimeRunStartWorkQueue.MaximumConcurrentWork,
                CancellationToken = cancellationToken
            },
            process);
}

internal sealed class RuntimeRunStartDispatcher(
    CoordinationRuntimeRunStartStore store,
    RuntimeRunStartWorkQueue queue,
    RuntimeRunStartContinuation continuation,
    TimeProvider timeProvider,
    OrchestratorOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<RuntimeStartupSummary?> EnqueueAsync(
        HttpContext context,
        RuntimeOwnerContext owner,
        CancellationToken cancellationToken)
    {
        var actor = await CreateActorAsync(context, timeProvider).ConfigureAwait(false);
        try
        {
            var intent = await store.EnsureIntentAsync(owner, cancellationToken).ConfigureAwait(false);
            if (intent.State == "ready")
            {
                var summary = await continuation.RevalidateReadyAsync(intent, actor, cancellationToken)
                    .ConfigureAwait(false);
                actor.Bearer.Invalidate();
                return summary;
            }
            if (intent.CleanupObligation)
            {
                actor.Bearer.Invalidate();
                return intent.Summary;
            }
            cancellationToken.ThrowIfCancellationRequested();
            EnqueueOrThrow(queue, new RuntimeRunStartContinueWork(owner, actor));
            return intent.Summary;
        }
        catch
        {
            actor.Bearer.Invalidate();
            throw;
        }
    }

    public async Task<RuntimeStartupSummary?> ReadSummaryAsync(
        HttpContext context,
        string projectId,
        string runId,
        string rootSessionId,
        CancellationToken cancellationToken)
    {
        var intent = await store.ReadIntentForStatusAsync(
            projectId, runId, rootSessionId, cancellationToken).ConfigureAwait(false);
        if (intent is null || intent.State != "ready")
            return intent?.Summary;
        var actor = await CreateActorAsync(context, timeProvider).ConfigureAwait(false);
        try
        {
            return await continuation.RevalidateReadyAsync(intent, actor, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            actor.Bearer.Invalidate();
        }
    }

    public async Task EnqueueCleanupAsync(
        HttpContext context,
        string tenantId,
        string projectId,
        string runId,
        CancellationToken cancellationToken)
    {
        CoordinationActor caller;
        RuntimeActorAuthorization actor;
        try
        {
            caller = CoordinationIdentity.RequireActor(context.User, options.Issuer);
            actor = await CreateActorAsync(context, timeProvider).ConfigureAwait(false);
        }
        catch (CoordinationException)
        {
            return;
        }
        EnqueueOrThrow(queue, new RuntimeRunStartCleanupWork(
            projectId, runId, tenantId, caller, actor));
    }

    internal static void EnqueueOrThrow(RuntimeRunStartWorkQueue queue, RuntimeRunStartWork work)
    {
        if (queue.TryEnqueue(work))
            return;
        work.Actor.Bearer.Invalidate();
        throw new CoordinationException(
            "runtime_start_queue_full", StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<RuntimeActorAuthorization> CreateActorAsync(
        HttpContext context, TimeProvider timeProvider)
    {
        var authentication = await context.AuthenticateAsync().ConfigureAwait(false);
        var authorization = context.Request.Headers.Authorization;
        var tenant = context.Request.Headers["X-Agentweaver-Tenant"];
        if (!authentication.Succeeded ||
            authentication.Properties?.ExpiresUtc is not { } expiresAt ||
            authorization.Count != 1 ||
            !System.Net.Http.Headers.AuthenticationHeaderValue.TryParse(authorization[0], out var header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(header.Parameter) ||
            tenant.Count > 1)
            throw new CoordinationException("runtime_start_actor_unavailable", StatusCodes.Status401Unauthorized);
        return new RuntimeActorAuthorization(
            new SecretCredential(header.Parameter, expiresAt, timeProvider),
            tenant.SingleOrDefault());
    }
}

internal sealed class RuntimeRunStartContinuation(
    CoordinationRuntimeRunStartStore store,
    RuntimeRegistrationOwner registrations,
    RuntimeEnvironmentContextClient environment,
    RuntimeRunStartOptions options,
    IHttpClientFactory clients,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task ContinueAsync(
        RuntimeOwnerContext owner,
        RuntimeActorAuthorization actor,
        CancellationToken cancellationToken)
    {
        if (!actor.Bearer.IsUsable())
            return;
        var intent = await store.EnsureIntentAsync(owner, cancellationToken).ConfigureAwait(false);
        await using var lease = await store.AcquireAsync(intent, cancellationToken).ConfigureAwait(false);
        intent = await store.ReadAsync(intent, cancellationToken).ConfigureAwait(false) ?? intent;
        if (intent.CleanupObligation)
            return;

        var callerToken = cancellationToken;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(180));
        cancellationToken = deadline.Token;
        try
        {
            RequireUsable(actor);
            if (intent.CurrentStep == "retirement")
            {
                var replacement = await RetireFailedBootstrapAsync(
                    intent, owner, actor, cancellationToken).ConfigureAwait(false);
                if (replacement is null)
                    return;
                intent = replacement;
            }
            if (intent.State == "ready")
            {
                _ = await RevalidateReadyAsync(intent, actor, cancellationToken).ConfigureAwait(false);
                return;
            }
            intent = await SaveAsync(
                intent, owner, "placement", null, RuntimeRunStartKeys.EnvironmentId(owner), null, cancellationToken)
                .ConfigureAwait(false);
            var placement = await RuntimeOwnerHttpTransport.SendRuntimeRunStartAsync<RuntimeRunStartPlacementReceipt>(
                clients.CreateClient("runtime-run-start-environment"),
                options.EnvironmentOwnerAddress,
                "/internal/runtime/run-start/placement",
                actor,
                new RuntimeRunStartPlacementRequest(1, owner, null, intent.BootstrapOperationId),
                cancellationToken).ConfigureAwait(false);
            placement.Validate();
            if (placement.EnvironmentId != RuntimeRunStartKeys.EnvironmentId(owner) ||
                placement.WorkspaceVolumeId != RuntimeRunStartKeys.WorkspaceVolumeId(owner) ||
                placement.SandboxIdempotencyKey !=
                    RuntimeRunStartKeys.SandboxIdempotencyKey(owner, intent.BootstrapOperationId) ||
                placement.NetworkPolicyGeneration < 1)
                throw new RuntimeAuthorizationException("runtime_start_placement_receipt_invalid");
            intent = await SaveAsync(
                intent, owner, "registration", null, placement.EnvironmentId, null,
                cancellationToken, ("placement", placement)).ConfigureAwait(false);

            RequireUsable(actor);
            var registration = await registrations.RegisterAsync(
                actor, owner, placement.EnvironmentId, placement.SourceProfileId, cancellationToken)
                .ConfigureAwait(false);
            RequireRegistration(owner, placement, registration);
            intent = await SaveAsync(
                intent, owner, "bootstrap", null, placement.EnvironmentId, registration.RuntimeInstanceId,
                cancellationToken, ("registration", RegistrationReceipt(registration))).ConfigureAwait(false);

            var configuration = JsonDocument.Parse("{}");
            using (configuration)
            {
                var configurationHash = RuntimeContractValidation.Hash(
                    Encoding.UTF8.GetBytes(configuration.RootElement.GetRawText()));
                RequireUsable(actor);
                var bootstrap = await RuntimeOwnerHttpTransport.SendRuntimeRunStartAsync<RuntimeBootstrapDeliveryReceipt>(
                    clients.CreateClient("runtime-run-start-broker"),
                    options.BrokerOwnerAddress,
                    "/internal/runtime/bootstrap/request",
                    actor,
                    new RuntimeBootstrapRequest(
                        registration.RuntimeInstanceId, intent.BootstrapOperationId, configurationHash),
                    cancellationToken).ConfigureAwait(false);
                RequireBootstrapReceipt(intent, registration, bootstrap, configurationHash);
                intent = await SaveAsync(
                    intent, owner, "activation", null, placement.EnvironmentId, registration.RuntimeInstanceId,
                    cancellationToken, ("bootstrap", BootstrapReceipt(bootstrap))).ConfigureAwait(false);

                var context = await environment.ReadAsync(
                    actor, owner.ProjectId, owner.RunId, owner.SessionId,
                    placement.EnvironmentId, registration.Binding.ProfileId, cancellationToken)
                    .ConfigureAwait(false);
                RequirePlacement(placement, registration, context);
                var activationRequest = new RuntimeHostConfigureRequest(
                    1,
                    registration,
                    intent.ConsumeOperationId,
                    intent.ExchangeOperationId,
                    configuration.RootElement.Clone())
                {
                    McpServer = context.McpServer
                };
                RequireUsable(actor);
                var activated = await RuntimeOwnerHttpTransport.SendRuntimeRunStartAsync<RuntimeHostReadinessReceipt>(
                    clients.CreateClient("runtime-run-start-environment"),
                    options.EnvironmentOwnerAddress,
                    "/internal/runtime/v1/activate",
                    actor,
                    activationRequest,
                    cancellationToken).ConfigureAwait(false);
                CoordinationEndpoints.ValidateHostReadiness(activated, registration, timeProvider);
                intent = await SaveAsync(
                    intent, owner, "readiness", null, placement.EnvironmentId, registration.RuntimeInstanceId,
                    cancellationToken, ("activation", activated)).ConfigureAwait(false);

                var readiness = await environment.ReadReadinessAsync(
                    actor, owner.ProjectId, owner.RunId, owner.SessionId,
                    placement.EnvironmentId, registration.Binding.ProfileId, cancellationToken)
                    .ConfigureAwait(false);
                RequireCurrentReadiness(placement, registration, activated, readiness);
                var receipt = new
                {
                    placement = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(placement, JsonOptions)),
                    registration = RegistrationReceipt(registration),
                    bootstrap = BootstrapReceipt(bootstrap),
                    activation = activated,
                    readiness
                };
                _ = await store.CommitReadyAsync(
                    intent, owner, intent.RowVersion, receipt, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            var current = await store.ReadAsync(intent, CancellationToken.None).ConfigureAwait(false) ?? intent;
            if (!current.CleanupObligation && current.State != "ready")
                _ = await store.UpdateAsync(
                    current, current.RowVersion, "pending", current.CurrentStep,
                    "runtime_startup_time_budget_exceeded", current.EnvironmentId,
                    current.RuntimeInstanceId, ReadReceiptLinks(current), CancellationToken.None)
                    .ConfigureAwait(false);
        }
        catch (RuntimeAuthorizationException exception) when (exception.Code == "runtime_delivery_failed")
        {
            var current = await store.ReadAsync(intent, CancellationToken.None).ConfigureAwait(false) ?? intent;
            if (!current.CleanupObligation && current.State != "ready")
            {
                current = await store.UpdateAsync(
                    current,
                    current.RowVersion,
                    "pending",
                    "retirement",
                    exception.Code,
                    current.EnvironmentId,
                    current.RuntimeInstanceId,
                    ReadReceiptLinks(current),
                    CancellationToken.None).ConfigureAwait(false);
                _ = await RetireFailedBootstrapAsync(current, owner, actor, callerToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var failureCode = FailureCode(exception);
            var blocked = exception is RuntimeAuthorizationException authorization &&
                authorization.Code is "runtime_start_actor_expired" or "runtime_start_owner_context_stale" or
                    "runtime_start_workspace_capacity_unavailable" or "runtime_start_workspace_spec_invalid" or
                    "runtime_delivery_unavailable";
            try
            {
                var current = await store.ReadAsync(intent, CancellationToken.None).ConfigureAwait(false) ?? intent;
                if (!current.CleanupObligation && current.State != "ready")
                    _ = await store.UpdateAsync(
                        current, current.RowVersion,
                        blocked ? "blocked" : "pending",
                        current.CurrentStep,
                        failureCode,
                        current.EnvironmentId,
                        current.RuntimeInstanceId,
                        ReadReceiptLinks(current),
                        CancellationToken.None).ConfigureAwait(false);
            }
            catch (CoordinationException)
            {
            }
            throw;
        }
    }

    private async Task<RuntimeRunStartIntent?> RetireFailedBootstrapAsync(
        RuntimeRunStartIntent intent,
        RuntimeOwnerContext owner,
        RuntimeActorAuthorization actor,
        CancellationToken cancellationToken)
    {
        var links = ReadReceiptLinks(intent);
        if (!links.TryGetValue("placement", out var placementElement) ||
            !links.TryGetValue("registration", out var registrationElement))
            throw new RuntimeAuthorizationException("runtime_start_retirement_binding_missing");
        var placement = placementElement.Deserialize<RuntimeRunStartPlacementReceipt>(JsonOptions)
            ?? throw new RuntimeAuthorizationException("runtime_start_retirement_binding_invalid");
        var registration = registrationElement.Deserialize<RuntimeRegistration>(JsonOptions)
            ?? throw new RuntimeAuthorizationException("runtime_start_retirement_binding_invalid");
        placement.Validate();
        RequireRegistration(owner, placement, registration);
        var request = new RuntimeRunStartRetirementRequest(
            1,
            owner,
            intent.BootstrapOperationId,
            registration.RuntimeInstanceId,
            registration.Revision,
            placement).Validate();
        var receipt = await RuntimeOwnerHttpTransport.SendRuntimeRunStartAsync<
                RuntimeRunStartRetirementReceipt>(
                clients.CreateClient("runtime-run-start-environment"),
                options.EnvironmentOwnerAddress,
                "/internal/runtime/run-start/retire-failed",
                actor,
                request,
                cancellationToken).ConfigureAwait(false);
        receipt.ValidateFor(request);
        if (receipt.State != "released")
            return null;
        return await store.ReplaceFailedBootstrapAttemptAsync(
            intent, intent.RowVersion, receipt, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RuntimeStartupSummary> RevalidateReadyAsync(
        RuntimeRunStartIntent intent,
        RuntimeActorAuthorization actor,
        CancellationToken cancellationToken)
    {
        if (intent.State != "ready")
            return intent.Summary;
        try
        {
            RequireUsable(actor);
            var links = ReadReceiptLinks(intent);
            var placement = ReadReceipt<RuntimeRunStartPlacementReceipt>(links, "placement");
            var registration = ReadReceipt<RuntimeRegistration>(links, "registration");
            var readiness = await environment.ReadReadinessAsync(
                actor,
                intent.ProjectId,
                intent.RunId,
                intent.RootSessionId,
                placement.EnvironmentId,
                registration.Binding.ProfileId,
                cancellationToken).ConfigureAwait(false);
            var hostReadiness = await CoordinationEndpoints.SendRuntimeHostAsync<RuntimeHostReadinessReceipt>(
                clients.CreateClient("runtime-suspend-host"),
                CoordinationEndpoints.RuntimeHostEndpoint(
                    registration.Binding.ConfigureEndpoint, "/runtime/v1/readiness"),
                actor,
                body: null,
                allowNotReady: true,
                cancellationToken).ConfigureAwait(false);
            if (hostReadiness is null)
                return new RuntimeStartupSummary("pending", "readiness", "runtime_host_not_ready");
            CoordinationEndpoints.ValidateHostReadiness(hostReadiness, registration, timeProvider);
            RequireCurrentReadiness(placement, registration, hostReadiness, readiness);
            return intent.Summary;
        }
        catch (RuntimeAuthorizationException exception)
        {
            var state = exception.Code is "runtime_owner_unavailable" or "runtime_owner_timeout"
                ? "pending"
                : "blocked";
            return new RuntimeStartupSummary(state, "readiness", exception.Code);
        }
        catch (HttpRequestException)
        {
            return new RuntimeStartupSummary("pending", "readiness", "runtime_owner_unavailable");
        }
        catch (CoordinationException exception) when (
            exception.Code == "maf_execution_host_unavailable" ||
            exception.Code == "maf_execution_host_request_rejected" &&
                exception.StatusCode >= StatusCodes.Status500InternalServerError ||
            exception.Code is "runtime_owner_unavailable" or "runtime_owner_timeout")
        {
            return new RuntimeStartupSummary("pending", "readiness", exception.Code);
        }
        catch (CoordinationException exception)
        {
            return new RuntimeStartupSummary("blocked", "readiness", exception.Code);
        }
        catch (JsonException)
        {
            return new RuntimeStartupSummary("blocked", "readiness", "runtime_start_readiness_receipt_invalid");
        }
    }

    private static T ReadReceipt<T>(Dictionary<string, JsonElement> links, string key)
    {
        if (!links.TryGetValue(key, out var value))
            throw new RuntimeAuthorizationException("runtime_start_readiness_receipt_invalid");
        return value.Deserialize<T>(JsonOptions)
            ?? throw new RuntimeAuthorizationException("runtime_start_readiness_receipt_invalid");
    }

    internal static string FailureCode(Exception exception) => exception switch
    {
        RuntimeAuthorizationException authorization => authorization.Code,
        CoordinationException coordination => coordination.Code,
        HttpRequestException => "runtime_start_owner_unavailable",
        OperationCanceledException => "runtime_start_cancelled",
        _ => "runtime_start_unavailable"
    };

    private async Task<RuntimeRunStartIntent> SaveAsync(
        RuntimeRunStartIntent intent,
        RuntimeOwnerContext owner,
        string step,
        string? failure,
        string? environmentId,
        Guid? runtimeInstanceId,
        CancellationToken cancellationToken,
        params (string Key, object Value)[] receipts)
    {
        _ = await store.EnsureIntentAsync(owner, cancellationToken).ConfigureAwait(false);
        var links = ReadReceiptLinks(intent);
        foreach (var (key, value) in receipts)
            links[key] = JsonSerializer.SerializeToElement(value, JsonOptions);
        return await store.UpdateAsync(
            intent,
            intent.RowVersion,
            "pending",
            step,
            failure,
            environmentId ?? intent.EnvironmentId,
            runtimeInstanceId ?? intent.RuntimeInstanceId,
            links,
            cancellationToken).ConfigureAwait(false);
    }

    private static Dictionary<string, JsonElement> ReadReceiptLinks(RuntimeRunStartIntent intent) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(intent.ReceiptLinks, JsonOptions)
        ?? new(StringComparer.Ordinal);

    private static object RegistrationReceipt(RuntimeRegistration registration) => registration;

    private static object BootstrapReceipt(RuntimeBootstrapDeliveryReceipt receipt) => new
    {
        receipt.OperationId,
        receipt.GrantId,
        receipt.RuntimeInstanceId,
        receipt.RegistrationRevision,
        receipt.PlacementUid,
        receipt.PlacementGeneration,
        receipt.ExecutionFence,
        receipt.ConfigurationHash,
        receipt.DeliveredAt,
        receipt.EnvironmentCurrentFencingGeneration,
        receipt.EnvironmentProviderFencingGeneration
    };

    internal static void RequireRegistration(
        RuntimeOwnerContext owner,
        RuntimeRunStartPlacementReceipt placement,
        RuntimeRegistration registration)
    {
        RuntimeContractValidation.Validate(registration);
        var binding = registration.Binding;
        if (placement.WorkspaceDataGeneration != 0)
            throw new RuntimeAuthorizationException("runtime_start_workspace_not_empty");
        if (registration.State != RuntimeRegistrationState.Active ||
            binding.ActorIssuer != owner.ActorIssuer || binding.ActorId != owner.ActorId ||
            binding.TenantId != owner.TenantId || binding.ProjectId != owner.ProjectId ||
            binding.RunId != owner.RunId || binding.SessionId != owner.SessionId ||
            binding.AgentId != RuntimeActionContract.CoordinatorRootStepId ||
            !IsResolvedProfileId(placement.SourceProfileId, binding.ProfileId) ||
            binding.AcceptedSelectionHash != owner.AcceptedSelectionHash ||
            binding.ExecutionFence != owner.ExecutionFence ||
            binding.EnvironmentId != placement.EnvironmentId ||
            binding.PlacementGeneration != placement.SandboxResource.Generation ||
            binding.EnvironmentCurrentFencingGeneration != placement.CurrentFencingGeneration ||
            binding.EnvironmentProviderFencingGeneration != placement.ProviderFencingGeneration)
            throw new RuntimeAuthorizationException("runtime_start_registration_stale");
    }

    private static bool IsResolvedProfileId(string sourceProfileId, string resolvedProfileId)
    {
        if (string.Equals(sourceProfileId, resolvedProfileId, StringComparison.Ordinal))
            return true;
        var prefix = $"{sourceProfileId[..Math.Min(sourceProfileId.Length, 220)]}.r.";
        return resolvedProfileId.StartsWith(prefix, StringComparison.Ordinal) &&
            resolvedProfileId.Length == prefix.Length + 24 &&
            resolvedProfileId.AsSpan(prefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;
    }

    private static void RequireBootstrapReceipt(
        RuntimeRunStartIntent intent,
        RuntimeRegistration registration,
        RuntimeBootstrapDeliveryReceipt receipt,
        string configurationHash)
    {
        if (receipt.OperationId != intent.BootstrapOperationId ||
            receipt.RuntimeInstanceId != registration.RuntimeInstanceId ||
            receipt.RegistrationRevision != registration.Revision ||
            receipt.PlacementUid != registration.Binding.PlacementUid ||
            receipt.PlacementGeneration != registration.Binding.PlacementGeneration ||
            receipt.ExecutionFence != registration.Binding.ExecutionFence ||
            receipt.ConfigurationHash != configurationHash ||
            receipt.EnvironmentCurrentFencingGeneration !=
                registration.Binding.EnvironmentCurrentFencingGeneration ||
            receipt.EnvironmentProviderFencingGeneration !=
                registration.Binding.EnvironmentProviderFencingGeneration)
            throw new RuntimeAuthorizationException("runtime_start_bootstrap_receipt_invalid");
    }

    private static void RequirePlacement(
        RuntimeRunStartPlacementReceipt placement,
        RuntimeRegistration registration,
        EnvironmentRuntimeBootstrapContext context)
    {
        if (context.ContractVersion != 1 ||
            context.TenantId != registration.Binding.TenantId ||
            context.ProjectId != registration.Binding.ProjectId ||
            context.RunId != registration.Binding.RunId ||
            context.EnvironmentId != placement.EnvironmentId ||
            context.ProfileId != registration.Binding.ProfileId ||
            context.Resource != placement.SandboxResource ||
            context.LifecycleGeneration != placement.LifecycleGeneration ||
            context.LeaseRevision != placement.LeaseRevision ||
            context.CurrentFencingGeneration != placement.CurrentFencingGeneration ||
            context.ProviderFencingGeneration != placement.ProviderFencingGeneration ||
            context.LeaseExpiresAt != placement.LeaseExpiresAt ||
            context.Resource.ResourceId != registration.Binding.PlacementUid)
            throw new RuntimeAuthorizationException("runtime_start_placement_stale");
    }

    private void RequireCurrentReadiness(
        RuntimeRunStartPlacementReceipt placement,
        RuntimeRegistration registration,
        RuntimeHostReadinessReceipt activated,
        EnvironmentRuntimeReadinessContext readiness)
    {
        var now = timeProvider.GetUtcNow();
        var current = readiness.Placement;
        var observation = readiness.Observation;
        var pod = observation.PodObservation;
        var runtimeEndpoint = observation.RuntimeEndpoint;
        var image = registration.Binding.Image
            ?? throw new RuntimeAuthorizationException("runtime_start_readiness_stale");
        var hostPhases = activated.StartupPhases;
        var providerPhases = observation.StartupPhases;
        var hostPhasesValid = !hostPhases.IsDefault &&
            hostPhases.Length == 5 &&
            hostPhases.All(phase => phase is not null &&
                phase.ContractVersion == 1 && Enum.IsDefined(phase.Phase) &&
                phase.ObservedAt >= readiness.LeaseCreatedAt && phase.ObservedAt <= now) &&
            hostPhases.Select(phase => phase.Phase).Distinct().Count() == hostPhases.Length &&
            hostPhases.Any(phase => phase.Phase == SandboxStartupPhase.Scheduled) &&
            hostPhases.Any(phase => phase.Phase == SandboxStartupPhase.Started) &&
            hostPhases.Any(phase => phase.Phase == SandboxStartupPhase.ImageReady &&
                phase.ImageDigest == image.Digest &&
                phase.CompressedPullBytes == image.CompressedPullBytes) &&
            hostPhases.Any(phase => phase.Phase == SandboxStartupPhase.Configured) &&
            hostPhases.Any(phase => phase.Phase == SandboxStartupPhase.Ready) &&
            !providerPhases.IsDefault &&
            providerPhases.Length == 3 &&
            providerPhases.All(phase => phase is not null && hostPhases.Contains(phase)) &&
            providerPhases.Select(phase => phase.Phase).Distinct().Count() == providerPhases.Length &&
            providerPhases.Any(phase => phase.Phase == SandboxStartupPhase.Scheduled) &&
            providerPhases.Any(phase => phase.Phase == SandboxStartupPhase.Started) &&
            providerPhases.Any(phase => phase.Phase == SandboxStartupPhase.ImageReady);
        var grant = activated.SourceGrant;
        if (readiness.ContractVersion != 1 ||
            current.EnvironmentId != placement.EnvironmentId ||
            current.Resource != placement.SandboxResource ||
            current.LifecycleGeneration != placement.LifecycleGeneration ||
            current.LeaseRevision != placement.LeaseRevision ||
            current.CurrentFencingGeneration != placement.CurrentFencingGeneration ||
            current.ProviderFencingGeneration != placement.ProviderFencingGeneration ||
            current.LeaseExpiresAt != placement.LeaseExpiresAt ||
            current.LeaseExpiresAt <= now ||
            readiness.WorkspaceMountPath != "/workspace" ||
            current.ProfileId != registration.Binding.ProfileId ||
            current.Image != image ||
            activated.ContractVersion != 1 ||
            activated.RuntimeInstanceId != registration.RuntimeInstanceId ||
            activated.RegistrationRevision != registration.Revision ||
            activated.ExecutionFence != registration.Binding.ExecutionFence ||
            activated.Image != image ||
            !hostPhasesValid ||
            grant.Purpose != RuntimeCredentialPurpose.Observe ||
            grant.State != RuntimeCredentialState.Active ||
            grant.RuntimeInstanceId != registration.RuntimeInstanceId ||
            grant.RegistrationRevision != registration.Revision ||
            grant.ExpiresAt <= now || grant.ExpiresAt > registration.ExpiresAt ||
            current.RuntimeEndpoint is null ||
            observation.Resource != placement.SandboxResource ||
            observation.State is not (SandboxObservedState.Pending or SandboxObservedState.Ready) ||
            observation.FencingGeneration != placement.ProviderFencingGeneration ||
            observation.VerifiedNetworkGeneration != placement.NetworkPolicyGeneration ||
            !observation.VmIsolationVerified || !observation.WorkspaceAttachmentVerified ||
            observation.StartupFailure is not null ||
            observation.ProvisionOperationId != placement.SandboxOperationId ||
            runtimeEndpoint is null ||
            pod is null ||
            pod.Resource != placement.SandboxResource ||
            pod.FencingGeneration != placement.ProviderFencingGeneration ||
            pod.ProvisionOperationId != placement.SandboxOperationId ||
            pod.ClaimUid != placement.SandboxResource.ResourceId ||
            pod.WorkspaceAttachment is not { ReadOnly: false, MountPath: "/workspace" } ||
            pod.ObservedAt < readiness.LeaseCreatedAt ||
            pod.ObservedAt > now ||
            runtimeEndpoint.PodUid != pod.PodUid ||
            current.RuntimeEndpoint.PodUid != pod.PodUid)
            throw new RuntimeAuthorizationException("runtime_start_readiness_stale");
    }

    private static void RequireUsable(RuntimeActorAuthorization actor)
    {
        if (!actor.Bearer.IsUsable())
            throw new RuntimeAuthorizationException("runtime_start_actor_expired");
    }
}

internal sealed class RuntimeRunStartCleanupContinuation(
    CoordinationRuntimeRunStartStore store,
    ProjectsRunSelectionClient projects,
    RuntimeRunStartOptions options,
    IHttpClientFactory clients)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task ContinueAsync(RuntimeRunStartCleanupWork work, CancellationToken cancellationToken)
    {
        if (!work.Actor.Bearer.IsUsable())
            return;
        var intent = await store.FindCleanupIntentAsync(
            work.TenantId, work.ProjectId, work.RunId, cancellationToken).ConfigureAwait(false);
        if (intent is null || intent.State == "cleanup_complete")
            return;

        await using var lease = await store.AcquireAsync(intent, cancellationToken).ConfigureAwait(false);
        intent = await store.ReadAsync(intent, cancellationToken).ConfigureAwait(false) ?? intent;
        if (!intent.CleanupObligation || intent.State == "cleanup_complete" || !work.Actor.Bearer.IsUsable())
            return;

        var selection = await projects.ReadSelectionForCancelAsync(
            work.Actor, work.Caller, work.ProjectId, work.RunId, cancellationToken).ConfigureAwait(false);
        CoordinationOwnerStore.RequireProjectRunCancellationAuthority(work.Caller, selection);
        if (selection.Authorization.TenantId != work.TenantId)
            throw new CoordinationException("run_cancel_permission_denied", StatusCodes.Status403Forbidden);
        var projection = await store.ReadCleanupProjectionAsync(
            work.ProjectId, work.RunId, intent.RootSessionId, selection, cancellationToken)
            .ConfigureAwait(false);
        if (projection.CleanupDisposition == "cleanup_complete" || !work.Actor.Bearer.IsUsable())
            return;

        var receipt = await RuntimeOwnerHttpTransport.SendAsync<RuntimeRunStartCleanupReceipt>(
            clients.CreateClient("runtime-run-start-environment"),
            options.EnvironmentOwnerAddress,
            "/internal/runtime/run-start/placement",
            work.Actor,
            new RuntimeRunStartPlacementRequest(1, null, projection),
            cancellationToken).ConfigureAwait(false);
        receipt.Validate();
        if (receipt.IntentId != intent.IntentId)
            throw new RuntimeAuthorizationException("runtime_start_cleanup_receipt_invalid");
        if (projection.ReceiptLinks.TryGetProperty("placement", out var savedPlacement))
        {
            var placement = savedPlacement.Deserialize<RuntimeRunStartPlacementReceipt>(JsonOptions)
                ?? throw new RuntimeAuthorizationException("runtime_start_cleanup_receipt_invalid");
            if (receipt.LeaseState == "absent" ||
                receipt.SandboxOperationId != placement.SandboxOperationId ||
                receipt.ResourceGeneration != placement.SandboxResource.Generation ||
                receipt.ProviderFencingGeneration != placement.ProviderFencingGeneration ||
                receipt.SandboxIdempotencyKey != placement.SandboxIdempotencyKey)
                throw new RuntimeAuthorizationException("runtime_start_cleanup_receipt_invalid");
        }
        else if (receipt.LeaseState != "absent" &&
            receipt.SandboxIdempotencyKey != RuntimeRunStartKeys.SandboxIdempotencyKey(
                projection.TenantId, projection.ProjectId, projection.RunId,
                projection.RootSessionId, projection.AcceptedSelectionHash,
                projection.BootstrapOperationId))
            throw new RuntimeAuthorizationException("runtime_start_cleanup_receipt_invalid");
        if (!work.Actor.Bearer.IsUsable())
            return;
        if (receipt.CleanupDisposition == "cleanup_complete")
            _ = await store.MarkCleanupCompleteAsync(
                intent, intent.RowVersion, receipt, cancellationToken).ConfigureAwait(false);
        else
            _ = await store.RecordCleanupPendingAsync(
                intent, intent.RowVersion, receipt, cancellationToken).ConfigureAwait(false);
    }
}
