using System.Collections.Immutable;
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

internal sealed record RuntimeRunStartChildWork(
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
                        case RuntimeRunStartChildWork child:
                            await scope.ServiceProvider.GetRequiredService<RuntimeRunStartContinuation>()
                                .ContinueChildAsync(child.Owner, child.Actor, token).ConfigureAwait(false);
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
    ProjectsRunSelectionClient projects,
    CoordinationOwnerStore sessions,
    CoordinatorDecisionOwnerStore decisions,
    CoordinatorRunSelectionContextStore selectionContexts,
    PostgresMafCheckpointStore checkpoints,
    EventsAddressedMessageClient events,
    TimeProvider timeProvider,
    OrchestratorOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public async Task<RuntimeStartupSummary> EnqueueChildAsync(
        HttpContext context,
        SessionIdentity root,
        MafExecutionDispatchIntent dispatch,
        long executionFence,
        CancellationToken cancellationToken)
    {
        var intent = await store.ReadIntentForStatusAsync(
            root.ProjectId, root.RunId, dispatch.ChildSessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new CoordinationException("runtime_child_start_intent_unavailable", StatusCodes.Status409Conflict);
        var binding = CoordinationRuntimeRunStartStore.ReadChildBinding(intent);
        if (binding.ParentRootSessionId != root.SessionId ||
            binding.ChildSessionId != dispatch.ChildSessionId ||
            binding.AssociationId != dispatch.AssociationId ||
            binding.DispatchMessageId != dispatch.MessageId || binding.PromptHash != dispatch.PromptHash ||
            intent.ExecutionFence != executionFence)
            throw new CoordinationException("runtime_child_start_binding_conflict", StatusCodes.Status409Conflict);
        if (intent.CleanupObligation)
            return intent.Summary;

        var actorIdentity = CoordinationIdentity.RequireActor(context.User, options.Issuer);
        if (binding.ActorIssuer != actorIdentity.Issuer || binding.ActorId != actorIdentity.Subject)
            throw new CoordinationException("runtime_child_start_owner_stale", StatusCodes.Status409Conflict);
        var identity = new SessionIdentity(root.ProjectId, root.RunId, dispatch.ChildSessionId);
        // Dispatch polling must not wait for a startup worker; a busy lease means startup is in progress.
        await using (var lease = await store.TryAcquireAsync(intent, cancellationToken).ConfigureAwait(false))
        {
            if (lease is null)
                return intent.State == "ready" ? intent.Summary with { State = "pending" } : intent.Summary;
            intent = await store.ReadAsync(intent, cancellationToken).ConfigureAwait(false) ?? intent;
            if (intent.CleanupObligation || intent.State == "blocked" && intent.CurrentStep == "retirement")
                return intent.Summary;
            var completed = await sessions.ReadCompletedTurnBoundaryAsync(
                actorIdentity, identity, executionFence, binding.InitialStateVersion, cancellationToken)
                .ConfigureAwait(false);
            if (completed is null)
            {
                var boundary = await sessions.AdvanceTurnBoundaryAsync(
                    actorIdentity, identity, executionFence, binding.InitialStateVersion, cancellationToken)
                    .ConfigureAwait(false);
                var claim = await events.ClaimNextAsync(context, identity, cancellationToken).ConfigureAwait(false);
                if (claim is not null)
                {
                    if (claim.Owner != CoordinationIdentity.ClaimOwner(actorIdentity) ||
                        claim.Message.Recipient != identity || claim.Message.RecipientFence != executionFence)
                        throw new CoordinationException("events_claim_contract_invalid", StatusCodes.Status502BadGateway);
                    var presented = claim.Message.Status == AddressedMessageStatus.Delivered
                        ? claim.Message
                        : await events.PresentAsync(
                            context, identity, claim.Message.MessageId, claim.Message.ClaimFence, cancellationToken)
                            .ConfigureAwait(false);
                    await sessions.RecordPresentationAsync(
                        actorIdentity, identity, presented, cancellationToken).ConfigureAwait(false);
                    boundary = boundary with { PresentedMessage = presented };
                }
                _ = await sessions.CompleteTurnBoundaryAsync(
                    actorIdentity, identity, executionFence, binding.InitialStateVersion,
                    boundary.StateVersion, boundary.PresentedMessage, cancellationToken).ConfigureAwait(false);
            }
        }

        var owner = await CoordinationEndpoints.ReadRuntimeOwnerContextCoreAsync(
            root.ProjectId, root.RunId, identity.SessionId, context, options, projects, sessions,
            decisions, selectionContexts, checkpoints, cancellationToken).ConfigureAwait(false);
        intent = await store.EnsureChildIntentAsync(owner, cancellationToken).ConfigureAwait(false);
        var actor = await CreateActorAsync(context, timeProvider).ConfigureAwait(false);
        try
        {
            if (intent.State == "ready")
            {
                var summary = await continuation.RevalidateReadyAsync(intent, actor, cancellationToken)
                    .ConfigureAwait(false);
                actor.Bearer.Invalidate();
                return summary;
            }
            if (intent.CleanupObligation || intent.State == "blocked" && intent.CurrentStep == "retirement")
            {
                actor.Bearer.Invalidate();
                return intent.Summary;
            }
            EnqueueOrThrow(queue, new RuntimeRunStartChildWork(owner, actor));
            return intent.Summary;
        }
        catch
        {
            actor.Bearer.Invalidate();
            throw;
        }
    }

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
                summary = await AddP1ProjectionAsync(context, intent, summary, null, cancellationToken)
                    .ConfigureAwait(false);
                actor.Bearer.Invalidate();
                return summary;
            }
            if (intent.CurrentStep.StartsWith("replacement-", StringComparison.Ordinal))
                throw new CoordinationException(
                    "runtime_replacement_in_progress", StatusCodes.Status409Conflict);
            if (intent.CleanupObligation)
            {
                actor.Bearer.Invalidate();
                return intent.Summary;
            }
            cancellationToken.ThrowIfCancellationRequested();
            EnqueueOrThrow(queue, new RuntimeRunStartContinueWork(owner, actor));
            return await AddP1ProjectionAsync(context, intent, intent.Summary, null, cancellationToken)
                .ConfigureAwait(false);
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
        AuthorizedRunSelection selection,
        CancellationToken cancellationToken)
    {
        var intent = await store.ReadIntentForStatusAsync(
            projectId, runId, rootSessionId, cancellationToken).ConfigureAwait(false);
        if (intent is null)
            return null;
        if (intent.State != "ready")
        {
            return await AddP1ProjectionAsync(context, intent, intent.Summary, selection, cancellationToken)
                .ConfigureAwait(false);
        }
        var actor = await CreateActorAsync(context, timeProvider).ConfigureAwait(false);
        try
        {
            var summary = await continuation.RevalidateReadyAsync(intent, actor, cancellationToken)
                .ConfigureAwait(false);
            return await AddP1ProjectionAsync(context, intent, summary, selection, cancellationToken)
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

    internal static async Task<RuntimeActorAuthorization> CreateActorAsync(
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

    private async Task<RuntimeStartupSummary> AddP1ProjectionAsync(
        HttpContext context,
        RuntimeRunStartIntent intent,
        RuntimeStartupSummary summary,
        AuthorizedRunSelection? selection,
        CancellationToken cancellationToken)
    {
        summary = AddVerifiedRuntimeProjection(intent, summary);
        var actor = CoordinationIdentity.RequireActor(context.User, options.Issuer);
        // Status passes its own selection so its final authority recheck covers this projection.
        selection ??= await projects.ReadAcceptedSelectionWithAuthorityAsync(
            context, intent.ProjectId, intent.RunId, cancellationToken).ConfigureAwait(false);
        P1InvocationBinding binding;
        try
        {
            binding = await sessions.ReadCurrentP1InvocationBindingAsync(
                actor, selection, cancellationToken).ConfigureAwait(false);
        }
        catch (CoordinationException exception) when (exception.Code == "p1_invocation_binding_not_found")
        {
            return summary;
        }
        if (binding.RootSessionId != intent.RootSessionId ||
            binding.TenantId != intent.TenantId ||
            binding.AcceptedSelectionHash != intent.AcceptedSelectionHash ||
            binding.ExecutionFence != intent.ExecutionFence ||
            binding.ActorIssuer != actor.Issuer ||
            binding.ActorSubject != actor.Subject)
            throw new CoordinationException(
                "p1_invocation_binding_stale", StatusCodes.Status409Conflict);

        RuntimeRegistration? registration = null;
        RuntimeRunStartPlacementReceipt? placement = null;
        RuntimeHostReadinessReceipt? readiness = null;
        if (summary.State == "ready")
        {
            using var links = JsonDocument.Parse(intent.ReceiptLinks);
            if (!links.RootElement.TryGetProperty("registration", out var value) ||
                !links.RootElement.TryGetProperty("placement", out var placementValue) ||
                !links.RootElement.TryGetProperty("activation", out var activationValue))
                throw new CoordinationException(
                    "runtime_start_readiness_receipt_invalid", StatusCodes.Status503ServiceUnavailable);
            registration = value.Deserialize<RuntimeRegistration>(JsonOptions)
                ?? throw new CoordinationException(
                    "runtime_start_readiness_receipt_invalid", StatusCodes.Status503ServiceUnavailable);
            placement = placementValue.Deserialize<RuntimeRunStartPlacementReceipt>(JsonOptions)
                ?? throw new CoordinationException(
                    "runtime_start_readiness_receipt_invalid", StatusCodes.Status503ServiceUnavailable);
            readiness = activationValue.Deserialize<RuntimeHostReadinessReceipt>(JsonOptions)
                ?? throw new CoordinationException(
                    "runtime_start_readiness_receipt_invalid", StatusCodes.Status503ServiceUnavailable);
            if (registration.State != RuntimeRegistrationState.Active ||
                registration.Binding.SessionId != intent.RootSessionId ||
                registration.Binding.AcceptedSelectionHash != intent.AcceptedSelectionHash ||
                registration.Binding.ExecutionFence != intent.ExecutionFence ||
                readiness.RuntimeInstanceId != registration.RuntimeInstanceId ||
                readiness.RegistrationRevision != registration.Revision ||
                readiness.ExecutionFence != intent.ExecutionFence ||
                string.IsNullOrWhiteSpace(placement.PodUid))
                throw new CoordinationException(
                    "runtime_start_readiness_receipt_invalid", StatusCodes.Status503ServiceUnavailable);
        }

        return summary with
        {
            P1CoreInvocationId = binding.CoreInvocationId,
            P1SetupInvocationId = binding.SetupInvocationId,
            RuntimeInstanceId = registration?.RuntimeInstanceId,
            RegistrationRevision = registration?.Revision,
            AcceptedSelectionHash = registration?.Binding.AcceptedSelectionHash,
            ExecutionFence = registration?.Binding.ExecutionFence,
            ModelSelectionReference = registration?.Binding.ModelSelectionReference,
            ModelSourceMode = registration?.Binding.ModelSourceMode switch
            {
                ModelSourceMode.Byok => "byok",
                ModelSourceMode.HostedCopilot => "hostedCopilot",
                _ => null
            },
            ReasoningEffort = registration?.Binding.ReasoningEffort,
            MaxPromptTokens = registration?.Binding.MaxPromptTokens,
            ImageDigest = readiness?.Image.Digest,
            ImagePlatform = readiness?.Image.Platform,
            PodUid = placement?.PodUid
        };
    }

    private static RuntimeStartupSummary AddVerifiedRuntimeProjection(
        RuntimeRunStartIntent intent, RuntimeStartupSummary summary)
    {
        if (summary.State != "ready")
            return summary;
        using var links = JsonDocument.Parse(intent.ReceiptLinks);
        if (!links.RootElement.TryGetProperty("registration", out var registrationValue) ||
            !links.RootElement.TryGetProperty("placement", out var placementValue) ||
            !links.RootElement.TryGetProperty("activation", out var activationValue))
            throw new CoordinationException(
                "runtime_start_readiness_receipt_invalid", StatusCodes.Status503ServiceUnavailable);
        var registration = registrationValue.Deserialize<RuntimeRegistration>(JsonOptions)
            ?? throw new CoordinationException(
                "runtime_start_readiness_receipt_invalid", StatusCodes.Status503ServiceUnavailable);
        var placement = placementValue.Deserialize<RuntimeRunStartPlacementReceipt>(JsonOptions)
            ?? throw new CoordinationException(
                "runtime_start_readiness_receipt_invalid", StatusCodes.Status503ServiceUnavailable);
        var readiness = activationValue.Deserialize<RuntimeHostReadinessReceipt>(JsonOptions)
            ?? throw new CoordinationException(
                "runtime_start_readiness_receipt_invalid", StatusCodes.Status503ServiceUnavailable);
        var binding = registration.Binding;
        if (registration.State != RuntimeRegistrationState.Active ||
            registration.RuntimeInstanceId != readiness.RuntimeInstanceId ||
            registration.Revision != readiness.RegistrationRevision ||
            binding.ProjectId != intent.ProjectId || binding.RunId != intent.RunId ||
            binding.SessionId != intent.RootSessionId ||
            binding.AcceptedSelectionHash != intent.AcceptedSelectionHash ||
            binding.ExecutionFence != intent.ExecutionFence ||
            readiness.ExecutionFence != intent.ExecutionFence ||
            readiness.Image != binding.Image ||
            string.IsNullOrWhiteSpace(placement.PodUid))
            throw new CoordinationException(
                "runtime_start_readiness_receipt_invalid", StatusCodes.Status503ServiceUnavailable);
        return summary with
        {
            RuntimeInstanceId = registration.RuntimeInstanceId,
            RegistrationRevision = registration.Revision,
            AcceptedSelectionHash = binding.AcceptedSelectionHash,
            ExecutionFence = binding.ExecutionFence,
            ModelSelectionReference = binding.ModelSelectionReference,
            ModelSourceMode = binding.ModelSourceMode switch
            {
                Agentweaver.Abstractions.ModelSourceMode.Byok => "byok",
                Agentweaver.Abstractions.ModelSourceMode.HostedCopilot => "hostedCopilot",
                _ => null
            },
            ReasoningEffort = binding.ReasoningEffort,
            MaxPromptTokens = binding.MaxPromptTokens,
            ImageDigest = readiness.Image.Digest,
            ImagePlatform = readiness.Image.Platform,
            PodUid = placement.PodUid
        };
    }
}

internal sealed partial class RuntimeRunStartContinuation(
    CoordinationRuntimeRunStartStore store,
    RuntimeRegistrationOwner registrations,
    RuntimeEnvironmentContextClient environment,
    RuntimeRunStartOptions options,
    IHttpClientFactory clients,
    TimeProvider timeProvider,
    EventsAddressedMessageClient? events = null,
    RuntimeUsageSourceStore? usageSources = null)
{
    private sealed record CheckpointRecoveryMemberContinuation(
        ChildRecoveryMember Member,
        RuntimeRunStartIntent Intent,
        RuntimeOwnerContext Owner,
        RuntimeRegistration PreviousRegistration,
        RuntimeRegistration Registration,
        RuntimeRunStartPlacementReceipt PreviousPlacement,
        RuntimeRunStartPlacementReceipt Placement,
        Guid BootstrapId,
        Guid ConsumeId,
        Guid ExchangeId);

    internal static async Task<bool> RegisterCheckpointRecoveryMembersBeforeRootBootstrapAsync(
        ChildCheckpointRecoveryManifest manifest,
        bool rootRegistered,
        Func<ChildRecoveryMember, CancellationToken, Task<bool>> registerMember,
        Func<CancellationToken, Task> bootstrapRoot,
        CancellationToken cancellationToken)
    {
        var registered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in CheckpointRecoveryMemberBarrier.RequiredMembers(manifest))
        {
            if (!await registerMember(member, cancellationToken).ConfigureAwait(false))
                return false;
            registered.Add(member.AssociationId);
        }
        CheckpointRecoveryMemberBarrier.RequireAllRegistered(manifest, rootRegistered, registered);
        await bootstrapRoot(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly IReadOnlySet<string> CheckpointRecoverySpentBootstrapFailureCodes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "runtime_delivery_unavailable",
            "runtime_receiver_unavailable",
            "runtime_source_credential_unavailable",
            "runtime_configuration_conflict",
            "runtime_operation_conflict"
        };
    private static readonly IReadOnlySet<string> CheckpointRecoveryActivationFailureCodes =
        new HashSet<string>(CheckpointRecoverySpentBootstrapFailureCodes, StringComparer.Ordinal)
        {
            "runtime_activation_owner_unavailable",
            "runtime_activation_owner_timeout",
            "runtime_owner_unavailable",
            "runtime_owner_timeout"
        };

    public async Task ContinueAsync(
        RuntimeOwnerContext owner,
        RuntimeActorAuthorization actor,
        CancellationToken cancellationToken) =>
        await ContinueCoreAsync(owner, actor, isChild: false, cancellationToken).ConfigureAwait(false);

    public async Task ContinueChildAsync(
        RuntimeOwnerContext owner,
        RuntimeActorAuthorization actor,
        CancellationToken cancellationToken) =>
        await ContinueCoreAsync(owner, actor, isChild: true, cancellationToken).ConfigureAwait(false);

    private async Task ContinueCoreAsync(
        RuntimeOwnerContext owner,
        RuntimeActorAuthorization actor,
        bool isChild,
        CancellationToken cancellationToken)
    {
        if (!actor.Bearer.IsUsable())
            return;
        var intent = await (isChild
            ? store.EnsureChildIntentAsync(owner, cancellationToken)
            : store.EnsureIntentAsync(owner, cancellationToken)).ConfigureAwait(false);
        await using var lease = await store.AcquireAsync(intent, cancellationToken).ConfigureAwait(false);
        intent = await store.ReadAsync(intent, cancellationToken).ConfigureAwait(false) ?? intent;
        if (intent.CleanupObligation || isChild && intent.CurrentStep == "retirement")
            return;

        var callerToken = cancellationToken;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(180));
        cancellationToken = deadline.Token;
        try
        {
            RequireUsable(actor);
            if (!isChild && intent.CurrentStep == "retirement")
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
                isChild ? "/internal/runtime/run-start/child-placement" : "/internal/runtime/run-start/placement",
                actor,
                new RuntimeRunStartPlacementRequest(isChild ? 2 : 1, owner, null, intent.BootstrapOperationId),
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
            var registration = await (isChild
                ? registrations.RegisterChildStartAsync(
                    actor, owner, placement.EnvironmentId, placement.SourceProfileId, cancellationToken)
                : registrations.RegisterAsync(
                    actor, owner, placement.EnvironmentId, placement.SourceProfileId, cancellationToken))
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
                RequireCurrentReadiness(timeProvider, placement, registration, activated, readiness);
                var receipt = ReadReceiptLinks(intent);
                receipt["readiness"] = JsonSerializer.SerializeToElement(readiness, JsonOptions);
                _ = await store.CommitReadyAsync(
                    intent, owner, intent.RowVersion, receipt, cancellationToken, isChild).ConfigureAwait(false);
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
        catch (RuntimeAuthorizationException exception) when (
            exception.Code == "runtime_delivery_failed" && !isChild)
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
        catch (RuntimeAuthorizationException exception) when (
            exception.Code == "runtime_delivery_failed" && isChild)
        {
            // Children have no retirement contract. The used bootstrap operation cannot be retried, so the
            // intent keeps its placement and registration links until terminal cleanup releases them.
            var current = await store.ReadAsync(intent, CancellationToken.None).ConfigureAwait(false) ?? intent;
            if (!current.CleanupObligation && current.State != "ready")
                _ = await store.UpdateAsync(
                    current,
                    current.RowVersion,
                    "blocked",
                    "retirement",
                    exception.Code,
                    current.EnvironmentId,
                    current.RuntimeInstanceId,
                    ReadReceiptLinks(current),
                    CancellationToken.None).ConfigureAwait(false);
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

    public async Task<RuntimeReplacementSummary> ReplaceAsync(
        RuntimeOwnerContext owner,
        RuntimeActorAuthorization actor,
        RuntimeReplacementRequest request,
        RuntimeRegistration registration,
        RuntimeRunStartPlacementReceipt placement,
        RuntimeRootTurnCompletionLedgerReceipt completion,
        CancellationToken cancellationToken)
    {
        request.Validate();
        RuntimeContractValidation.Validate(registration);
        placement.Validate();
        completion.Validate();
        var intent = await store.EnsureIntentAsync(owner, cancellationToken).ConfigureAwait(false);
        await using var lease = await store.AcquireAsync(intent, cancellationToken).ConfigureAwait(false);
        intent = await store.ReadAsync(intent, cancellationToken).ConfigureAwait(false) ?? intent;
        var (updatedIntent, operation) = await store.BeginReplacementAsync(
            intent, owner, request, registration, placement, completion, cancellationToken).ConfigureAwait(false);
        intent = updatedIntent;
        var operationLinks = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            operation.ReceiptLinks, JsonOptions) ?? new(StringComparer.Ordinal);
        var intentLinks = ReadReceiptLinks(intent);
        var previousRegistration = ReadReceipt<RuntimeRegistration>(operationLinks, "previousRegistration");
        var previousPlacement = ReadReceipt<RuntimeRunStartPlacementReceipt>(operationLinks, "previousPlacement");
        var rootCompletion = ReadReceipt<RuntimeRootTurnCompletionLedgerReceipt>(operationLinks, "completion");
        var previousBootstrapId = ReadGuid(operationLinks, "previousBootstrapOperationId");
        var bootstrapId = RuntimeRunStartKeys.ReplacementOperationId(request.OperationId, "bootstrap");
        var consumeId = RuntimeRunStartKeys.ReplacementOperationId(request.OperationId, "consume");
        var exchangeId = RuntimeRunStartKeys.ReplacementOperationId(request.OperationId, "exchange");

        if (operation.State == "ready")
            return CreateReplacementSummary(operation, previousRegistration, previousPlacement);

        async Task PersistAsync(
            string step,
            string? failure = null,
            string? environmentId = null,
            Guid? runtimeInstanceId = null)
        {
            (intent, operation) = await store.UpdateReplacementProgressAsync(
                intent,
                operation,
                "pending",
                step,
                failure,
                JsonSerializer.Serialize(operationLinks, JsonOptions),
                JsonSerializer.Serialize(intentLinks, JsonOptions),
                step == "replacement-retirement" ? null : bootstrapId,
                step == "replacement-retirement" ? null : consumeId,
                step == "replacement-retirement" ? null : exchangeId,
                environmentId ?? intent.EnvironmentId,
                runtimeInstanceId ?? intent.RuntimeInstanceId,
                cancellationToken).ConfigureAwait(false);
        }

        if (!operationLinks.TryGetValue("retirement", out var retirementElement) ||
            retirementElement.Deserialize<RuntimeRunStartRetirementReceipt>(JsonOptions)?.State != "released")
        {
            var retirementRequest = new RuntimeRunStartRetirementRequest(
                1,
                owner,
                previousBootstrapId,
                previousRegistration.RuntimeInstanceId,
                previousRegistration.Revision,
                previousPlacement).Validate();
            var retirement = await RuntimeOwnerHttpTransport.SendRuntimeRunStartAsync<
                    RuntimeRunStartRetirementReceipt>(
                clients.CreateClient("runtime-run-start-environment"),
                options.EnvironmentOwnerAddress,
                "/internal/runtime/run-start/replacement-retire",
                actor,
                new RuntimeReplacementRetirementRequest(
                    1, request.OperationId, intent.IntentId, retirementRequest),
                cancellationToken).ConfigureAwait(false);
            retirement.ValidateFor(retirementRequest);
            operationLinks["retirement"] = JsonSerializer.SerializeToElement(retirement, JsonOptions);
            if (retirement.State != "released")
            {
                await PersistAsync(
                    "replacement-retirement",
                    "runtime_replacement_retirement_pending",
                    previousPlacement.EnvironmentId,
                    previousRegistration.RuntimeInstanceId).ConfigureAwait(false);
                return CreateReplacementSummary(operation, previousRegistration, previousPlacement);
            }

            var priorAttempt = new
            {
                bootstrapOperationId = previousBootstrapId,
                consumeOperationId = ReadGuid(operationLinks, "previousConsumeOperationId"),
                exchangeOperationId = ReadGuid(operationLinks, "previousExchangeOperationId"),
                registration = previousRegistration,
                placement = previousPlacement,
                completion = rootCompletion,
                retirement
            };
            intentLinks["previousReplacementAttempt"] =
                JsonSerializer.SerializeToElement(priorAttempt, JsonOptions);
            foreach (var key in new[] { "placement", "registration", "bootstrap", "activation", "readiness" })
                intentLinks.Remove(key);
            intentLinks["replacementOperationId"] =
                JsonSerializer.SerializeToElement(request.OperationId, JsonOptions);
            intentLinks["replacementCompletion"] =
                JsonSerializer.SerializeToElement(rootCompletion, JsonOptions);
            intentLinks["replacementRetirement"] =
                JsonSerializer.SerializeToElement(retirement, JsonOptions);
            var newBootstrapId = bootstrapId;
            intent = (await store.UpdateReplacementProgressAsync(
                intent,
                operation,
                "pending",
                "replacement-placement",
                null,
                JsonSerializer.Serialize(operationLinks, JsonOptions),
                JsonSerializer.Serialize(intentLinks, JsonOptions),
                newBootstrapId,
                consumeId,
                exchangeId,
                previousPlacement.EnvironmentId,
                null,
                cancellationToken).ConfigureAwait(false)).Intent;
            operation = await store.ReadReplacementOperationAsync(intent, request.OperationId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new CoordinationException(
                    "runtime_replacement_operation_not_found", StatusCodes.Status503ServiceUnavailable);
        }

        if (!operationLinks.TryGetValue("currentPlacement", out var currentPlacementElement))
        {
            var replacementPlacement = await RuntimeOwnerHttpTransport.SendRuntimeRunStartAsync<
                    RuntimeRunStartPlacementReceipt>(
                clients.CreateClient("runtime-run-start-environment"),
                options.EnvironmentOwnerAddress,
                "/internal/runtime/run-start/replacement-placement",
                actor,
                new RuntimeReplacementPlacementRequest(
                    1, request.OperationId, owner, previousBootstrapId, bootstrapId, previousPlacement),
                cancellationToken).ConfigureAwait(false);
            RequireReplacementPlacement(previousPlacement, replacementPlacement, bootstrapId, owner);
            operationLinks["currentPlacement"] =
                JsonSerializer.SerializeToElement(replacementPlacement, JsonOptions);
            intentLinks["placement"] = JsonSerializer.SerializeToElement(replacementPlacement, JsonOptions);
            await PersistAsync(
                "replacement-registration", null, replacementPlacement.EnvironmentId).ConfigureAwait(false);
            currentPlacementElement = operationLinks["currentPlacement"];
        }
        var currentPlacement = currentPlacementElement.Deserialize<RuntimeRunStartPlacementReceipt>(JsonOptions)
            ?? throw new RuntimeAuthorizationException("runtime_replacement_placement_invalid");
        RequireReplacementPlacement(previousPlacement, currentPlacement, bootstrapId, owner);

        RuntimeRegistration currentRegistration;
        if (operationLinks.TryGetValue("currentRegistration", out var currentRegistrationElement))
        {
            currentRegistration = currentRegistrationElement.Deserialize<RuntimeRegistration>(JsonOptions)
                ?? throw new RuntimeAuthorizationException("runtime_replacement_registration_invalid");
        }
        else
        {
            currentRegistration = await registrations.ReplaceOrdinaryAsync(
                actor,
                previousRegistration,
                request.OperationId,
                owner,
                currentPlacement.EnvironmentId,
                currentPlacement.SourceProfileId,
                cancellationToken).ConfigureAwait(false);
            RequireReplacementRegistration(owner, currentPlacement, currentRegistration);
            operationLinks["currentRegistration"] =
                JsonSerializer.SerializeToElement(currentRegistration, JsonOptions);
            intentLinks["registration"] = JsonSerializer.SerializeToElement(currentRegistration, JsonOptions);
            await PersistAsync(
                "replacement-bootstrap", null,
                currentPlacement.EnvironmentId, currentRegistration.RuntimeInstanceId).ConfigureAwait(false);
        }
        RequireReplacementRegistration(owner, currentPlacement, currentRegistration);

        RuntimeBootstrapDeliveryReceipt bootstrap;
        if (operationLinks.TryGetValue("currentBootstrap", out var bootstrapElement))
        {
            bootstrap = bootstrapElement.Deserialize<RuntimeBootstrapDeliveryReceipt>(JsonOptions)
                ?? throw new RuntimeAuthorizationException("runtime_replacement_bootstrap_invalid");
        }
        else
        {
            using var configuration = JsonDocument.Parse("{}");
            var configurationHash = RuntimeContractValidation.Hash(
                Encoding.UTF8.GetBytes(configuration.RootElement.GetRawText()));
            bootstrap = await RuntimeOwnerHttpTransport.SendRuntimeRunStartAsync<
                    RuntimeBootstrapDeliveryReceipt>(
                clients.CreateClient("runtime-run-start-broker"),
                options.BrokerOwnerAddress,
                "/internal/runtime/bootstrap/request",
                actor,
                new RuntimeBootstrapRequest(
                    currentRegistration.RuntimeInstanceId, bootstrapId, configurationHash),
                cancellationToken).ConfigureAwait(false);
            RequireBootstrapReceipt(
                intent with { BootstrapOperationId = bootstrapId }, currentRegistration, bootstrap, configurationHash);
            operationLinks["currentBootstrap"] =
                JsonSerializer.SerializeToElement(BootstrapReceipt(bootstrap), JsonOptions);
            intentLinks["bootstrap"] = operationLinks["currentBootstrap"];
            await PersistAsync(
                "replacement-activation", null,
                currentPlacement.EnvironmentId, currentRegistration.RuntimeInstanceId).ConfigureAwait(false);
        }

        var environmentContext = await environment.ReadAsync(
            actor, owner.ProjectId, owner.RunId, owner.SessionId,
            currentPlacement.EnvironmentId, currentRegistration.Binding.ProfileId, cancellationToken)
            .ConfigureAwait(false);
        RequirePlacement(currentPlacement, currentRegistration, environmentContext);
        RuntimeHostReadinessReceipt activated;
        if (operationLinks.TryGetValue("currentActivation", out var activationElement))
        {
            activated = activationElement.Deserialize<RuntimeHostReadinessReceipt>(JsonOptions)
                ?? throw new RuntimeAuthorizationException("runtime_replacement_activation_invalid");
        }
        else
        {
            var activationRequest = new RuntimeHostConfigureRequest(
                1, currentRegistration, consumeId, exchangeId, JsonDocument.Parse("{}").RootElement.Clone())
            {
                McpServer = environmentContext.McpServer,
                ReplacementRecovery = new RuntimeHostReplacementRecovery(
                    rootCompletion.Completion.Registration,
                    rootCompletion.Completion.ToEvidence(),
                    rootCompletion.Completion.CacheBaselineCursor)
                    .ValidateFor(currentRegistration)
            };
            activated = await RuntimeOwnerHttpTransport.SendRuntimeRunStartAsync<RuntimeHostReadinessReceipt>(
                clients.CreateClient("runtime-run-start-environment"),
                options.EnvironmentOwnerAddress,
                "/internal/runtime/v1/activate",
                actor,
                activationRequest,
                cancellationToken).ConfigureAwait(false);
            CoordinationEndpoints.ValidateHostReadiness(activated, currentRegistration, timeProvider);
            var recovery = activated.Recovery?.ValidateFor(currentRegistration);
            if (recovery?.RecoveryMode != RuntimeHostRecoveryMode.NativeCache ||
                recovery.NativeCacheAcknowledgment?.Reference != rootCompletion.Completion.CacheMaterial.Reference ||
                !MatchesReplacementReconstructionInterval(
                    recovery, rootCompletion.Completion.CacheBaselineCursorHash))
                throw new RuntimeAuthorizationException("runtime_replacement_cache_recovery_invalid");
            operationLinks["currentActivation"] =
                JsonSerializer.SerializeToElement(activated, JsonOptions);
            intentLinks["activation"] = operationLinks["currentActivation"];
            await PersistAsync(
                "replacement-readiness", null,
                currentPlacement.EnvironmentId, currentRegistration.RuntimeInstanceId).ConfigureAwait(false);
        }
        var recovered = activated.Recovery?.ValidateFor(currentRegistration);
        if (recovered?.RecoveryMode != RuntimeHostRecoveryMode.NativeCache ||
            recovered.NativeCacheAcknowledgment?.Reference != rootCompletion.Completion.CacheMaterial.Reference ||
            !MatchesReplacementReconstructionInterval(
                recovered, rootCompletion.Completion.CacheBaselineCursorHash))
            throw new RuntimeAuthorizationException("runtime_replacement_cache_recovery_invalid");

        EnvironmentRuntimeReadinessContext readiness;
        if (operationLinks.TryGetValue("currentReadiness", out var readinessElement))
        {
            readiness = readinessElement.Deserialize<EnvironmentRuntimeReadinessContext>(JsonOptions)
                ?? throw new RuntimeAuthorizationException("runtime_replacement_readiness_invalid");
        }
        else
        {
            readiness = await environment.ReadReadinessAsync(
                actor, owner.ProjectId, owner.RunId, owner.SessionId,
                currentPlacement.EnvironmentId, currentRegistration.Binding.ProfileId, cancellationToken)
                .ConfigureAwait(false);
            RequireCurrentReadiness(timeProvider, currentPlacement, currentRegistration, activated, readiness);
            operationLinks["currentReadiness"] =
                JsonSerializer.SerializeToElement(readiness, JsonOptions);
            intentLinks["readiness"] = operationLinks["currentReadiness"];
        }
        RequireCurrentReadiness(timeProvider, currentPlacement, currentRegistration, activated, readiness);
        (intent, operation) = await store.UpdateReplacementProgressAsync(
            intent,
            operation,
            "ready",
            "ready",
            null,
            JsonSerializer.Serialize(operationLinks, JsonOptions),
            JsonSerializer.Serialize(intentLinks, JsonOptions),
            bootstrapId,
            consumeId,
            exchangeId,
            currentPlacement.EnvironmentId,
            currentRegistration.RuntimeInstanceId,
            cancellationToken).ConfigureAwait(false);
        return CreateReplacementSummary(operation, previousRegistration, previousPlacement);
    }

    internal async Task<RunExecutionTransitionResult> ContinueCheckpointRecoveryAsync(
        HttpContext context,
        CoordinationActor actor,
        RuntimeActorAuthorization runtimeActor,
        RunExecutionTransitionResult result,
        RuntimeRunStartIntent intent,
        RuntimeRegistrationOwner registrationOwner,
        MafExecutionCheckpointStore checkpointStore,
        CoordinationOwnerStore recoveryStore,
        Func<CancellationToken, Task> requireCurrentSelection,
        Func<CancellationToken, Task> requireCurrentGate,
        CancellationToken cancellationToken,
        AuthorizedRunSelection? selection = null)
    {
        var status = result.CheckpointRecovery
            ?? throw new CoordinationException(
                "checkpoint_recovery_operation_unavailable", StatusCodes.Status409Conflict);
        if (status.Phase != "pending")
            return result;

        await using var continuationLease = await store.TryAcquireAsync(intent, cancellationToken)
            .ConfigureAwait(false);
        if (continuationLease is null)
            return result;
        status = await recoveryStore.ReadCheckpointRecoveryStatusAsync(
            actor, result.Session, result.OperationId, cancellationToken).ConfigureAwait(false);
        result = result with { CheckpointRecovery = status };
        if (status.Phase != "pending")
            return result;
        intent = await store.ReadAsync(intent, cancellationToken).ConfigureAwait(false)
            ?? throw new RuntimeAuthorizationException("checkpoint_recovery_runtime_stale");

        ChildCheckpointRecoveryManifest? childManifest = null;
        var memberContinuations = new List<CheckpointRecoveryMemberContinuation>();
        if (status.BoundaryKind == "childJoin")
        {
            childManifest = await recoveryStore.ReadCheckpointRecoveryManifestAsync(
                actor, result.Session, result.OperationId, cancellationToken).ConfigureAwait(false);
            _ = CheckpointRecoveryMemberBarrier.RequiredMembers(childManifest);
        }

        var links = ReadReceiptLinks(intent);
        var hasPreviousRuntimeEvidence = HasPreviousCheckpointRecoveryEvidence(result.OperationId, links);
        if (intent.State != "ready" ||
            intent.ExecutionFence != status.PreviousExecutionFence &&
                !(intent.ExecutionFence == status.ExecutionFence && hasPreviousRuntimeEvidence) ||
            !links.TryGetValue("registration", out var registrationElement) ||
            !links.TryGetValue("placement", out var placementElement) &&
                !hasPreviousRuntimeEvidence)
            throw new RuntimeAuthorizationException("checkpoint_recovery_runtime_stale");
        var previousRegistration = (hasPreviousRuntimeEvidence && links.TryGetValue(
                "checkpointRecoveryPreviousRegistration", out var previousRegistrationElement)
                ? previousRegistrationElement : registrationElement).Deserialize<RuntimeRegistration>(JsonOptions)
            ?? throw new RuntimeAuthorizationException("checkpoint_recovery_runtime_stale");
        var previousPlacement = (hasPreviousRuntimeEvidence && links.TryGetValue(
                "checkpointRecoveryPreviousPlacement", out var previousPlacementElement)
                ? previousPlacementElement : placementElement).Deserialize<RuntimeRunStartPlacementReceipt>(JsonOptions)
            ?? throw new RuntimeAuthorizationException("checkpoint_recovery_runtime_stale");
        previousPlacement.Validate();
        if (previousRegistration.RuntimeInstanceId != status.PreviousRuntimeInstanceId ||
            previousRegistration.Revision != status.PreviousRegistrationRevision)
            throw new RuntimeAuthorizationException("checkpoint_recovery_runtime_stale");
        var previousOwner = CreateOwnerContext(previousRegistration.Binding);
        var owner = CreateCheckpointRecoveryOwnerContext(
            previousRegistration.Binding, status.ExecutionFence);
        RequireReplacementRegistration(previousOwner, previousPlacement, previousRegistration);
        var previousBootstrapId = hasPreviousRuntimeEvidence && links.TryGetValue(
            "checkpointRecoveryPreviousBootstrapOperationId", out var previousBootstrapElement)
            ? previousBootstrapElement.GetGuid()
            : intent.BootstrapOperationId;
        var bootstrapId = RuntimeRunStartKeys.ReplacementOperationId(result.OperationId, "checkpoint-bootstrap");
        var consumeId = RuntimeRunStartKeys.ReplacementOperationId(result.OperationId, "checkpoint-consume");
        var exchangeId = RuntimeRunStartKeys.ReplacementOperationId(result.OperationId, "checkpoint-exchange");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(75));
        var token = deadline.Token;

        async Task PersistAsync(
            string step,
            string? reason = null,
            string? materialMode = null,
            string? phase = null,
            Guid? runtimeInstanceId = null,
            long? registrationRevision = null)
        {
            status = status with
            {
                CurrentStep = step,
                ReasonCode = reason,
                RecoveryMaterialMode = materialMode ?? status.RecoveryMaterialMode,
                Phase = phase ?? status.Phase,
                RuntimeInstanceId = runtimeInstanceId ?? status.RuntimeInstanceId,
                RegistrationRevision = registrationRevision ?? status.RegistrationRevision
            };
            result = await recoveryStore.UpdateCheckpointRecoveryProgressAsync(
                actor, result.Session, result.OperationId, status, cancellationToken,
                selection: selection).ConfigureAwait(false);
            status = result.CheckpointRecovery!;
        }

        async Task RequireBoundaryAsync(CancellationToken boundaryToken = default)
        {
            boundaryToken = boundaryToken.CanBeCanceled ? boundaryToken : token;
            await requireCurrentSelection(boundaryToken).ConfigureAwait(false);
            await requireCurrentGate(boundaryToken).ConfigureAwait(false);
            var latest = await checkpointStore.ReadLatestAsync(boundaryToken).ConfigureAwait(false);
            var lineage = latest?.State.RecoveryLineage;
            if (latest is null || latest.Info.CheckpointId != status.RecoveryCheckpointId ||
                latest.State.Revision != status.RecoveryCheckpointRevision ||
                latest.State.DecisionStateVersion != status.DecisionStateVersion ||
                lineage is null || lineage.SourceCheckpointId != status.SourceCheckpointId ||
                lineage.SourceCheckpointRevision != status.SourceCheckpointRevision ||
                lineage.SourceCheckpointHash != status.SourceCheckpointHash ||
                lineage.RecoveryOperationId != result.OperationId ||
                lineage.RecoveryExecutionFence != status.ExecutionFence)
                throw new RuntimeAuthorizationException("checkpoint_recovery_boundary_stale");
        }

        try
        {
            await RequireBoundaryAsync().ConfigureAwait(false);
            await PersistAsync("retirement").ConfigureAwait(false);
            var retirementRequest = new RuntimeRunStartRetirementRequest(
                1,
                owner,
                previousBootstrapId,
                previousRegistration.RuntimeInstanceId,
                previousRegistration.Revision,
                previousPlacement).Validate();
            var retirement = await RuntimeOwnerHttpTransport.SendRuntimeRunStartAsync<
                    RuntimeRunStartRetirementReceipt>(
                clients.CreateClient("runtime-run-start-environment"),
                options.EnvironmentOwnerAddress,
                "/internal/runtime/run-start/replacement-retire",
                runtimeActor,
                new RuntimeReplacementRetirementRequest(
                    1, result.OperationId, intent.IntentId, retirementRequest)
                {
                    CheckpointRecoveryOperationId = result.OperationId
                },
                token).ConfigureAwait(false);
            retirement.ValidateFor(retirementRequest);
            if (retirement.State != "released")
            {
                await PersistAsync("retirement", "runtime_replacement_retirement_pending").ConfigureAwait(false);
                return result;
            }

            await RequireBoundaryAsync().ConfigureAwait(false);
            await PersistAsync("placement").ConfigureAwait(false);
            var cleanupLinks = ReadReceiptLinks(intent);
            SetPreviousCheckpointRecoveryEvidence(
                cleanupLinks,
                result.OperationId,
                JsonSerializer.SerializeToElement(previousRegistration, JsonOptions),
                JsonSerializer.SerializeToElement(previousPlacement, JsonOptions),
                JsonSerializer.SerializeToElement(previousBootstrapId, JsonOptions));
            cleanupLinks["checkpointRecoveryRetirement"] =
                JsonSerializer.SerializeToElement(retirement, JsonOptions);
            cleanupLinks.Remove("placement");
            intent = await recoveryStore.PersistCheckpointRecoveryCleanupEvidenceAsync(
                intent,
                JsonSerializer.Serialize(cleanupLinks, JsonOptions),
                status.ExecutionFence,
                bootstrapId,
                RuntimeRunStartKeys.EnvironmentId(owner),
                null,
                CancellationToken.None).ConfigureAwait(false);
            var placement = await RuntimeOwnerHttpTransport.SendRuntimeRunStartAsync<
                    RuntimeRunStartPlacementReceipt>(
                clients.CreateClient("runtime-run-start-environment"),
                options.EnvironmentOwnerAddress,
                "/internal/runtime/run-start/replacement-placement",
                runtimeActor,
                new RuntimeReplacementPlacementRequest(
                    1, result.OperationId, owner, previousBootstrapId, bootstrapId, previousPlacement)
                {
                    CheckpointRecoveryOperationId = result.OperationId
                },
                token).ConfigureAwait(false);
            placement.Validate();
            cleanupLinks["placement"] = JsonSerializer.SerializeToElement(placement, JsonOptions);
            intent = await recoveryStore.PersistCheckpointRecoveryCleanupEvidenceAsync(
                intent,
                JsonSerializer.Serialize(cleanupLinks, JsonOptions),
                status.ExecutionFence,
                bootstrapId,
                placement.EnvironmentId,
                null,
                CancellationToken.None).ConfigureAwait(false);
            RequireReplacementPlacement(previousPlacement, placement, bootstrapId, owner);

            await RequireBoundaryAsync().ConfigureAwait(false);
            await PersistAsync("registration").ConfigureAwait(false);
            var authorization = new CheckpointRecoveryRegistrationAuthorization(result.OperationId, status);
            var registration = await registrationOwner.ReplaceAfterCheckpointRecoveryFenceAsync(
                context, authorization, result.Session.ProjectId, result.Session.RunId,
                result.Session.SessionId, token).ConfigureAwait(false);
            cleanupLinks["registration"] = JsonSerializer.SerializeToElement(registration, JsonOptions);
            intent = await recoveryStore.PersistCheckpointRecoveryCleanupEvidenceAsync(
                intent,
                JsonSerializer.Serialize(cleanupLinks, JsonOptions),
                status.ExecutionFence,
                bootstrapId,
                placement.EnvironmentId,
                registration.RuntimeInstanceId,
                CancellationToken.None).ConfigureAwait(false);
            RequireReplacementRegistration(owner, placement, registration);

            using var configuration = JsonDocument.Parse("{}");
            var configurationHash = RuntimeContractValidation.Hash(
                Encoding.UTF8.GetBytes(configuration.RootElement.GetRawText()));
            RuntimeBootstrapDeliveryReceipt? bootstrap = null;
            async Task BootstrapRootAsync(CancellationToken bootstrapToken)
            {
                await PersistAsync(
                    "bootstrap", runtimeInstanceId: registration.RuntimeInstanceId,
                    registrationRevision: registration.Revision).ConfigureAwait(false);
                await RequireBoundaryAsync(bootstrapToken).ConfigureAwait(false);
                bootstrap = await RuntimeOwnerHttpTransport.SendRuntimeRunStartAsync<
                        RuntimeBootstrapDeliveryReceipt>(
                    clients.CreateClient("runtime-run-start-broker"),
                    options.BrokerOwnerAddress,
                    "/internal/runtime/bootstrap/request",
                    runtimeActor,
                    new RuntimeBootstrapRequest(registration.RuntimeInstanceId, bootstrapId, configurationHash)
                    {
                        CheckpointRecoveryOperationId = result.OperationId
                    },
                    bootstrapToken,
                    CheckpointRecoverySpentBootstrapFailureCodes).ConfigureAwait(false);
                SetCheckpointRecoveryBootstrapEvidence(
                    cleanupLinks,
                    result.OperationId,
                    JsonSerializer.SerializeToElement(BootstrapReceipt(bootstrap), JsonOptions));
                intent = await recoveryStore.PersistCheckpointRecoveryCleanupEvidenceAsync(
                    intent,
                    JsonSerializer.Serialize(cleanupLinks, JsonOptions),
                    status.ExecutionFence,
                    bootstrapId,
                    placement.EnvironmentId,
                    registration.RuntimeInstanceId,
                    CancellationToken.None).ConfigureAwait(false);
                RequireBootstrapReceipt(
                    intent with { BootstrapOperationId = bootstrapId }, registration, bootstrap, configurationHash);
            }

            if (childManifest is not null)
            {
                await PersistAsync("member-registration").ConfigureAwait(false);
                var registered = await RegisterCheckpointRecoveryMembersBeforeRootBootstrapAsync(
                    childManifest,
                    rootRegistered: true,
                    async (member, memberToken) =>
                    {
                        await RequireBoundaryAsync(memberToken).ConfigureAwait(false);
                        var continuation = await PrepareCheckpointRecoveryMemberAsync(
                            context, actor, runtimeActor, result, status, childManifest, member, registrationOwner,
                            recoveryStore, RequireBoundaryAsync, memberToken).ConfigureAwait(false);
                        if (continuation is null)
                        {
                            await PersistAsync(
                                "member-registration", "checkpoint_recovery_member_registration_pending")
                                .ConfigureAwait(false);
                            return false;
                        }
                        memberContinuations.Add(continuation);
                        return true;
                    },
                    BootstrapRootAsync,
                    token).ConfigureAwait(false);
                if (!registered)
                    return result;
            }
            else
                await BootstrapRootAsync(token).ConfigureAwait(false);

            var bootstrapReceipt = bootstrap
                ?? throw new RuntimeAuthorizationException("runtime_start_bootstrap_receipt_invalid");

            await RequireBoundaryAsync().ConfigureAwait(false);
            await PersistAsync("activation").ConfigureAwait(false);
            var environmentContext = await environment.ReadAsync(
                runtimeActor, owner.ProjectId, owner.RunId, owner.SessionId,
                placement.EnvironmentId, registration.Binding.ProfileId, token,
                result.OperationId).ConfigureAwait(false);
            RequirePlacement(placement, registration, environmentContext);
            var activation = await RuntimeOwnerHttpTransport.SendRuntimeRunStartAsync<
                    RuntimeHostReadinessReceipt>(
                clients.CreateClient("runtime-run-start-environment"),
                options.EnvironmentOwnerAddress,
                "/internal/runtime/v1/activate",
                runtimeActor,
                new RuntimeHostConfigureRequest(
                    1, registration, consumeId, exchangeId, configuration.RootElement.Clone())
                {
                    McpServer = environmentContext.McpServer,
                    CheckpointRecoveryOperationId = result.OperationId
                },
                token,
                CheckpointRecoveryActivationFailureCodes).ConfigureAwait(false);
            cleanupLinks["activation"] = JsonSerializer.SerializeToElement(activation, JsonOptions);
            intent = await recoveryStore.PersistCheckpointRecoveryCleanupEvidenceAsync(
                intent,
                JsonSerializer.Serialize(cleanupLinks, JsonOptions),
                status.ExecutionFence,
                bootstrapId,
                placement.EnvironmentId,
                registration.RuntimeInstanceId,
                CancellationToken.None).ConfigureAwait(false);
            CoordinationEndpoints.ValidateHostReadiness(activation, registration, timeProvider);
            var recovery = activation.Recovery?.ValidateFor(registration);
            if (recovery?.RecoveryMode is not (RuntimeHostRecoveryMode.NativeCache or
                RuntimeHostRecoveryMode.JournalRebuild))
                throw new RuntimeAuthorizationException("checkpoint_recovery_material_unavailable");
            var materialMode = recovery.RecoveryMode == RuntimeHostRecoveryMode.NativeCache
                ? "nativeCache"
                : "journalRebuild";
            await PersistAsync(
                "readiness",
                materialMode: materialMode,
                runtimeInstanceId: registration.RuntimeInstanceId,
                registrationRevision: registration.Revision).ConfigureAwait(false);

            await RequireBoundaryAsync().ConfigureAwait(false);
            var readiness = await environment.ReadReadinessAsync(
                runtimeActor, owner.ProjectId, owner.RunId, owner.SessionId,
                placement.EnvironmentId, registration.Binding.ProfileId, token,
                result.OperationId).ConfigureAwait(false);
            RequireCurrentReadiness(timeProvider, placement, registration, activation, readiness);
            cleanupLinks["readiness"] = JsonSerializer.SerializeToElement(readiness, JsonOptions);
            intent = await recoveryStore.PersistCheckpointRecoveryCleanupEvidenceAsync(
                intent,
                JsonSerializer.Serialize(cleanupLinks, JsonOptions),
                status.ExecutionFence,
                bootstrapId,
                placement.EnvironmentId,
                registration.RuntimeInstanceId,
                CancellationToken.None).ConfigureAwait(false);
            if (childManifest is not null)
            {
                foreach (var member in memberContinuations)
                {
                    await RequireBoundaryAsync().ConfigureAwait(false);
                    var readyMember = await ContinueCheckpointRecoveryMemberAsync(
                        runtimeActor, result, status, member, recoveryStore,
                        RequireBoundaryAsync, token).ConfigureAwait(false);
                    if (readyMember is null)
                    {
                        await PersistAsync(
                            "member-readiness", "checkpoint_recovery_member_readiness_pending")
                            .ConfigureAwait(false);
                        return result;
                    }
                }
            }
            var completedUnjoinedMaterials =
                ImmutableDictionary.CreateBuilder<string, ChildRecoveryCompletedUnjoinedMaterial>(
                    StringComparer.Ordinal);
            if (childManifest is not null &&
                childManifest.Members.Any(member => member.Disposition == "completedUnjoined"))
            {
                var materialReader = events
                    ?? throw new RuntimeAuthorizationException("events_session_material_unavailable");
                foreach (var member in childManifest.Members.Where(
                             candidate => candidate.Disposition == "completedUnjoined"))
                {
                    await RequireBoundaryAsync().ConfigureAwait(false);
                    var childIdentity = new SessionIdentity(
                        result.Session.ProjectId, result.Session.RunId, member.Session.SessionId);
                    var user = await materialReader.ReadSessionMaterialAsync(
                        context, childIdentity, member.MessageId, SessionMaterialKind.TurnContent,
                        runtimeActor, token).ConfigureAwait(false);
                    var assistant = await materialReader.ReadSessionMaterialAsync(
                        context, childIdentity,
                        RuntimeRootTurnCompletionReceipt.AssistantEventId(member.MessageId),
                        SessionMaterialKind.TurnContent, runtimeActor, token).ConfigureAwait(false);
                    var prompt = ReadCompletedTurnText(
                        user, childIdentity, member.MessageId, member.PromptHash);
                    var answer = ReadCompletedTurnText(
                        assistant, childIdentity,
                        RuntimeRootTurnCompletionReceipt.AssistantEventId(member.MessageId), null);
                    completedUnjoinedMaterials.Add(member.AssociationId,
                        new(prompt, answer, user.Material, assistant.Material));
                }
            }
            await requireCurrentSelection(token).ConfigureAwait(false);
            await requireCurrentGate(token).ConfigureAwait(false);
            var readyStatus = status with
            {
                CurrentStep = "ready",
                ReasonCode = null,
                RecoveryMaterialMode = materialMode,
                Phase = "ready",
                DispatchHeld = childManifest is null ? status.DispatchHeld : false,
                RuntimeInstanceId = registration.RuntimeInstanceId,
                RegistrationRevision = registration.Revision
            };
            var readyLinks = ReadReceiptLinks(intent);
            readyLinks["placement"] = JsonSerializer.SerializeToElement(placement, JsonOptions);
            readyLinks["registration"] = JsonSerializer.SerializeToElement(registration, JsonOptions);
            readyLinks["bootstrap"] = JsonSerializer.SerializeToElement(BootstrapReceipt(bootstrapReceipt), JsonOptions);
            readyLinks["activation"] = JsonSerializer.SerializeToElement(activation, JsonOptions);
            readyLinks["readiness"] = JsonSerializer.SerializeToElement(readiness, JsonOptions);
            var readyIntent = intent with
            {
                ExecutionFence = owner.ExecutionFence,
                BootstrapOperationId = bootstrapId,
                ConsumeOperationId = consumeId,
                ExchangeOperationId = exchangeId,
                State = "ready",
                CurrentStep = "ready",
                FailureCode = null,
                EnvironmentId = placement.EnvironmentId,
                RuntimeInstanceId = registration.RuntimeInstanceId,
                ReceiptLinks = JsonSerializer.Serialize(readyLinks, JsonOptions)
            };
            if (childManifest is not null)
                readyIntent = await recoveryStore.PersistCheckpointRecoveryCleanupEvidenceAsync(
                    readyIntent,
                    readyIntent.ReceiptLinks,
                    status.ExecutionFence,
                    bootstrapId,
                    placement.EnvironmentId,
                    registration.RuntimeInstanceId,
                    CancellationToken.None,
                    currentStep: "ready",
                    ready: true,
                    consumeOperationId: consumeId,
                    exchangeOperationId: exchangeId).ConfigureAwait(false);
            result = await recoveryStore.UpdateCheckpointRecoveryProgressAsync(
                actor, result.Session, result.OperationId, readyStatus, cancellationToken, readyIntent,
                checkpointStore, usageSources, completedUnjoinedMaterials.ToImmutable(), childManifest,
                selection)
                .ConfigureAwait(false);
            status = result.CheckpointRecovery!;
            return result;
        }

        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return await PersistFailureAsync("checkpoint_recovery_continuation_timeout").ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is RuntimeAuthorizationException or CoordinationException or HttpRequestException or JsonException)
        {
            var reason = FailureCode(exception);
            var failure = ClassifyCheckpointRecoveryFailure(
                HasCheckpointRecoveryBootstrapEvidence(result.OperationId, ReadReceiptLinks(intent)),
                reason,
                exception is HttpRequestException or OperationCanceledException ||
                    reason is "runtime_activation_owner_unavailable" or
                        "runtime_activation_owner_timeout" or
                        "runtime_owner_unavailable" or
                        "runtime_owner_timeout" ||
                    reason.Contains("timeout", StringComparison.Ordinal));
            return await PersistFailureAsync(failure.ReasonCode, failure.Phase).ConfigureAwait(false);
        }

        async Task<RunExecutionTransitionResult> PersistFailureAsync(string reason, string phase = "pending")
        {
            await PersistAsync(status.CurrentStep, reason, phase: phase).ConfigureAwait(false);
            return result;
        }
    }

    private static string ReadCompletedTurnText(
        SessionMaterialReadResult material,
        SessionIdentity identity,
        Guid eventId,
        string? expectedHash)
    {
        var binding = material.Material.Reference.Material;
        if (material.Material.Identity != identity || material.Material.EventId != eventId ||
            binding is not { Kind: SessionMaterialKind.TurnContent } ||
            material.Bytes.Length is <= 0 or > SessionMaterialValidation.MaximumBytes ||
            RuntimeContractValidation.Hash(material.Bytes) != binding.Sha256)
            throw new RuntimeAuthorizationException("checkpoint_recovery_result_material_invalid");
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(material.Bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new RuntimeAuthorizationException(
                "checkpoint_recovery_result_material_invalid", null, exception);
        }
        if (text.Length > AddressedMessageValidation.MaximumTextLength ||
            text.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')) ||
            expectedHash is not null &&
                RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(text)) != expectedHash)
            throw new RuntimeAuthorizationException("checkpoint_recovery_result_material_invalid");
        return text;
    }

    internal static void RequireReplacementPlacement(
        RuntimeRunStartPlacementReceipt previous,
        RuntimeRunStartPlacementReceipt current,
        Guid bootstrapOperationId,
        RuntimeOwnerContext owner)
    {
        current.Validate();
        if (current.EnvironmentId != RuntimeRunStartKeys.EnvironmentId(owner) ||
            current.WorkspaceVolumeId != previous.WorkspaceVolumeId ||
            current.WorkspaceResourceGeneration != previous.WorkspaceResourceGeneration ||
            current.WorkspaceDataGeneration != previous.WorkspaceDataGeneration ||
            current.SandboxIdempotencyKey != RuntimeRunStartKeys.SandboxIdempotencyKey(owner, bootstrapOperationId) ||
            current.SandboxOperationId == previous.SandboxOperationId ||
            current.SandboxResource == previous.SandboxResource ||
            string.IsNullOrWhiteSpace(previous.PodUid) ||
            string.IsNullOrWhiteSpace(current.PodUid) ||
            string.Equals(previous.PodUid, current.PodUid, StringComparison.Ordinal) ||
            current.CurrentFencingGeneration == previous.CurrentFencingGeneration ||
            current.ProviderFencingGeneration == previous.ProviderFencingGeneration ||
            current.LifecycleGeneration != previous.LifecycleGeneration ||
            current.NetworkPolicyGeneration != previous.NetworkPolicyGeneration)
            throw new RuntimeAuthorizationException("runtime_replacement_placement_invalid");
    }

    internal static bool MatchesReplacementReconstructionInterval(
        RuntimeHostRecoveryReceipt recovery,
        string expectedCursorHash) =>
        recovery.ReplacementReconstructionInterval is
            { EventCount: 0, IsComplete: true } interval &&
        interval.StartCursorSha256 == expectedCursorHash &&
        interval.EndCursorSha256 == expectedCursorHash;

    internal static RuntimeReplacementSummary CreateReplacementSummary(
        RuntimeReplacementOperation operation,
        RuntimeRegistration previousRegistration,
        RuntimeRunStartPlacementReceipt previousPlacement)
    {
        var links = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(operation.ReceiptLinks, JsonOptions)
            ?? new(StringComparer.Ordinal);
        RuntimeRegistration? currentRegistration = null;
        RuntimeRunStartPlacementReceipt? currentPlacement = null;
        RuntimeHostReadinessReceipt? currentActivation = null;
        if (links.TryGetValue("currentRegistration", out var registration))
            currentRegistration = registration.Deserialize<RuntimeRegistration>(JsonOptions);
        if (links.TryGetValue("currentPlacement", out var placement))
            currentPlacement = placement.Deserialize<RuntimeRunStartPlacementReceipt>(JsonOptions);
        if (links.TryGetValue("currentActivation", out var activation))
            currentActivation = activation.Deserialize<RuntimeHostReadinessReceipt>(JsonOptions);
        var recovery = currentActivation?.Recovery;
        return new RuntimeReplacementSummary(
            1,
            operation.OperationId,
            operation.State,
            operation.CurrentStep,
            operation.FailureCode,
            previousRegistration.RuntimeInstanceId,
            previousRegistration.Revision,
            currentRegistration?.RuntimeInstanceId,
            currentRegistration?.Revision,
            operation.ExecutionFence,
            operation.AcceptedSelectionHash,
            previousPlacement.PodUid,
            currentPlacement?.PodUid,
            currentPlacement?.WorkspaceVolumeId ?? previousPlacement.WorkspaceVolumeId,
            currentPlacement?.WorkspaceResourceGeneration ?? previousPlacement.WorkspaceResourceGeneration,
            currentPlacement?.WorkspaceDataGeneration ?? previousPlacement.WorkspaceDataGeneration,
            recovery?.RecoveryMode,
            recovery?.NativeCacheAcknowledgment is { } cache
                ? ProjectMaterial(cache)
                : null,
            recovery?.ReplacementReconstructionInterval,
            recovery?.ReplacementCallSequence).Validate();
    }

    private static RuntimeMaterialAcknowledgmentEvidence ProjectMaterial(
        SessionMaterialAcknowledgment acknowledgment)
    {
        var material = acknowledgment.Reference.Material
            ?? throw new RuntimeAuthorizationException("runtime_replacement_summary_invalid");
        return new RuntimeMaterialAcknowledgmentEvidence(
            acknowledgment.EventId,
            acknowledgment.Position,
            material.Kind,
            material.Sha256,
            RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(
                acknowledgment.Reference, JsonOptions)));
    }

    private static Guid ReadGuid(Dictionary<string, JsonElement> links, string key) =>
        links.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String &&
        value.TryGetGuid(out var result) && result != Guid.Empty
            ? result
            : throw new RuntimeAuthorizationException("runtime_replacement_receipt_invalid");

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
        // Revoke before the attempt is replaced, so the retired registration never stays active unowned.
        var revoked = await registrations.RevokeAfterPlacementReleaseAsync(registration, cancellationToken)
            .ConfigureAwait(false);
        if (revoked.State != RuntimeRegistrationState.Revoked ||
            revoked.RuntimeInstanceId != registration.RuntimeInstanceId)
            throw new RuntimeAuthorizationException("runtime_start_retirement_registration_invalid");
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
            RequireCurrentReadiness(timeProvider, placement, registration, hostReadiness, readiness);
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
        _ = await (owner.AgentId == RuntimeActionContract.CoordinatorRootStepId
            ? store.EnsureIntentAsync(owner, cancellationToken)
            : store.EnsureChildIntentAsync(owner, cancellationToken)).ConfigureAwait(false);
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
        RequireRegistration(owner, placement, registration, requireEmptyWorkspace: true);
    }

    internal static void RequireReplacementRegistration(
        RuntimeOwnerContext owner,
        RuntimeRunStartPlacementReceipt placement,
        RuntimeRegistration registration)
    {
        RequireRegistration(owner, placement, registration, requireEmptyWorkspace: false);
    }

    internal static RuntimeOwnerContext CreateOwnerContext(RuntimeBinding binding) =>
        new(
            1,
            binding.ActorIssuer,
            binding.ActorId,
            binding.TenantId,
            binding.ProjectId,
            binding.RunId,
            binding.SessionId,
            binding.AgentId,
            binding.ModelSelectionReference!,
            binding.TurnId,
            binding.ProjectRevision,
            binding.ProjectConfigurationRevision,
            binding.PlatformRuntimeRevision,
            binding.ContextRevision,
            binding.AcceptedSelectionHash,
            binding.ExecutionFence,
            0,
            1,
            1)
        {
            WorkflowStepId = binding.WorkflowStepId,
            ModelCredentialReference = binding.ModelCredentialReference,
            ModelSourceMode = binding.ModelSourceMode,
            ModelBindingPin = binding.ModelBindingPin,
            ModelConnectionId = binding.ModelConnectionId,
            ModelConnectionScope = binding.ModelConnectionScope,
            ReasoningEffort = binding.ReasoningEffort,
            MaxModelTurns = binding.MaxModelTurns,
            MaxToolCalls = binding.MaxToolCalls,
            MaxPromptTokens = binding.MaxPromptTokens,
            MaxRevisionAttempts = binding.MaxRevisionAttempts,
            CopilotSoftCreditLimit = binding.CopilotSoftCreditLimit,
            CopilotHardCreditLimit = binding.CopilotHardCreditLimit
        };

    internal static RuntimeOwnerContext CreateCheckpointRecoveryOwnerContext(
        RuntimeBinding binding, long executionFence, long logicalTurnOrdinal = 0) =>
        CreateOwnerContext(binding) with
        {
            TurnId = logicalTurnOrdinal == 0
                ? CoordinationOwnerStore.CreateCoordinatorRootRuntimeTurnId(
                    new(binding.ProjectId, binding.RunId, binding.SessionId), executionFence)
                : CoordinationOwnerStore.CreateRuntimeTurnId(
                    new(binding.ProjectId, binding.RunId, binding.SessionId), executionFence, logicalTurnOrdinal),
            LogicalTurnOrdinal = logicalTurnOrdinal,
            ExecutionFence = executionFence
        };

    internal static bool HasPreviousCheckpointRecoveryEvidence(
        Guid operationId,
        IReadOnlyDictionary<string, JsonElement> links) =>
        links.TryGetValue("checkpointRecoveryEvidenceOperationId", out var evidenceOperationElement) &&
        evidenceOperationElement.GetGuid() == operationId &&
        links.ContainsKey("checkpointRecoveryPreviousRegistration") &&
        links.ContainsKey("checkpointRecoveryPreviousPlacement");

    internal static bool HasCheckpointRecoveryBootstrapEvidence(
        Guid operationId,
        IReadOnlyDictionary<string, JsonElement> links) =>
        links.TryGetValue("checkpointRecoveryBootstrapEvidenceOperationId", out var evidenceOperationElement) &&
        evidenceOperationElement.GetGuid() == operationId &&
        links.ContainsKey("bootstrap");

    internal static void SetCheckpointRecoveryBootstrapEvidence(
        IDictionary<string, JsonElement> links,
        Guid operationId,
        JsonElement receipt)
    {
        links["bootstrap"] = receipt;
        links["checkpointRecoveryBootstrapEvidenceOperationId"] =
            JsonSerializer.SerializeToElement(operationId, JsonOptions);
    }

    internal static void SetPreviousCheckpointRecoveryEvidence(
        IDictionary<string, JsonElement> links,
        Guid operationId,
        JsonElement registration,
        JsonElement placement,
        JsonElement bootstrapOperationId)
    {
        links["checkpointRecoveryPreviousRegistration"] = registration;
        links["checkpointRecoveryPreviousPlacement"] = placement;
        links["checkpointRecoveryPreviousBootstrapOperationId"] = bootstrapOperationId;
        links["checkpointRecoveryEvidenceOperationId"] =
            JsonSerializer.SerializeToElement(operationId, JsonOptions);
    }

    internal static (string Phase, string ReasonCode) ClassifyCheckpointRecoveryFailure(
        bool hasBootstrapReceipt,
        string reason,
        bool transportOrTimeout)
    {
        var bootstrapSpent = hasBootstrapReceipt &&
            reason is "runtime_delivery_unavailable" or
                "runtime_receiver_unavailable" or
                "runtime_source_credential_unavailable" or
                "runtime_configuration_conflict" or
                "runtime_operation_conflict";
        if (bootstrapSpent)
            return ("blocked", "checkpoint_recovery_host_bootstrap_spent");
        return (transportOrTimeout || reason.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            ? "pending"
            : "blocked", reason);
    }

    private static void RequireRegistration(
        RuntimeOwnerContext owner,
        RuntimeRunStartPlacementReceipt placement,
        RuntimeRegistration registration,
        bool requireEmptyWorkspace)
    {
        RuntimeContractValidation.Validate(registration);
        var binding = registration.Binding;
        if (requireEmptyWorkspace && placement.WorkspaceDataGeneration != 0)
            throw new RuntimeAuthorizationException("runtime_start_workspace_not_empty");
        if (registration.State != RuntimeRegistrationState.Active ||
            binding.ActorIssuer != owner.ActorIssuer || binding.ActorId != owner.ActorId ||
            binding.TenantId != owner.TenantId || binding.ProjectId != owner.ProjectId ||
            binding.RunId != owner.RunId || binding.SessionId != owner.SessionId ||
            binding.AgentId != owner.AgentId || binding.TurnId != owner.TurnId ||
            binding.WorkflowStepId != owner.WorkflowStepId ||
            binding.ModelSelectionReference != owner.ModelSelectionReference ||
            binding.ModelCredentialReference != owner.ModelCredentialReference ||
            binding.ModelSourceMode != owner.ModelSourceMode ||
            binding.ModelBindingPin != owner.ModelBindingPin ||
            binding.ModelConnectionId != owner.ModelConnectionId ||
            binding.ModelConnectionScope != owner.ModelConnectionScope ||
            binding.ReasoningEffort != owner.ReasoningEffort ||
            binding.MaxModelTurns != owner.MaxModelTurns || binding.MaxToolCalls != owner.MaxToolCalls ||
            binding.MaxPromptTokens != owner.MaxPromptTokens ||
            binding.MaxRevisionAttempts != owner.MaxRevisionAttempts ||
            binding.CopilotSoftCreditLimit != owner.CopilotSoftCreditLimit ||
            binding.CopilotHardCreditLimit != owner.CopilotHardCreditLimit ||
            !IsResolvedProfileId(placement.SourceProfileId, binding.ProfileId) ||
            binding.AcceptedSelectionHash != owner.AcceptedSelectionHash ||
            binding.ExecutionFence != owner.ExecutionFence ||
            binding.EnvironmentId != placement.EnvironmentId ||
            binding.PlacementGeneration != placement.SandboxResource.Generation ||
            binding.EnvironmentCurrentFencingGeneration != placement.CurrentFencingGeneration ||
            binding.EnvironmentProviderFencingGeneration != placement.ProviderFencingGeneration)
            throw new RuntimeAuthorizationException("runtime_start_registration_stale");
    }

    internal static bool IsResolvedProfileId(string sourceProfileId, string resolvedProfileId)
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

    internal static void RequirePlacement(
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

    internal static void RequireCurrentReadiness(
        TimeProvider timeProvider,
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
    RuntimeRegistrationOwner registrations,
    WorkflowGenerationJobStore workflowGenerationJobs,
    RuntimeRunStartOptions options,
    IHttpClientFactory clients)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task ContinueAsync(RuntimeRunStartCleanupWork work, CancellationToken cancellationToken)
    {
        if (!work.Actor.Bearer.IsUsable())
            return;
        var intents = await store.FindCleanupIntentsAsync(
            work.TenantId, work.ProjectId, work.RunId, cancellationToken).ConfigureAwait(false);
        if (intents.Count == 0)
            return;
        if (await workflowGenerationJobs.HasUnresolvedNativeWorkAsync(
                work.ProjectId, work.RunId, cancellationToken).ConfigureAwait(false))
        {
            var proofIntent = intents.FirstOrDefault(intent => !IsChild(intent)) ?? intents[0];
            await workflowGenerationJobs.ReconcileTerminalQuiescenceAsync(
                work.ProjectId, work.RunId, async token =>
                {
                    var selection = await projects.ReadSelectionForCancelAsync(
                        work.Actor, work.Caller, work.ProjectId, work.RunId, token).ConfigureAwait(false);
                    CoordinationOwnerStore.RequireProjectRunCancellationAuthority(work.Caller, selection);
                    if (selection.Authorization.TenantId != work.TenantId || !work.Actor.Bearer.IsUsable())
                        throw new CoordinationException("run_cancel_permission_denied", StatusCodes.Status403Forbidden);
                    var contractVersion = await store.IsTerminalCleanupGuardInstalledAsync(token)
                        .ConfigureAwait(false) ? 2 : 1;
                    _ = await store.ReadCleanupProjectionAsync(
                        work.ProjectId, work.RunId, proofIntent.RootSessionId, selection, token,
                        contractVersion, requireQuiescence: false).ConfigureAwait(false);
                }, cancellationToken).ConfigureAwait(false);
            if (await workflowGenerationJobs.HasUnresolvedNativeWorkAsync(
                    work.ProjectId, work.RunId, cancellationToken).ConfigureAwait(false))
                return;
        }
        Exception? firstFailure = null;
        foreach (var intent in intents.OrderByDescending(IsChild))
        {
            if (intent.State == "cleanup_complete")
                continue;
            try
            {
                await ContinueIntentAsync(work, intent, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                firstFailure ??= failure;
            }
        }
        if (firstFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(firstFailure).Throw();
    }

    private static bool IsChild(RuntimeRunStartIntent intent)
    {
        using var links = JsonDocument.Parse(intent.ReceiptLinks);
        return links.RootElement.TryGetProperty("childStart", out _);
    }

    private async Task ContinueIntentAsync(
        RuntimeRunStartCleanupWork work,
        RuntimeRunStartIntent intent,
        CancellationToken cancellationToken)
    {
        if (!work.Actor.Bearer.IsUsable())
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
        var contractVersion = await store.IsTerminalCleanupGuardInstalledAsync(cancellationToken)
            .ConfigureAwait(false) ? 2 : 1;
        var projection = await store.ReadCleanupProjectionAsync(
            work.ProjectId, work.RunId, intent.RootSessionId, selection, cancellationToken, contractVersion)
            .ConfigureAwait(false);
        if (projection.CleanupDisposition == "cleanup_complete" || !work.Actor.Bearer.IsUsable())
            return;

        async Task RequireCurrentAsync(CancellationToken token)
        {
            var refreshed = await projects.ReadSelectionForCancelAsync(
                work.Actor, work.Caller, work.ProjectId, work.RunId, token).ConfigureAwait(false);
            CoordinationOwnerStore.RequireProjectRunCancellationAuthority(work.Caller, refreshed);
            if (refreshed.Authorization.TenantId != work.TenantId || !work.Actor.Bearer.IsUsable())
                throw new CoordinationException("run_cancel_permission_denied", StatusCodes.Status403Forbidden);
            var latest = await store.ReadCleanupProjectionAsync(
                work.ProjectId, work.RunId, intent.RootSessionId, refreshed, token, contractVersion).ConfigureAwait(false);
            projection.ValidateFor(latest);
        }

        var receipt = await RuntimeOwnerHttpTransport.SendAsync<RuntimeRunStartCleanupReceipt>(
            clients.CreateClient("runtime-run-start-environment"),
            options.EnvironmentOwnerAddress,
            contractVersion == 2 ? "/internal/runtime/run-start/terminal-cleanup" :
                "/internal/runtime/run-start/placement",
            work.Actor,
            new RuntimeRunStartPlacementRequest(contractVersion, null, projection),
            cancellationToken).ConfigureAwait(false);
        receipt.Validate();
        if (receipt.ContractVersion != projection.ContractVersion || receipt.IntentId != intent.IntentId)
            throw new RuntimeAuthorizationException("runtime_start_cleanup_receipt_invalid");
        if (projection.ReceiptLinks.TryGetProperty("placement", out var savedPlacement))
        {
            var placement = savedPlacement.Deserialize<RuntimeRunStartPlacementReceipt>(JsonOptions)
                ?? throw new RuntimeAuthorizationException("runtime_start_cleanup_receipt_invalid");
            if (receipt.LeaseState == "absent" ||
                receipt.SandboxOperationId != placement.SandboxOperationId ||
                receipt.ResourceGeneration != placement.SandboxResource.Generation ||
                receipt.ProviderFencingGeneration != placement.ProviderFencingGeneration ||
                receipt.SandboxIdempotencyKey != placement.SandboxIdempotencyKey ||
                receipt.ContractVersion == 2 && receipt.CleanupDisposition == "cleanup_complete" &&
                    receipt.SandboxResource != placement.SandboxResource)
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
        await RequireCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (receipt.CleanupDisposition == "cleanup_complete")
        {
            await RevokeOwnedRegistrationsAsync(
                intent, projection, receipt, RequireCurrentAsync, cancellationToken).ConfigureAwait(false);
            await RequireCurrentAsync(cancellationToken).ConfigureAwait(false);
            _ = await store.MarkCleanupCompleteAsync(
                intent, intent.RowVersion, receipt, projection, cancellationToken).ConfigureAwait(false);
        }
        else
            _ = await store.RecordCleanupPendingAsync(
                intent, intent.RowVersion, receipt, projection, cancellationToken).ConfigureAwait(false);
    }

    private async Task RevokeOwnedRegistrationsAsync(
        RuntimeRunStartIntent intent,
        RuntimeRunStartCleanupProjection projection,
        RuntimeRunStartCleanupReceipt receipt,
        Func<CancellationToken, Task> requireCurrent,
        CancellationToken cancellationToken)
    {
        var owned = new Dictionary<Guid, RuntimeRegistration>();
        RuntimeRunStartPlacementReceipt? savedPlacement = null;
        if (projection.ReceiptLinks.TryGetProperty("placement", out var placementElement))
            savedPlacement = placementElement.Deserialize<RuntimeRunStartPlacementReceipt>(JsonOptions)
                ?? throw new RuntimeAuthorizationException("runtime_start_cleanup_registration_invalid");
        if (projection.ReceiptLinks.TryGetProperty("registration", out var currentRegistrationElement))
        {
            var current = currentRegistrationElement.Deserialize<RuntimeRegistration>(JsonOptions)
                ?? throw new RuntimeAuthorizationException("runtime_start_cleanup_registration_invalid");
            owned[current.RuntimeInstanceId] = current;
        }
        var resource = savedPlacement?.SandboxResource ?? receipt.SandboxResource;
        if (owned.Count != 0 && resource is null)
            throw new RuntimeAuthorizationException("runtime_start_cleanup_registration_invalid");
        if (savedPlacement is not null)
            savedPlacement.Validate();
        var candidates = await registrations.ReadCleanupCandidatesAsync(
            intent.ProjectId, intent.RunId, intent.RootSessionId, cancellationToken).ConfigureAwait(false);
        var retired = await ReadActiveRetiredRegistrationAsync(intent, projection, cancellationToken)
            .ConfigureAwait(false);
        if (resource is null && candidates.Any(candidate => candidate.State == RuntimeRegistrationState.Active &&
                candidate.RuntimeInstanceId != retired?.RuntimeInstanceId))
            throw new RuntimeAuthorizationException("runtime_start_cleanup_registration_invalid");
        if (resource is not null)
        {
            var matches = new List<RuntimeRegistration>();
            var environmentId = RuntimeRunStartKeys.EnvironmentId(
                intent.TenantId, intent.ProjectId, intent.RunId,
                intent.RootSessionId, intent.AcceptedSelectionHash);
            foreach (var candidate in candidates)
            {
                var binding = candidate.Binding;
                if (binding.EnvironmentId != environmentId ||
                    binding.PlacementUid != resource.ResourceId ||
                    binding.PlacementGeneration != resource.Generation)
                    continue;
                if (binding.TenantId != intent.TenantId || binding.ProjectId != intent.ProjectId ||
                    binding.RunId != intent.RunId || binding.SessionId != intent.RootSessionId ||
                    binding.AcceptedSelectionHash != intent.AcceptedSelectionHash ||
                    binding.ExecutionFence != intent.ExecutionFence ||
                    binding.PlacementProviderId != resource.ProviderId ||
                    binding.EnvironmentProviderFencingGeneration != receipt.ProviderFencingGeneration ||
                    binding.EnvironmentCurrentFencingGeneration != receipt.ProviderFencingGeneration ||
                    savedPlacement is not null &&
                        (binding.EnvironmentLifecycleGeneration != savedPlacement.LifecycleGeneration ||
                         binding.EnvironmentLeaseRevision != savedPlacement.LeaseRevision ||
                         binding.EnvironmentCurrentFencingGeneration != savedPlacement.CurrentFencingGeneration ||
                         binding.EnvironmentProviderFencingGeneration != savedPlacement.ProviderFencingGeneration))
                    throw new RuntimeAuthorizationException("runtime_start_cleanup_registration_invalid");
                if (projection.ReceiptLinks.TryGetProperty("childStart", out _))
                {
                    var child = CoordinationRuntimeRunStartStore.ReadChildBinding(intent);
                    if (binding.ActorIssuer != child.ActorIssuer || binding.ActorId != child.ActorId ||
                        binding.AgentId != child.AgentId || binding.WorkflowStepId != child.WorkflowStepId ||
                        binding.ModelSelectionReference != child.ModelSelectionReference ||
                        binding.ContextRevision != child.ContextRevision)
                        throw new RuntimeAuthorizationException("runtime_start_cleanup_registration_invalid");
                }
                else if (binding.AgentId != RuntimeActionContract.CoordinatorRootStepId ||
                         binding.WorkflowStepId is not null)
                    throw new RuntimeAuthorizationException("runtime_start_cleanup_registration_invalid");
                await store.RequireRegistrationCleanupOwnerAsync(intent, candidate, cancellationToken)
                    .ConfigureAwait(false);
                matches.Add(candidate);
            }
            if (matches.Count > 1)
                throw new RuntimeAuthorizationException("runtime_start_cleanup_registration_ambiguous");
            if (matches.Count == 1 && matches[0].State == RuntimeRegistrationState.Active)
                owned[matches[0].RuntimeInstanceId] = matches[0];
            if (owned.Count != 0 && (matches.Count != 1 || !owned.ContainsKey(matches[0].RuntimeInstanceId)))
                throw new RuntimeAuthorizationException("runtime_start_cleanup_registration_invalid");
        }
        if (projection.ReceiptLinks.TryGetProperty("replacementOperationId", out var operationElement) &&
            operationElement.TryGetGuid(out var operationId) && operationId != Guid.Empty)
        {
            var operation = await store.ReadReplacementOperationAsync(
                intent, operationId, cancellationToken).ConfigureAwait(false)
                ?? throw new RuntimeAuthorizationException("runtime_replacement_cleanup_receipt_missing");
            using var links = JsonDocument.Parse(operation.ReceiptLinks);
            if (!links.RootElement.TryGetProperty("previousRegistration", out var previousValue) ||
                previousValue.Deserialize<RuntimeRegistration>(JsonOptions) is not { } previous ||
                !links.RootElement.TryGetProperty("previousPlacement", out var previousPlacementValue) ||
                previousPlacementValue.Deserialize<RuntimeRunStartPlacementReceipt>(JsonOptions) is not { } previousPlacement ||
                operation.ExpectedRuntimeInstanceId != previous.RuntimeInstanceId ||
                operation.ExpectedRegistrationRevision != previous.Revision ||
                previous.Binding.TenantId != intent.TenantId ||
                previous.Binding.ProjectId != intent.ProjectId ||
                previous.Binding.RunId != intent.RunId ||
                previous.Binding.SessionId != intent.RootSessionId ||
                previous.Binding.ExecutionFence != intent.ExecutionFence ||
                previous.Binding.AcceptedSelectionHash != intent.AcceptedSelectionHash)
                throw new RuntimeAuthorizationException("runtime_replacement_cleanup_registration_invalid");
            previousPlacement.Validate();
            var replacementOwner = RuntimeRunStartContinuation.CreateOwnerContext(previous.Binding);
            RuntimeRunStartContinuation.RequireReplacementRegistration(
                replacementOwner, previousPlacement, previous);
            owned[previous.RuntimeInstanceId] = previous;
            if (links.RootElement.TryGetProperty("currentRegistration", out var currentValue) &&
                currentValue.Deserialize<RuntimeRegistration>(JsonOptions) is { } replacementRegistration)
            {
                if (!links.RootElement.TryGetProperty("currentPlacement", out var currentPlacementValue) ||
                    currentPlacementValue.Deserialize<RuntimeRunStartPlacementReceipt>(JsonOptions) is not { } currentPlacement ||
                    RuntimeRegistrationStore.CreateReplacementId(previous.RuntimeInstanceId, operationId) !=
                        replacementRegistration.RuntimeInstanceId)
                    throw new RuntimeAuthorizationException("runtime_replacement_cleanup_registration_invalid");
                RuntimeRunStartContinuation.RequireReplacementPlacement(
                    previousPlacement,
                    currentPlacement,
                    RuntimeRunStartKeys.ReplacementOperationId(operationId, "bootstrap"),
                    replacementOwner);
                RuntimeRunStartContinuation.RequireReplacementRegistration(
                    replacementOwner, currentPlacement, replacementRegistration);
                owned[replacementRegistration.RuntimeInstanceId] = replacementRegistration;
            }
            else if (links.RootElement.TryGetProperty("currentPlacement", out var recoveredPlacementValue) &&
                recoveredPlacementValue.Deserialize<RuntimeRunStartPlacementReceipt>(JsonOptions) is { } recoveredPlacement)
            {
                RuntimeRunStartContinuation.RequireReplacementPlacement(
                    previousPlacement,
                    recoveredPlacement,
                    RuntimeRunStartKeys.ReplacementOperationId(operationId, "bootstrap"),
                    replacementOwner);
                var recovered = await registrations.ReadAsync(
                    RuntimeRegistrationStore.CreateReplacementId(previous.RuntimeInstanceId, operationId),
                    cancellationToken).ConfigureAwait(false);
                if (recovered is not null)
                {
                    RuntimeRunStartContinuation.RequireReplacementRegistration(
                        replacementOwner, recoveredPlacement, recovered);
                    owned[recovered.RuntimeInstanceId] = recovered;
                }
            }
        }

        if (retired is not null)
            owned[retired.RuntimeInstanceId] = retired;
        if (candidates.Any(candidate => candidate.State == RuntimeRegistrationState.Active &&
                !owned.ContainsKey(candidate.RuntimeInstanceId)))
            throw new RuntimeAuthorizationException("runtime_start_cleanup_registration_ambiguous");
        foreach (var registration in owned.Values)
        {
            if (registration.State != RuntimeRegistrationState.Active)
                throw new RuntimeAuthorizationException("runtime_replacement_cleanup_registration_invalid");
            await requireCurrent(cancellationToken).ConfigureAwait(false);
            var revoked = await registrations.RevokeAfterPlacementReleaseAsync(
                registration, cancellationToken).ConfigureAwait(false);
            if (revoked.State != RuntimeRegistrationState.Revoked ||
                revoked.RuntimeInstanceId != registration.RuntimeInstanceId)
                throw new RuntimeAuthorizationException("runtime_replacement_cleanup_registration_invalid");
        }
    }

    /// <summary>
    /// Returns the registration of a released bootstrap attempt that older code replaced without revoking.
    /// </summary>
    private async Task<RuntimeRegistration?> ReadActiveRetiredRegistrationAsync(
        RuntimeRunStartIntent intent,
        RuntimeRunStartCleanupProjection projection,
        CancellationToken cancellationToken)
    {
        if (!projection.ReceiptLinks.TryGetProperty("retiredBootstrap", out var retiredElement))
            return null;
        if (!retiredElement.TryGetProperty("registration", out var registrationElement) ||
            registrationElement.Deserialize<RuntimeRegistration>(JsonOptions) is not { } saved ||
            !retiredElement.TryGetProperty("retirement", out var retirementElement) ||
            retirementElement.Deserialize<RuntimeRunStartRetirementReceipt>(JsonOptions)?.State != "released")
            throw new RuntimeAuthorizationException("runtime_start_cleanup_registration_invalid");
        var binding = saved.Binding;
        if (binding.TenantId != intent.TenantId || binding.ProjectId != intent.ProjectId ||
            binding.RunId != intent.RunId || binding.SessionId != intent.RootSessionId ||
            binding.AcceptedSelectionHash != intent.AcceptedSelectionHash ||
            binding.ExecutionFence != intent.ExecutionFence)
            throw new RuntimeAuthorizationException("runtime_start_cleanup_registration_invalid");
        var current = await registrations.ReadAsync(saved.RuntimeInstanceId, cancellationToken).ConfigureAwait(false);
        return current is { State: RuntimeRegistrationState.Active } && current.Revision == saved.Revision
            ? current
            : null;
    }
}
