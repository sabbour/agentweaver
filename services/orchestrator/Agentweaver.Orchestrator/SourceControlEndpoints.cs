using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Agentweaver.Providers;
using Agentweaver.SourceControl;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Agentweaver.Orchestrator;

internal static class SourceControlEndpoints
{
    private static readonly TimeSpan MergeSettlementTimeout = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions ProviderSelectionJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    public static IEndpointRouteBuilder MapSourceControlEndpoints(this IEndpointRouteBuilder app)
    {
        var sourceControl = app.MapGroup(
                "/api/projects/{projectId}/runs/{runId}/source-control/sessions/{sessionId}")
            .RequireAuthorization();
        sourceControl.MapPost("/pin", PinRepositoryAsync)
            .WithMetadata(new RequestSizeLimitAttribute(4096));
        sourceControl.MapPost("/issues", CreateIssueAsync);
        sourceControl.MapPost("/pull-requests", CreateOrReusePullRequestAsync);
        sourceControl.MapGet(
            "/pull-requests/{pullRequestNumber:long}/reviews", ReadReviewsAsync);
        sourceControl.MapPost("/workspaces", PrepareWorkspaceAsync);
        sourceControl.MapPost("/workspaces/{workspaceId}/diff", ReadWorkspaceDiffAsync);
        sourceControl.MapPost(
            "/workspaces/{workspaceId}/output-captures", CaptureWorkspaceOutputAsync);
        sourceControl.MapGet("/output-captures", ReadOutputCapturePageAsync);
        sourceControl.MapGet("/output-captures/{captureId}", ReadOutputCaptureAsync);
        sourceControl.MapGet("/output-captures/{captureId}/diff", ReadOutputCaptureDiffAsync);
        sourceControl.MapGet("/output-captures/{captureId}/files", ReadOutputCaptureFileAsync);
        sourceControl.MapPost("/merge-intents", PrepareMergeIntentAsync);
        sourceControl.MapGet("/merge-intents/{intentId}", ReadMergeIntentAsync);
        sourceControl.MapPost("/merge-intents/{intentId}/execute", ExecuteMergeIntentAsync);
        sourceControl.MapPost("/webhook-relay", ReceiveRelayedGitHubWebhookAsync)
            .WithMetadata(new RequestSizeLimitAttribute(1024 * 1024));
        app.MapGet(
                "/internal/projects/{projectId}/runs/{runId}/source-control/sessions/{sessionId}/output-captures/{captureId}/proof",
                ReadOutputCaptureProofForEventsAsync)
            .RequireAuthorization();
        app.MapPost("/api/source-control/github/webhook", RejectDirectGitHubWebhookAsync)
            .RequireAuthorization();
        return app;
    }

    private static Task<IResult> ReadOutputCaptureProofForEventsAsync(
        HttpContext context,
        string projectId,
        string runId,
        string sessionId,
        string captureId,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinationOwnerStore coordinationOwner,
        SourceControlOwnerStore sourceControlOwner,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            _ = RequireOwnerActor(context, options, projectId, runId);
            var identity = new SessionIdentity(projectId, runId, sessionId);
            var authorized = await ReadAuthorizedOutputCaptureScopeAsync(
                context, identity, options, projects, coordinationOwner, cancellationToken)
                .ConfigureAwait(false);
            var proof = await sourceControlOwner.ReadOutputCaptureProofForEventsAsync(
                identity,
                captureId,
                SourceControlOwnerStore.HashSelection(authorized.Selection.Selection),
                authorized.Selection.Authorization.TenantId,
                cancellationToken).ConfigureAwait(false);
            await RevalidateOutputCaptureReadAsync(
                context, identity, authorized, options, projects, coordinationOwner, cancellationToken)
                .ConfigureAwait(false);
            return Results.Json(proof, ProviderSelectionJsonOptions);
        }, cancellationToken);

    private static Task<IResult> PinRepositoryAsync(
        string projectId,
        string runId,
        string sessionId,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] SourceControlPinRequest? pinRequest,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinatorDecisionOwnerStore decisions,
        SourceControlOwnerStore sourceControlOwner,
        ProviderCatalog catalog,
        ProviderResolver resolver,
        ISourceControlAdapter adapter,
        SourceControlSecretRedemptionClient redemption,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var actor = RequireOwnerActor(context, options, projectId, runId);
            var identity = new SessionIdentity(projectId, runId, sessionId);
            var selection = await projects.ReadAcceptedSelectionWithAuthorityAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            var current = await decisions.ReadCurrentAsync(
                actor, identity, selection, cancellationToken).ConfigureAwait(false);
            var settings = SourceControlProjectConfigurationResolver.Resolve(selection.Selection.Snapshot);
            var identitySelectionCode = pinRequest?.SelectionCode;
            if (identitySelectionCode is not null &&
                !IsExactIdentityRepositorySelectionCode(identitySelectionCode))
                throw new CoordinationException(
                    "source_control_pin_request_invalid", StatusCodes.Status400BadRequest);
            if (settings.AuthMode != SourceControlAuthMode.GitHubApp &&
                identitySelectionCode is not null)
                throw new CoordinationException(
                    "source_control_pin_request_invalid", StatusCodes.Status400BadRequest);
            var existing = await sourceControlOwner.FindRepositoryPinAsync(
                actor, identity, selection, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                EnsurePinnedConfiguration(existing, settings);
                if (identitySelectionCode is not null &&
                    (existing.GitHubAppBinding is not { } existingBinding ||
                     !string.Equals(
                         HashIdentityRepositorySelectionCode(identitySelectionCode),
                         existingBinding.IdentityRepositorySelectionHash,
                         StringComparison.Ordinal)))
                    throw new CoordinationException(
                        "source_control_repository_already_pinned", StatusCodes.Status409Conflict);
                return Results.Ok(ToPinView(existing));
            }

            var acceptedRun = CreateAcceptedRun(actor, identity, selection, current.State.Fence);
            var providerSelection = SourceControlProviderSelectionResolver.Resolve(
                selection.Selection.Snapshot, catalog, resolver);
            if (!string.Equals(adapter.ProviderId, providerSelection.Candidate.ProviderId, StringComparison.Ordinal))
                throw new SourceControlOperationException(
                    SourceControlFailureCode.CapabilityUnavailable,
                    "The selected SourceControl provider adapter is not registered.");

            SourceControlRepositoryPin pin;
            if (settings.AuthMode == SourceControlAuthMode.GitHubApp)
            {
                if (providerSelection.Candidate.ProviderId != SourceControlProviderIds.GitHub)
                    throw new CoordinationException(
                        "source_control_github_app_requires_github_provider", StatusCodes.Status409Conflict);
                if (identitySelectionCode is null)
                    throw new CoordinationException(
                        "source_control_github_app_selection_required", StatusCodes.Status409Conflict);

                pin = await redemption.WithGitHubAppSelectionCredentialAsync(
                    context,
                    acceptedRun,
                    settings.IdentityConnectionId!,
                    identitySelectionCode,
                    settings.Repository.FullName,
                    providerSelection.Candidate.RequiredCapabilities.Contains(
                        SourceControlCapabilities.IssueWrite),
                    async (credential, appBinding, token) =>
                    {
                        var negotiation = await adapter.NegotiateRepositoryAsync(
                            providerSelection.Candidate,
                            settings.Repository,
                            credential,
                            token).ConfigureAwait(false);
                        if (!negotiation.Resource.Capabilities.Contains(SourceControlCapabilities.RepositoryRead))
                            throw new SourceControlOperationException(
                                SourceControlFailureCode.CapabilityUnavailable,
                                "The selected SourceControl provider cannot read the configured repository.");
                        if (negotiation.ProviderRepositoryId != appBinding.RepositoryId ||
                            negotiation.IsPrivate != appBinding.IsPrivate ||
                            !string.Equals(
                                negotiation.Repository.FullName,
                                appBinding.RepositoryFullName,
                                StringComparison.OrdinalIgnoreCase))
                            throw new SourceControlOperationException(
                                SourceControlFailureCode.InvalidBinding,
                                "The GitHub App repository selection no longer matches the negotiated repository.");

                        var binding = new SourceControlGitHubAppBinding(
                            appBinding.ConnectionId,
                            appBinding.ConnectionRevision,
                            appBinding.InstallationId,
                            appBinding.PermissionDigest,
                            appBinding.SelectionHash,
                            appBinding.IssueWriteGranted);
                        var repositoryPin = SourceControlProjectConfigurationResolver.PinNegotiatedGitHubAppRepository(
                            selection.Selection.Snapshot,
                            catalog,
                            resolver,
                            acceptedRun,
                            SourceControlOwnerStore.CreateRepositoryPinId(acceptedRun),
                            identitySelectionCode,
                            negotiation,
                            binding,
                            timeProvider.GetUtcNow());
                        var operationContext = new SourceControlOperationContext(
                            repositoryPin.ProviderBinding, settings.Repository, credential);
                        await adapter.VerifyCurrentBindingAsync(
                            operationContext, SourceControlCapabilities.RepositoryRead, token)
                            .ConfigureAwait(false);
                        return repositoryPin;
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                pin = await redemption.WithCredentialAsync(
                    context,
                    acceptedRun,
                    new SourceControlCredentialReference(
                        RequireSecretApiReference(settings),
                        SourceControlSecretPurposes.Api),
                    async (credential, token) =>
                    {
                        var negotiation = await adapter.NegotiateRepositoryAsync(
                            providerSelection.Candidate,
                            settings.Repository,
                            credential,
                            token).ConfigureAwait(false);
                        if (!negotiation.Resource.Capabilities.Contains(SourceControlCapabilities.RepositoryRead))
                            throw new SourceControlOperationException(
                                SourceControlFailureCode.CapabilityUnavailable,
                                "The selected SourceControl provider cannot read the configured repository.");
                        var repositoryPin = SourceControlProjectConfigurationResolver.PinNegotiatedRepository(
                            selection.Selection.Snapshot,
                            catalog,
                            resolver,
                            acceptedRun,
                            SourceControlOwnerStore.CreateRepositoryPinId(acceptedRun),
                            negotiation,
                            timeProvider.GetUtcNow());
                        var operationContext = new SourceControlOperationContext(
                            repositoryPin.ProviderBinding, settings.Repository, credential);
                        await adapter.VerifyCurrentBindingAsync(
                            operationContext, SourceControlCapabilities.RepositoryRead, token).ConfigureAwait(false);
                        return repositoryPin;
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            await RequireUnchangedAuthorizedSelectionAsync(
                context, projectId, runId, selection, projects, cancellationToken).ConfigureAwait(false);
            var latest = await decisions.ReadCurrentAsync(
                actor, identity, selection, cancellationToken).ConfigureAwait(false);
            if (latest.StateVersion != current.StateVersion ||
                latest.State.Fence != current.State.Fence)
                throw new CoordinationException(
                    "source_control_pin_stale_decision", StatusCodes.Status409Conflict);

            var persisted = await sourceControlOwner.PersistRepositoryPinAsync(
                actor, identity, selection, pin, current.StateVersion, cancellationToken).ConfigureAwait(false);
            return Results.Accepted(value: ToPinView(persisted));
        }, cancellationToken);

    private static Task<IResult> CreateIssueAsync(
        string projectId,
        string runId,
        string sessionId,
        SourceControlIssueRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinatorDecisionOwnerStore decisions,
        SourceControlOwnerStore sourceControlOwner,
        ISourceControlAdapter adapter,
        SourceControlSecretRedemptionClient redemption,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            ValidateIssueRequest(request);
            var run = await ReadPinnedSourceControlRunAsync(
                context,
                projectId,
                runId,
                sessionId,
                options,
                projects,
                decisions,
                sourceControlOwner,
                cancellationToken).ConfigureAwait(false);
            EnsureAdapterMatchesPin(adapter, run.Pin);
            EnsureApiCredential(run.Pin);
            EnsureIssueWriteCapability(run.Pin);

            var issue = await redemption.WithCredentialAsync(
                context,
                run.Pin,
                run.Pin.ApiCredential,
                async (credential, token) =>
                {
                    await RequireCurrentSourceControlRunAsync(
                        context, run, projects, decisions, token).ConfigureAwait(false);
                    var operation = new SourceControlOperationContext(
                        run.Pin.ProviderBinding, run.Pin.Repository, credential);
                    await adapter.VerifyCurrentBindingAsync(
                        operation, SourceControlCapabilities.IssueWrite, token).ConfigureAwait(false);
                    await RequireCurrentSourceControlRunAsync(
                        context, run, projects, decisions, token).ConfigureAwait(false);
                    var result = await adapter.CreateIssueAsync(operation, request, token)
                        .ConfigureAwait(false);
                    await RequireCurrentSourceControlRunAsync(
                        context, run, projects, decisions, token).ConfigureAwait(false);
                    return result;
                },
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(issue);
        }, cancellationToken);

    private static Task<IResult> CreateOrReusePullRequestAsync(
        string projectId,
        string runId,
        string sessionId,
        SourceControlPullRequestRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinatorDecisionOwnerStore decisions,
        SourceControlOwnerStore sourceControlOwner,
        ISourceControlAdapter adapter,
        SourceControlSecretRedemptionClient redemption,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            ValidatePullRequestRequest(request);
            var run = await ReadPinnedSourceControlRunAsync(
                context,
                projectId,
                runId,
                sessionId,
                options,
                projects,
                decisions,
                sourceControlOwner,
                cancellationToken).ConfigureAwait(false);
            EnsureAdapterMatchesPin(adapter, run.Pin);
            EnsureApiCredential(run.Pin);

            var pullRequest = await redemption.WithCredentialAsync(
                context,
                run.Pin,
                run.Pin.ApiCredential,
                async (credential, token) =>
                {
                    await RequireCurrentSourceControlRunAsync(
                        context, run, projects, decisions, token).ConfigureAwait(false);
                    var operation = new SourceControlOperationContext(
                        run.Pin.ProviderBinding, run.Pin.Repository, credential);
                    await adapter.VerifyCurrentBindingAsync(
                        operation, SourceControlCapabilities.PullRequestWrite, token).ConfigureAwait(false);
                    await RequireCurrentSourceControlRunAsync(
                        context, run, projects, decisions, token).ConfigureAwait(false);
                    var result = await adapter.CreateOrReusePullRequestAsync(
                        operation, request, token).ConfigureAwait(false);
                    await RequireCurrentSourceControlRunAsync(
                        context, run, projects, decisions, token).ConfigureAwait(false);
                    return result;
                },
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(pullRequest);
        }, cancellationToken);

    private static Task<IResult> ReadReviewsAsync(
        string projectId,
        string runId,
        string sessionId,
        long pullRequestNumber,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinatorDecisionOwnerStore decisions,
        SourceControlOwnerStore sourceControlOwner,
        ISourceControlAdapter adapter,
        SourceControlSecretRedemptionClient redemption,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (pullRequestNumber <= 0)
                throw new CoordinationException(
                    "source_control_pull_request_number_invalid", StatusCodes.Status400BadRequest);
            var run = await ReadPinnedSourceControlRunAsync(
                context,
                projectId,
                runId,
                sessionId,
                options,
                projects,
                decisions,
                sourceControlOwner,
                cancellationToken).ConfigureAwait(false);
            EnsureAdapterMatchesPin(adapter, run.Pin);
            EnsureApiCredential(run.Pin);

            var reviews = await redemption.WithCredentialAsync(
                context,
                run.Pin,
                run.Pin.ApiCredential,
                async (credential, token) =>
                {
                    await RequireCurrentSourceControlRunAsync(
                        context, run, projects, decisions, token).ConfigureAwait(false);
                    var operation = new SourceControlOperationContext(
                        run.Pin.ProviderBinding, run.Pin.Repository, credential);
                    await adapter.VerifyCurrentBindingAsync(
                        operation, SourceControlCapabilities.ReviewRead, token).ConfigureAwait(false);
                    await RequireCurrentSourceControlRunAsync(
                        context, run, projects, decisions, token).ConfigureAwait(false);
                    var result = await adapter.ReadReviewsAsync(
                        operation, pullRequestNumber, token).ConfigureAwait(false);
                    await RequireCurrentSourceControlRunAsync(
                        context, run, projects, decisions, token).ConfigureAwait(false);
                    return result;
                },
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(reviews);
        }, cancellationToken);

    private static Task<IResult> PrepareWorkspaceAsync(
        string projectId,
        string runId,
        string sessionId,
        PrepareSourceControlWorkspaceRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinatorDecisionOwnerStore decisions,
        SourceControlOwnerStore sourceControlOwner,
        ISourceControlAdapter adapter,
        SourceControlSecretRedemptionClient redemption,
        IServiceProvider services,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            ValidateWorkspaceRequest(request.WorkspaceId, request.BaseSha, request.BranchName);
            var workspaceManager = services.GetService<GitWorkspaceManager>()
                ?? throw new CoordinationException(
                    "source_control_workspace_unconfigured", StatusCodes.Status503ServiceUnavailable);
            var run = await ReadPinnedSourceControlRunAsync(
                context,
                projectId,
                runId,
                sessionId,
                options,
                projects,
                decisions,
                sourceControlOwner,
                cancellationToken).ConfigureAwait(false);
            EnsureAdapterMatchesPin(adapter, run.Pin);
            var checkoutCredential = run.Pin.CheckoutCredential;
            if (run.Pin.GitHubAppBinding is null)
            {
                if (checkoutCredential is null)
                    throw new CoordinationException(
                        "source_control_checkout_not_configured", StatusCodes.Status409Conflict);
                EnsureCredentialPurpose(checkoutCredential, SourceControlSecretPurposes.Checkout);
            }
            EnsureCheckoutCapability(run.Pin);

            var workspace = await redemption.WithCredentialAsync(
                context,
                run.Pin,
                checkoutCredential,
                async (credential, token) =>
                {
                    await RequireCurrentSourceControlRunAsync(
                        context, run, projects, decisions, token).ConfigureAwait(false);
                    var operation = new SourceControlOperationContext(
                        run.Pin.ProviderBinding, run.Pin.Repository, credential);
                    await adapter.VerifyCurrentBindingAsync(
                        operation, SourceControlCapabilities.RepositoryCheckout, token).ConfigureAwait(false);
                    await RequireCurrentSourceControlRunAsync(
                        context, run, projects, decisions, token).ConfigureAwait(false);
                    var result = await workspaceManager.PrepareAsync(
                        new GitWorkspaceRequest(
                            operation, request.WorkspaceId, request.BaseSha, request.BranchName),
                        token).ConfigureAwait(false);
                    await RequireCurrentSourceControlRunAsync(
                        context, run, projects, decisions, token).ConfigureAwait(false);
                    return result;
                },
                cancellationToken).ConfigureAwait(false);
            return Results.Accepted(value: new SourceControlWorkspaceView(
                workspace.WorkspaceId,
                workspace.Path,
                workspace.BaseSha,
                workspace.BranchName));
        }, cancellationToken);

    private static Task<IResult> ReadWorkspaceDiffAsync(
        string projectId,
        string runId,
        string sessionId,
        string workspaceId,
        PrepareSourceControlWorkspaceRevisionRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinatorDecisionOwnerStore decisions,
        SourceControlOwnerStore sourceControlOwner,
        ISourceControlAdapter adapter,
        SourceControlSecretRedemptionClient redemption,
        IServiceProvider services,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            ValidateWorkspaceRequest(workspaceId, request.BaseSha, request.BranchName);
            var workspaceManager = services.GetService<GitWorkspaceManager>()
                ?? throw new CoordinationException(
                    "source_control_workspace_unconfigured", StatusCodes.Status503ServiceUnavailable);
            var run = await ReadPinnedSourceControlRunAsync(
                context,
                projectId,
                runId,
                sessionId,
                options,
                projects,
                decisions,
                sourceControlOwner,
                cancellationToken).ConfigureAwait(false);
            EnsureAdapterMatchesPin(adapter, run.Pin);
            var checkoutCredential = run.Pin.CheckoutCredential;
            if (run.Pin.GitHubAppBinding is null)
            {
                if (checkoutCredential is null)
                    throw new CoordinationException(
                        "source_control_checkout_not_configured", StatusCodes.Status409Conflict);
                EnsureCredentialPurpose(checkoutCredential, SourceControlSecretPurposes.Checkout);
            }
            EnsureCheckoutCapability(run.Pin);

            var diff = await redemption.WithCredentialAsync(
                context,
                run.Pin,
                checkoutCredential,
                async (credential, token) =>
                {
                    await RequireCurrentSourceControlRunAsync(
                        context, run, projects, decisions, token).ConfigureAwait(false);
                    var operation = new SourceControlOperationContext(
                        run.Pin.ProviderBinding, run.Pin.Repository, credential);
                    await adapter.VerifyCurrentBindingAsync(
                        operation, SourceControlCapabilities.RepositoryCheckout, token).ConfigureAwait(false);
                    await RequireCurrentSourceControlRunAsync(
                        context, run, projects, decisions, token).ConfigureAwait(false);
                    var workspaceRequest = new GitWorkspaceRequest(
                        operation, workspaceId, request.BaseSha, request.BranchName);
                    var workspace = await workspaceManager.PrepareAsync(
                        workspaceRequest, token).ConfigureAwait(false);
                    await RequireCurrentSourceControlRunAsync(
                        context, run, projects, decisions, token).ConfigureAwait(false);
                    var result = await workspaceManager.AssembleDiffAsync(
                        workspaceRequest, workspace, token).ConfigureAwait(false);
                    await RequireCurrentSourceControlRunAsync(
                        context, run, projects, decisions, token).ConfigureAwait(false);
                    return result;
                },
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(new SourceControlWorkspaceDiffView(
                diff.WorkspaceId, diff.BaseSha, diff.HeadSha, diff.Status, diff.Patch));
        }, cancellationToken);

    private static Task<IResult> CaptureWorkspaceOutputAsync(
        string projectId,
        string runId,
        string sessionId,
        string workspaceId,
        PrepareSourceControlWorkspaceRevisionRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinatorDecisionOwnerStore decisions,
        CoordinationOwnerStore coordinationOwner,
        SourceControlOwnerStore sourceControlOwner,
        EventsAddressedMessageClient events,
        ISourceControlAdapter adapter,
        IServiceProvider services,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            ValidateWorkspaceRequest(workspaceId, request.BaseSha, request.BranchName);
            var run = await ReadPinnedSourceControlRunAsync(
                context,
                projectId,
                runId,
                sessionId,
                options,
                projects,
                decisions,
                sourceControlOwner,
                cancellationToken).ConfigureAwait(false);
            EnsureAdapterMatchesPin(adapter, run.Pin);
            EnsureCheckoutCapability(run.Pin);
            await RequireCapturableOutputRunStateAsync(
                coordinationOwner, run.Identity, cancellationToken).ConfigureAwait(false);

            var existingPending = await sourceControlOwner.ReadPendingOutputCaptureAsync(
                run.Identity,
                workspaceId,
                SourceControlOwnerStore.HashSelection(run.Selection.Selection),
                cancellationToken).ConfigureAwait(false);
            if (existingPending is not null)
            {
                if (!string.Equals(
                        existingPending.Proof.BaseSha, request.BaseSha, StringComparison.OrdinalIgnoreCase))
                    throw new CoordinationException(
                        "source_control_output_capture_pending_conflict", StatusCodes.Status409Conflict);
                return await WriteAndAdmitOutputCaptureAsync(existingPending).ConfigureAwait(false);
            }

            var workspaceManager = services.GetService<GitWorkspaceManager>()
                ?? throw new CoordinationException(
                    "source_control_workspace_unconfigured", StatusCodes.Status503ServiceUnavailable);
            var workspaceRequest = new GitWorkspaceCaptureRequest(
                run.Pin.ProviderBinding,
                run.Pin.Repository,
                workspaceId,
                request.BaseSha,
                request.BranchName);
            var workspace = await workspaceManager.OpenExistingAsync(
                workspaceRequest, cancellationToken).ConfigureAwait(false);
            var capture = await workspaceManager.CaptureAsync(
                workspaceRequest, workspace, cancellationToken).ConfigureAwait(false);
            var document = GitWorkspaceCapturePackage.Create(capture);

            await RequireCurrentSourceControlRunAsync(
                context, run, projects, decisions, cancellationToken).ConfigureAwait(false);
            await RequireCapturableOutputRunStateAsync(
                coordinationOwner, run.Identity, cancellationToken).ConfigureAwait(false);
            var registered = await sourceControlOwner.RegisterOutputCaptureAsync(
                run.Actor,
                run.Identity,
                run.Selection,
                run.Pin,
                run.DecisionStateVersion,
                document,
                cancellationToken).ConfigureAwait(false);

            return await WriteAndAdmitOutputCaptureAsync(registered).ConfigureAwait(false);

            async Task<IResult> WriteAndAdmitOutputCaptureAsync(SourceControlOutputCaptureRecord capture)
            {
                await RequireCurrentSourceControlRunAsync(
                    context, run, projects, decisions, cancellationToken).ConfigureAwait(false);
                await RequireCapturableOutputRunStateAsync(
                    coordinationOwner, run.Identity, cancellationToken).ConfigureAwait(false);
                var acknowledgment = await events.WriteProducedRunCaptureAsync(
                    context, capture.Proof, capture.PackageBytes, cancellationToken).ConfigureAwait(false);
                await RequireCurrentSourceControlRunAsync(
                    context, run, projects, decisions, cancellationToken).ConfigureAwait(false);
                await RequireCapturableOutputRunStateAsync(
                    coordinationOwner, run.Identity, cancellationToken).ConfigureAwait(false);
                var admitted = await sourceControlOwner.AdmitOutputCaptureAsync(
                    run.Actor,
                    run.Identity,
                    run.Selection,
                    run.Pin,
                    run.DecisionStateVersion,
                    capture.Proof.CaptureId,
                    acknowledgment.Entry,
                    cancellationToken).ConfigureAwait(false);
                return Results.Json(
                    ToOutputCaptureSummary(admitted),
                    statusCode: acknowledgment.IsDuplicate
                        ? StatusCodes.Status200OK
                        : StatusCodes.Status201Created);
            }
        }, cancellationToken);

    private static Task<IResult> ReadOutputCapturePageAsync(
        string projectId,
        string runId,
        string sessionId,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinationOwnerStore coordinationOwner,
        SourceControlOwnerStore sourceControlOwner,
        int? limit,
        DateTimeOffset? beforeCapturedAt,
        string? beforeCaptureId,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var identity = new SessionIdentity(projectId, runId, sessionId);
            var authorized = await ReadAuthorizedOutputCaptureScopeAsync(
                context, identity, options, projects, coordinationOwner, cancellationToken)
                .ConfigureAwait(false);
            var pageSize = limit ?? 50;
            var records = await sourceControlOwner.ReadOutputCapturePageAsync(
                identity,
                SourceControlOwnerStore.HashSelection(authorized.Selection.Selection),
                beforeCapturedAt,
                beforeCaptureId,
                pageSize,
                cancellationToken).ConfigureAwait(false);
            await RevalidateOutputCaptureReadAsync(
                context, identity, authorized, options, projects, coordinationOwner, cancellationToken)
                .ConfigureAwait(false);
            var captures = records.Select(ToOutputCaptureSummary).ToImmutableArray();
            var last = records.Length == pageSize ? records[^1] : null;
            return Results.Ok(new SourceControlOutputCapturePageView(
                captures,
                last?.Proof.CapturedAt,
                last?.Proof.CaptureId));
        }, cancellationToken);

    private static Task<IResult> ReadOutputCaptureAsync(
        string projectId,
        string runId,
        string sessionId,
        string captureId,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinationOwnerStore coordinationOwner,
        SourceControlOwnerStore sourceControlOwner,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var identity = new SessionIdentity(projectId, runId, sessionId);
            var authorized = await ReadAuthorizedOutputCaptureScopeAsync(
                context, identity, options, projects, coordinationOwner, cancellationToken)
                .ConfigureAwait(false);
            var record = await ReadAdmittedOutputCaptureAsync(
                identity, captureId, authorized, sourceControlOwner, cancellationToken)
                .ConfigureAwait(false);
            var manifest = ValidateOutputCaptureDocument(record);
            await RevalidateAdmittedOutputCaptureAsync(
                context, identity, captureId, record, authorized, options, projects,
                coordinationOwner, sourceControlOwner, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new SourceControlOutputCaptureDetailView(
                ToOutputCaptureSummary(record), manifest));
        }, cancellationToken);

    private static Task<IResult> ReadOutputCaptureDiffAsync(
        string projectId,
        string runId,
        string sessionId,
        string captureId,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinationOwnerStore coordinationOwner,
        SourceControlOwnerStore sourceControlOwner,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var identity = new SessionIdentity(projectId, runId, sessionId);
            var authorized = await ReadAuthorizedOutputCaptureScopeAsync(
                context, identity, options, projects, coordinationOwner, cancellationToken)
                .ConfigureAwait(false);
            var record = await ReadAdmittedOutputCaptureAsync(
                identity, captureId, authorized, sourceControlOwner, cancellationToken)
                .ConfigureAwait(false);
            _ = ValidateOutputCaptureDocument(record);
            string patch;
            try
            {
                patch = new UTF8Encoding(false, true).GetString(record.PatchBytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw OutputCaptureIntegrityFailure(exception);
            }
            await RevalidateAdmittedOutputCaptureAsync(
                context, identity, captureId, record, authorized, options, projects,
                coordinationOwner, sourceControlOwner, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new SourceControlOutputCaptureDiffView(
                ToOutputCaptureSummary(record), patch));
        }, cancellationToken);

    private static Task<IResult> ReadOutputCaptureFileAsync(
        string projectId,
        string runId,
        string sessionId,
        string captureId,
        [FromQuery] string path,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinationOwnerStore coordinationOwner,
        SourceControlOwnerStore sourceControlOwner,
        EventsAddressedMessageClient events,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var identity = new SessionIdentity(projectId, runId, sessionId);
            var authorized = await ReadAuthorizedOutputCaptureScopeAsync(
                context, identity, options, projects, coordinationOwner, cancellationToken)
                .ConfigureAwait(false);
            var record = await ReadAdmittedOutputCaptureAsync(
                identity, captureId, authorized, sourceControlOwner, cancellationToken)
                .ConfigureAwait(false);
            var manifest = ValidateOutputCaptureDocument(record);
            var content = await events.ReadProducedRunCaptureAsync(
                context, record.Proof, cancellationToken).ConfigureAwait(false);
            if (content.Entry.Capture != record.Proof ||
                content.Entry.Position != record.EventPosition)
                throw OutputCaptureIntegrityFailure();

            byte[] file;
            try
            {
                file = GitWorkspaceCapturePackage.ExtractFile(
                    content.PackageBytes, manifest, record.Proof.PackageSha256, path);
            }
            catch (FileNotFoundException)
            {
                throw new CoordinationException(
                    "source_control_output_capture_file_not_found", StatusCodes.Status404NotFound);
            }
            catch (ArgumentException exception)
            {
                throw OutputCaptureIntegrityFailure(exception);
            }

            await RevalidateAdmittedOutputCaptureAsync(
                context, identity, captureId, record, authorized, options, projects,
                coordinationOwner, sourceControlOwner, cancellationToken).ConfigureAwait(false);
            context.Response.Headers["X-Source-Control-Output-Sha256"] =
                GitWorkspaceCapturePackage.Hash(file);
            return Results.Bytes(file, "application/octet-stream");
        }, cancellationToken);

    private static async Task<AuthorizedOutputCaptureScope> ReadAuthorizedOutputCaptureScopeAsync(
        HttpContext context,
        SessionIdentity identity,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinationOwnerStore coordinationOwner,
        CancellationToken cancellationToken)
    {
        _ = RequireOwnerActor(context, options, identity.ProjectId, identity.RunId);
        var selection = await projects.ReadSelectionForReadWithAuthorityAsync(
            context, identity.ProjectId, identity.RunId, cancellationToken).ConfigureAwait(false);
        var state = await coordinationOwner.ReadCurrentExecutionStateAsync(
            identity.ProjectId, identity.RunId, cancellationToken).ConfigureAwait(false);
        if (state is "failed" or "indeterminate")
            throw OutputCaptureWithheld();
        return new AuthorizedOutputCaptureScope(selection);
    }

    private static async Task RevalidateOutputCaptureReadAsync(
        HttpContext context,
        SessionIdentity identity,
        AuthorizedOutputCaptureScope previous,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinationOwnerStore coordinationOwner,
        CancellationToken cancellationToken)
    {
        var current = await ReadAuthorizedOutputCaptureScopeAsync(
            context, identity, options, projects, coordinationOwner, cancellationToken).ConfigureAwait(false);
        if (SourceControlOwnerStore.HashSelection(current.Selection.Selection) !=
            SourceControlOwnerStore.HashSelection(previous.Selection.Selection))
            throw OutputCaptureWithheld();
    }

    private static async Task<SourceControlOutputCaptureRecord> ReadAdmittedOutputCaptureAsync(
        SessionIdentity identity,
        string captureId,
        AuthorizedOutputCaptureScope authorized,
        SourceControlOwnerStore sourceControlOwner,
        CancellationToken cancellationToken)
    {
        var record = await sourceControlOwner.ReadOutputCaptureAsync(
            identity,
            captureId,
            SourceControlOwnerStore.HashSelection(authorized.Selection.Selection),
            cancellationToken).ConfigureAwait(false);
        if (record is null || record.State != "admitted")
            throw OutputCaptureWithheld();
        return record;
    }

    private static async Task RevalidateAdmittedOutputCaptureAsync(
        HttpContext context,
        SessionIdentity identity,
        string captureId,
        SourceControlOutputCaptureRecord expected,
        AuthorizedOutputCaptureScope previous,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinationOwnerStore coordinationOwner,
        SourceControlOwnerStore sourceControlOwner,
        CancellationToken cancellationToken)
    {
        await RevalidateOutputCaptureReadAsync(
            context, identity, previous, options, projects, coordinationOwner, cancellationToken)
            .ConfigureAwait(false);
        var current = await sourceControlOwner.ReadOutputCaptureAsync(
            identity,
            captureId,
            SourceControlOwnerStore.HashSelection(previous.Selection.Selection),
            cancellationToken).ConfigureAwait(false);
        if (current is null ||
            current.State != "admitted" ||
            current.Proof != expected.Proof ||
            current.EventPosition != expected.EventPosition ||
            current.ObjectKey != expected.ObjectKey)
            throw OutputCaptureWithheld();
    }

    private static GitWorkspaceCapturedOutputManifest ValidateOutputCaptureDocument(
        SourceControlOutputCaptureRecord record)
    {
        var proof = record.Proof;
        var package = ProducedRunCaptureContractValidation.CreatePackageReference(proof);
        try
        {
            if (record.ObjectKey != package.Key.Value ||
                record.EventPosition is null or < 1 ||
                record.ManifestBytes.LongLength != proof.ManifestByteLength ||
                GitWorkspaceCapturePackage.Hash(record.ManifestBytes) != proof.ManifestSha256 ||
                record.PatchBytes.LongLength != proof.PatchByteLength ||
                GitWorkspaceCapturePackage.Hash(record.PatchBytes) != proof.PatchSha256)
                throw new ArgumentException("The persisted output capture metadata is inconsistent.");
            var manifest = GitWorkspaceCapturePackage.ParseManifest(record.ManifestBytes);
            if (manifest.WorkspaceId != proof.WorkspaceId ||
                manifest.RunId != proof.Identity.RunId ||
                manifest.RepositoryId != proof.RepositoryId ||
                manifest.ResourceGeneration != proof.ResourceGeneration ||
                manifest.WorkspaceIncarnationId != proof.WorkspaceIncarnationId ||
                !string.Equals(manifest.BaseSha, proof.BaseSha, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(manifest.OutputTreeSha, proof.OutputTreeSha, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The persisted output capture manifest binding is inconsistent.");
            return manifest;
        }
        catch (ArgumentException exception)
        {
            throw OutputCaptureIntegrityFailure(exception);
        }
    }

    private static async Task RequireCapturableOutputRunStateAsync(
        CoordinationOwnerStore coordinationOwner,
        SessionIdentity identity,
        CancellationToken cancellationToken)
    {
        var state = await coordinationOwner.ReadCurrentExecutionStateAsync(
            identity.ProjectId, identity.RunId, cancellationToken).ConfigureAwait(false);
        if (state is not ("active" or "idle" or "blocked"))
            throw new CoordinationException(
                "source_control_output_run_unavailable", StatusCodes.Status409Conflict);
    }

    private static CoordinationException OutputCaptureWithheld() =>
        new("source_control_output_capture_unavailable", StatusCodes.Status404NotFound);

    private static CoordinationException OutputCaptureIntegrityFailure(Exception? inner = null) =>
        new("source_control_output_capture_integrity_invalid", StatusCodes.Status503ServiceUnavailable, inner);

    private static SourceControlOutputCaptureSummaryView ToOutputCaptureSummary(
        SourceControlOutputCaptureRecord record)
    {
        if (record.EventPosition is not long position)
            throw OutputCaptureIntegrityFailure();
        return new(
            record.Proof.CaptureId,
            record.Proof.EventId,
            position,
            record.Proof.CapturedAt,
            record.Proof.WorkspaceId,
            record.Proof.BaseSha,
            record.Proof.OutputTreeSha,
            record.Proof.ManifestSha256,
            record.Proof.ManifestByteLength,
            record.Proof.PatchSha256,
            record.Proof.PatchByteLength,
            record.Proof.PackageByteLength);
    }

    private static Task<IResult> PrepareMergeIntentAsync(
        string projectId,
        string runId,
        string sessionId,
        PrepareSourceControlMergeIntentRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinatorDecisionOwnerStore decisions,
        SourceControlOwnerStore sourceControlOwner,
        ProviderCatalog catalog,
        ProviderResolver resolver,
        ISourceControlAdapter adapter,
        SourceControlSecretRedemptionClient redemption,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var actor = RequireOwnerActor(context, options, projectId, runId);
            var identity = new SessionIdentity(projectId, runId, sessionId);
            ValidateRequest(request);
            var selection = await projects.ReadAcceptedSelectionWithAuthorityAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            var current = await decisions.ReadCurrentAsync(
                actor, identity, selection, cancellationToken).ConfigureAwait(false);

            var intentId = SourceControlOwnerStore.CreateIntentId(identity, request.IdempotencyKey);
            var approvalRequestId = SourceControlOwnerStore.CreateApprovalRequestId(intentId);
            var existing = await sourceControlOwner.FindMergeIntentByIdempotencyKeyAsync(
                actor, identity, selection, request.IdempotencyKey, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (existing.State != "approval_pending")
                    return Results.Ok(ToView(existing));
                if (current.State.PendingGate?.RequestId == existing.ApprovalRequestId &&
                    current.State.PendingGate.SubjectId == existing.IntentId)
                    return Results.Accepted(value: ToView(existing));
                if (current.State.PendingGate is not null ||
                    current.StateVersion != existing.SourceStateVersion)
                    throw new CoordinationException(
                        "source_control_intent_stale", StatusCodes.Status409Conflict);
            }
            else if (current.State.PendingGate is not null)
            {
                throw new CoordinationException(
                    "source_control_decision_gate_pending", StatusCodes.Status409Conflict);
            }
            if (current.StateVersion != request.ExpectedStateVersion)
                throw new CoordinationException(
                    "source_control_decision_stale", StatusCodes.Status409Conflict);

            var acceptedRun = CreateAcceptedRun(actor, identity, selection, current.State.Fence);
            var settings = SourceControlProjectConfigurationResolver.Resolve(selection.Selection.Snapshot);
            var providerSelection = SourceControlProviderSelectionResolver.Resolve(
                selection.Selection.Snapshot, catalog, resolver);
            var repositoryPin = await sourceControlOwner.ReadRepositoryPinAsync(
                actor, identity, selection, cancellationToken).ConfigureAwait(false);
            EnsurePinnedConfiguration(repositoryPin, settings);
            if (!string.Equals(adapter.ProviderId, providerSelection.Candidate.ProviderId, StringComparison.Ordinal))
                throw new SourceControlOperationException(
                    SourceControlFailureCode.CapabilityUnavailable,
                    "The selected SourceControl provider adapter is not registered.");

            var assemblyTransition = CoordinatorDecisionFlow.RequestAssembly(
                current.State,
                new CoordinatorAssemblyRequest(
                    "source-assembly-" + intentId,
                    request.WorkflowId,
                    request.DefinitionRevision,
                    request.WorkPlanId,
                    request.WorkflowStepId,
                    WorkflowPlatformGate.Merge));
            if (!assemblyTransition.IsSuccess || assemblyTransition.Value is null)
                throw new CoordinationException(
                    "source_control_merge_step_invalid", StatusCodes.Status409Conflict);

            var prepared = await redemption.WithCredentialAsync(
                context,
                repositoryPin,
                repositoryPin.ApiCredential,
                async (credential, token) =>
                {
                    var operationContext = new SourceControlOperationContext(
                        repositoryPin.ProviderBinding, settings.Repository, credential);
                    await adapter.VerifyCurrentBindingAsync(
                        operationContext, SourceControlCapabilities.Merge, token).ConfigureAwait(false);
                    var pullRequest = await adapter.ReadPullRequestAsync(
                        operationContext, request.PullRequestNumber, token).ConfigureAwait(false);
                    return (Pin: repositoryPin, PullRequest: pullRequest);
                },
                cancellationToken).ConfigureAwait(false);

            await RequireUnchangedAuthorizedSelectionAsync(
                context, projectId, runId, selection, projects, cancellationToken).ConfigureAwait(false);
            var refreshedDecision = await decisions.ReadCurrentAsync(
                actor, identity, selection, cancellationToken).ConfigureAwait(false);
            if (refreshedDecision.StateVersion != current.StateVersion ||
                refreshedDecision.State.Fence != current.State.Fence)
                throw new CoordinationException(
                    "source_control_decision_stale", StatusCodes.Status409Conflict);

            var intentRequest = SourceControlMergeIntentRequest.Create(
                intentId,
                approvalRequestId,
                acceptedRun,
                prepared.Pin,
                current.StateVersion,
                current.State,
                assemblyTransition.Value,
                prepared.PullRequest,
                request.Method,
                timeProvider.GetUtcNow());
            var savedIntent = await sourceControlOwner.PersistMergeIntentAsync(
                actor,
                identity,
                selection,
                intentRequest,
                request.IdempotencyKey,
                cancellationToken).ConfigureAwait(false);
            await RequireUnchangedAuthorizedSelectionAsync(
                context, projectId, runId, selection, projects, cancellationToken).ConfigureAwait(false);
            var latest = await decisions.ReadCurrentAsync(
                actor, identity, selection, cancellationToken).ConfigureAwait(false);
            if (latest.StateVersion != current.StateVersion ||
                latest.State.Fence != current.State.Fence ||
                latest.State.PendingGate is not null)
                throw new CoordinationException(
                    "source_control_decision_stale", StatusCodes.Status409Conflict);

            var approval = CoordinatorDecisionFlow.RequestApproval(
                latest.State,
                savedIntent.ApprovalRequestId,
                savedIntent.IntentId,
                selection.Authorization.ActorId,
                $"Approve {savedIntent.Method} merge of pull request {savedIntent.PullRequestNumber} " +
                $"from {savedIntent.HeadBranch} into {savedIntent.BaseBranch} at the recorded revisions.");
            if (!approval.IsSuccess)
                throw new CoordinationException(
                    "source_control_approval_invalid", StatusCodes.Status409Conflict);
            var persisted = await decisions.PersistTransitionAsync(
                actor,
                identity,
                selection,
                latest.StateVersion,
                request.IdempotencyKey,
                savedIntent.ApprovalRequestId,
                "approval.request.source_control.merge",
                CoordinatorDecisionOwnerStore.ComputeCommandHash(new
                {
                    latest.StateVersion,
                    savedIntent.IntentId,
                    savedIntent.ApprovalRequestId,
                    savedIntent.WorkflowStepId,
                    savedIntent.PullRequestNumber,
                    savedIntent.HeadSha,
                    savedIntent.BaseSha,
                    savedIntent.Method
                }),
                approval.State!,
                transitionAccepted: true,
                approval.Issues,
                confirmedSelectionContext: null,
                candidateSelectionContext: null,
                validatedTransitionValue: new { savedIntent.IntentId, savedIntent.ApprovalRequestId },
                cancellationToken).ConfigureAwait(false);

            return Results.Accepted(value: new SourceControlMergeApprovalView(
                savedIntent.IntentId,
                savedIntent.ApprovalRequestId,
                "approval_pending",
                persisted.DecisionId,
                persisted.StateVersion,
                persisted.State.PendingGate));
        }, cancellationToken);

    private static Task<IResult> ExecuteMergeIntentAsync(
        string projectId,
        string runId,
        string sessionId,
        string intentId,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinatorDecisionOwnerStore decisions,
        SourceControlOwnerStore sourceControlOwner,
        ProviderCatalog catalog,
        ProviderResolver resolver,
        ISourceControlAdapter adapter,
        SourceControlSecretRedemptionClient redemption,
        IExecutableActionGrantOwnerLookup grantOwner,
        ExecutableActionGuard actionGuard,
        AgtPolicyProvider policyProvider,
        IServiceProvider services,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var actor = RequireOwnerActor(context, options, projectId, runId);
            var identity = new SessionIdentity(projectId, runId, sessionId);
            var selection = await projects.ReadAcceptedSelectionWithAuthorityAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            var intent = await sourceControlOwner.ReadMergeIntentAsync(
                actor, identity, selection, intentId, cancellationToken).ConfigureAwait(false);
            if (intent.State == "merged")
                return Results.Ok(ToView(intent));
            if (intent.State == "merge_started")
                return await ResolveInterruptedMergeAsync(
                    context,
                    actor,
                    identity,
                    selection,
                    intent,
                    projects,
                    decisions,
                    sourceControlOwner,
                    grantOwner,
                    cancellationToken).ConfigureAwait(false);
            if (intent.State == "conflict")
                return Results.Json(
                    new
                    {
                        intentId = intent.IntentId,
                        state = intent.State,
                        failureCode = intent.LastFailureCode
                    },
                    statusCode: StatusCodes.Status409Conflict);
            if (intent.State != "approved" || intent.GrantReference is null)
                return Results.Json(
                    new { error = intent.State == "outcome_uncertain"
                        ? "source_control_merge_outcome_uncertain"
                        : "source_control_merge_not_approved" },
                    statusCode: intent.State == "outcome_uncertain"
                        ? StatusCodes.Status502BadGateway
                        : StatusCodes.Status409Conflict);

            if (!string.Equals(adapter.ProviderId, intent.Pin.ProviderBinding.ProviderId, StringComparison.Ordinal))
                throw new SourceControlOperationException(
                    SourceControlFailureCode.CapabilityUnavailable,
                    "The pinned SourceControl provider adapter is not registered.");

            var policyOptions = services.GetService<AgtPolicyProviderOptions>()
                ?? throw new CoordinationException(
                    "source_control_action_policy_unconfigured", StatusCodes.Status503ServiceUnavailable);
            var policyBinding = await ResolvePolicyBindingAsync(
                selection.Selection.Snapshot,
                selection.Selection.RunId,
                catalog,
                resolver,
                policyProvider,
                policyOptions,
                cancellationToken).ConfigureAwait(false);
            try
            {
                await RequireCurrentMergeAuthorityAsync(
                    context,
                    actor,
                    identity,
                    selection,
                    intent,
                    projects,
                    decisions,
                    grantOwner,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (CoordinationException exception) when (IsTerminalPreEffectConflict(exception))
            {
                await sourceControlOwner.RecordPreEffectMergeConflictAsync(
                    identity, intent.IntentId, exception.Code, CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            var invocation = new ExecutableActionInvocation(
                context.User,
                intent.AcceptedRun.RootSessionId,
                intent.WorkflowStepId,
                SourceControlMergeGrantProducer.ActionId,
                SourceControlMergeGrantProducer.Purpose,
                intent.GrantReference,
                intent.AcceptedRun.Fence,
                policyBinding,
                Guid.NewGuid());
            var guarded = await actionGuard.ExecuteAsync(
                invocation,
                token => ExecuteMergeEffectAsync(
                    context,
                    actor,
                    identity,
                    selection,
                    intent,
                    intent.GrantReference,
                    sourceControlOwner,
                    projects,
                    decisions,
                    grantOwner,
                    adapter,
                    redemption,
                    token),
                cancellationToken).ConfigureAwait(false);
            if (!guarded.EffectInvoked || guarded.EffectResult is null)
            {
                var status = guarded.Outcome == PolicyEvaluationOutcome.Deny
                    ? StatusCodes.Status403Forbidden
                    : StatusCodes.Status503ServiceUnavailable;
                return Results.Json(
                    new { error = "source_control_action_not_authorized" },
                    statusCode: status);
            }

            var result = guarded.EffectResult;
            return result.State switch
            {
                "merged" => Results.Ok(new SourceControlMergeExecutionView(
                    intent.IntentId, result.State, result.MergeSha, null)),
                "outcome_uncertain" => Results.Json(
                    new SourceControlMergeExecutionView(intent.IntentId, result.State, null, result.FailureCode),
                    statusCode: StatusCodes.Status502BadGateway),
                _ => Results.Json(
                    new SourceControlMergeExecutionView(intent.IntentId, result.State, null, result.FailureCode),
                    statusCode: StatusCodes.Status409Conflict)
            };
        }, cancellationToken);

    private static async Task<SourceControlMergeExecutionResult> ExecuteMergeEffectAsync(
        HttpContext context,
        CoordinationActor actor,
        SessionIdentity identity,
        AuthorizedRunSelection selection,
        SourceControlMergeIntentSnapshot intent,
        ExecutableActionGrantReference grantReference,
        SourceControlOwnerStore sourceControlOwner,
        ProjectsRunSelectionClient projects,
        CoordinatorDecisionOwnerStore decisions,
        IExecutableActionGrantOwnerLookup grantOwner,
        ISourceControlAdapter adapter,
        SourceControlSecretRedemptionClient redemption,
        CancellationToken cancellationToken)
    {
        await using var repositoryLock = await sourceControlOwner.AcquireRepositoryMergeLockAsync(
            intent.Pin.Repository, cancellationToken).ConfigureAwait(false);
        var mergeStarted = false;
        try
        {
            SourceControlMergeIntentSnapshot lockedIntent;
            try
            {
                lockedIntent = await sourceControlOwner.ReadMergeIntentAsync(
                    actor, identity, selection, intent.IntentId, cancellationToken).ConfigureAwait(false);
            }
            catch (CoordinationException exception) when (
                exception.Code == "source_control_intent_unavailable")
            {
                try
                {
                    await RequireCurrentMergeAuthorityAsync(
                        context,
                        actor,
                        identity,
                        selection,
                        intent,
                        projects,
                        decisions,
                        grantOwner,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (CoordinationException fenceChanged) when (
                    fenceChanged.Code == "source_control_run_binding_changed")
                {
                    if (intent.GrantReference is { } staleGrantReference)
                    {
                        var currentGrant = await grantOwner.GetCurrentAsync(
                            staleGrantReference, CancellationToken.None).ConfigureAwait(false);
                        if (currentGrant.Status is not (
                                ExecutableActionGrantLookupStatus.Current or
                                ExecutableActionGrantLookupStatus.Error or
                                ExecutableActionGrantLookupStatus.Expired) &&
                            await sourceControlOwner.RecordStaleFenceMergeConflictAsync(
                                actor,
                                identity,
                                selection,
                                intent,
                                CancellationToken.None).ConfigureAwait(false))
                            return new SourceControlMergeExecutionResult(
                                "conflict", null, "source_control_run_binding_changed");
                    }
                }

                throw;
            }
            if (lockedIntent.State == "merged")
                return new SourceControlMergeExecutionResult("merged", lockedIntent.MergeSha, null);
            await RequireCurrentMergeAuthorityAsync(
                context,
                actor,
                identity,
                selection,
                lockedIntent,
                projects,
                decisions,
                grantOwner,
                cancellationToken).ConfigureAwait(false);
            if (lockedIntent.State == "merge_started")
            {
                await sourceControlOwner.MarkInterruptedMergeUncertainAsync(
                    identity, lockedIntent.IntentId, cancellationToken).ConfigureAwait(false);
                return new SourceControlMergeExecutionResult(
                    "outcome_uncertain", null, "process_interrupted");
            }
            if (lockedIntent.State != "approved")
                throw new CoordinationException(
                    "source_control_intent_not_current", StatusCodes.Status409Conflict);

            var settings = SourceControlProjectConfigurationResolver.Resolve(selection.Selection.Snapshot);
            EnsurePinnedConfiguration(lockedIntent.Pin, settings);

            var result = await redemption.WithCredentialAsync(
                context,
                lockedIntent.Pin,
                lockedIntent.Pin.ApiCredential,
                async (credential, token) =>
                {
                    await RequireCurrentMergeAuthorityAsync(
                        context,
                        actor,
                        identity,
                        selection,
                        lockedIntent,
                        projects,
                        decisions,
                        grantOwner,
                        token).ConfigureAwait(false);
                    var operationContext = new SourceControlOperationContext(
                        lockedIntent.Pin.ProviderBinding,
                        lockedIntent.Pin.Repository,
                        credential);
                    await adapter.VerifyCurrentBindingAsync(
                        operationContext, SourceControlCapabilities.Merge, token).ConfigureAwait(false);
                    await RequireCurrentMergeAuthorityAsync(
                        context,
                        actor,
                        identity,
                        selection,
                        lockedIntent,
                        projects,
                        decisions,
                        grantOwner,
                        token).ConfigureAwait(false);

                    var readiness = await adapter.ReadMergeReadinessAsync(
                        operationContext,
                        new SourceControlMergeReadinessRequest(
                            lockedIntent.PullRequestNumber,
                            lockedIntent.HeadBranch,
                            lockedIntent.HeadSha,
                            lockedIntent.BaseBranch,
                            lockedIntent.BaseSha),
                        token).ConfigureAwait(false);
                    await RequireCurrentMergeAuthorityAsync(
                        context,
                        actor,
                        identity,
                        selection,
                        lockedIntent,
                        projects,
                        decisions,
                        grantOwner,
                        token).ConfigureAwait(false);
                    if (!readiness.IsReady)
                        throw new CoordinationException(
                            "source_control_merge_preflight_failed", StatusCodes.Status409Conflict);

                    await sourceControlOwner.MarkMergeStartedAsync(
                        identity, lockedIntent, grantReference, token).ConfigureAwait(false);
                    mergeStarted = true;
                    try
                    {
                        await RequireCurrentMergeAuthorityAsync(
                            context,
                            actor,
                            identity,
                            selection,
                            lockedIntent with { State = "merge_started" },
                            projects,
                            decisions,
                            grantOwner,
                            token).ConfigureAwait(false);
                    }
                    catch (CoordinationException exception)
                    {
                        await sourceControlOwner.RecordMergeOutcomeAsync(
                            identity,
                            lockedIntent.IntentId,
                            "conflict",
                            mergeSha: null,
                            failureCode: exception.Code,
                            CancellationToken.None).ConfigureAwait(false);
                        return new SourceControlMergeExecutionResult(
                            "conflict", null, exception.Code);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        await sourceControlOwner.RecordMergeOutcomeAsync(
                            identity,
                            lockedIntent.IntentId,
                            "conflict",
                            mergeSha: null,
                            failureCode: "cancelled_before_merge",
                            CancellationToken.None).ConfigureAwait(false);
                        throw;
                    }

                    SourceControlMergeOutcome outcome;
                    try
                    {
                        outcome = await adapter.MergePullRequestAsync(
                            operationContext,
                            new SourceControlMergeRequest(
                                lockedIntent.PullRequestNumber,
                                lockedIntent.HeadSha,
                                lockedIntent.BaseSha,
                                lockedIntent.Method),
                            token).ConfigureAwait(false);
                    }
                    catch (SourceControlOperationException exception)
                    {
                        var authorityFailure = await GetMergeAuthorityFailureAsync(
                            context,
                            actor,
                            identity,
                            selection,
                            lockedIntent with { State = "merge_started" },
                            projects,
                            decisions,
                            grantOwner,
                            token).ConfigureAwait(false);
                        var state = exception.Code == SourceControlFailureCode.RemoteOutcomeUncertain
                            ? "outcome_uncertain"
                            : "conflict";
                        await sourceControlOwner.RecordMergeOutcomeAsync(
                            identity,
                            lockedIntent.IntentId,
                            state,
                            mergeSha: null,
                            failureCode: authorityFailure?.Code ?? exception.Code.ToString(),
                            token).ConfigureAwait(false);
                        return new SourceControlMergeExecutionResult(
                            state, null, authorityFailure?.Code ?? exception.Code.ToString());
                    }

                    CoordinationException? postMergeAuthorityFailure;
                    var authorityObservationTimedOut = false;
                    using (var authoritySettlement = new CancellationTokenSource(MergeSettlementTimeout))
                    {
                        try
                        {
                            postMergeAuthorityFailure = await GetMergeAuthorityFailureAsync(
                                context,
                                actor,
                                identity,
                                selection,
                                lockedIntent with { State = "merge_started" },
                                projects,
                                decisions,
                                grantOwner,
                                authoritySettlement.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (authoritySettlement.IsCancellationRequested)
                        {
                            postMergeAuthorityFailure = null;
                            authorityObservationTimedOut = true;
                        }
                    }

                    using var durableSettlement = new CancellationTokenSource(MergeSettlementTimeout);
                    await sourceControlOwner.RecordMergeOutcomeAsync(
                        identity,
                        lockedIntent.IntentId,
                        "merged",
                        outcome.MergeSha,
                        failureCode: postMergeAuthorityFailure?.Code ??
                                     (authorityObservationTimedOut
                                         ? "post_merge_authority_observation_timeout"
                                         : null),
                        durableSettlement.Token).ConfigureAwait(false);
                    return new SourceControlMergeExecutionResult(
                        "merged", outcome.MergeSha, postMergeAuthorityFailure?.Code);
                },
                cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (CoordinationException exception) when (
            !mergeStarted && IsTerminalPreEffectConflict(exception))
        {
            await sourceControlOwner.RecordPreEffectMergeConflictAsync(
                identity, intent.IntentId, exception.Code, CancellationToken.None).ConfigureAwait(false);
            return new SourceControlMergeExecutionResult("conflict", null, exception.Code);
        }
        catch (OperationCanceledException) when (!mergeStarted && cancellationToken.IsCancellationRequested)
        {
            await sourceControlOwner.RecordPreEffectMergeConflictAsync(
                identity, intent.IntentId, "cancelled_before_merge", CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<IResult> ResolveInterruptedMergeAsync(
        HttpContext context,
        CoordinationActor actor,
        SessionIdentity identity,
        AuthorizedRunSelection selection,
        SourceControlMergeIntentSnapshot intent,
        ProjectsRunSelectionClient projects,
        CoordinatorDecisionOwnerStore decisions,
        SourceControlOwnerStore sourceControlOwner,
        IExecutableActionGrantOwnerLookup grantOwner,
        CancellationToken cancellationToken)
    {
        await using var repositoryLock = await sourceControlOwner.AcquireRepositoryMergeLockAsync(
            intent.Pin.Repository, cancellationToken).ConfigureAwait(false);
        var current = await sourceControlOwner.ReadMergeIntentAsync(
            actor, identity, selection, intent.IntentId, cancellationToken).ConfigureAwait(false);
        if (current.State == "merged")
            return Results.Ok(ToView(current));
        await RequireCurrentMergeAuthorityAsync(
            context,
            actor,
            identity,
            selection,
            current,
            projects,
            decisions,
            grantOwner,
            cancellationToken).ConfigureAwait(false);
        if (current.State != "merge_started")
            return Results.Ok(ToView(current));
        await sourceControlOwner.MarkInterruptedMergeUncertainAsync(
            identity, current.IntentId, cancellationToken).ConfigureAwait(false);
        return Results.Json(
            new { error = "source_control_merge_outcome_uncertain" },
            statusCode: StatusCodes.Status502BadGateway);
    }

    private static async Task RequireCurrentMergeAuthorityAsync(
        HttpContext context,
        CoordinationActor actor,
        SessionIdentity identity,
        AuthorizedRunSelection selection,
        SourceControlMergeIntentSnapshot intent,
        ProjectsRunSelectionClient projects,
        CoordinatorDecisionOwnerStore decisions,
        IExecutableActionGrantOwnerLookup grantOwner,
        CancellationToken cancellationToken)
    {
        await RequireUnchangedAuthorizedSelectionAsync(
            context,
            identity.ProjectId,
            identity.RunId,
            selection,
            projects,
            cancellationToken).ConfigureAwait(false);
        var current = await decisions.ReadCurrentAsync(
            actor, identity, selection, cancellationToken).ConfigureAwait(false);
        if (current.State.Fence != intent.AcceptedRun.Fence)
            throw new CoordinationException(
                "source_control_run_binding_changed", StatusCodes.Status409Conflict);
        var receipt = intent.ApprovalReceipt;
        if (intent.ApprovalDecisionId is null ||
            intent.ApprovalStateVersion is null ||
            current.StateVersion != intent.ApprovalStateVersion.Value ||
            current.SelectionHash != intent.AcceptedRun.AcceptedSelectionHash ||
            current.State.PendingGate is not null ||
            receipt is null ||
            receipt.Kind != CoordinatorGateKind.Approval ||
            receipt.RequestId != intent.ApprovalRequestId ||
            receipt.SubjectId != intent.IntentId ||
            receipt.ActorId != intent.AcceptedRun.Subject ||
            receipt.Fence != intent.AcceptedRun.Fence ||
            receipt.ChoiceId != CoordinatorGateChoices.Approve ||
            receipt.FreeformAnswer is not null ||
            current.State.DecisionReceipts.IsDefaultOrEmpty ||
            !current.State.DecisionReceipts.Contains(receipt))
            throw new CoordinationException(
                "source_control_merge_approval_not_current", StatusCodes.Status409Conflict);

        if (intent.GrantReference is null)
            throw new CoordinationException(
                "source_control_merge_grant_not_current", StatusCodes.Status403Forbidden);
        await RequireCurrentMergeGrantAsync(
            grantOwner, intent.GrantReference, intent, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<CoordinationException?> GetMergeAuthorityFailureAsync(
        HttpContext context,
        CoordinationActor actor,
        SessionIdentity identity,
        AuthorizedRunSelection selection,
        SourceControlMergeIntentSnapshot intent,
        ProjectsRunSelectionClient projects,
        CoordinatorDecisionOwnerStore decisions,
        IExecutableActionGrantOwnerLookup grantOwner,
        CancellationToken cancellationToken)
    {
        try
        {
            await RequireCurrentMergeAuthorityAsync(
                context,
                actor,
                identity,
                selection,
                intent,
                projects,
                decisions,
                grantOwner,
                cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (CoordinationException exception)
        {
            return exception;
        }
    }

    private static bool IsTerminalPreEffectConflict(CoordinationException exception) =>
        exception.StatusCode is StatusCodes.Status403Forbidden or StatusCodes.Status409Conflict;

    private static async Task RequireCurrentMergeGrantAsync(
        IExecutableActionGrantOwnerLookup grantOwner,
        ExecutableActionGrantReference reference,
        SourceControlMergeIntentSnapshot intent,
        CancellationToken cancellationToken)
    {
        var lookup = await grantOwner.GetCurrentAsync(reference, cancellationToken).ConfigureAwait(false);
        var grant = lookup.Grant;
        if (lookup.Status != ExecutableActionGrantLookupStatus.Current ||
            grant is null ||
            grant.State != ExecutableActionGrantState.Active ||
            grant.Issuer != intent.AcceptedRun.Issuer ||
            grant.ActorId != intent.AcceptedRun.Subject ||
            grant.TenantId != intent.AcceptedRun.TenantId ||
            grant.ProjectId != intent.AcceptedRun.ProjectId ||
            grant.RunId != intent.AcceptedRun.RunId ||
            grant.SessionId != intent.AcceptedRun.RootSessionId ||
            grant.StepId != intent.WorkflowStepId ||
            grant.Purpose != SourceControlMergeGrantProducer.Purpose ||
            grant.Fence != intent.AcceptedRun.Fence ||
            grant.ActionIds.Count != 1 ||
            !grant.ActionIds.Contains(SourceControlMergeGrantProducer.ActionId))
            throw new CoordinationException(
                "source_control_merge_grant_not_current", StatusCodes.Status403Forbidden);
    }

    internal static async Task<PinnedProviderBinding> ResolvePolicyBindingAsync(
        System.Text.Json.JsonElement acceptedSelectionSnapshot,
        string runId,
        ProviderCatalog catalog,
        ProviderResolver resolver,
        AgtPolicyProvider policyProvider,
        AgtPolicyProviderOptions policyOptions,
        CancellationToken cancellationToken)
    {
        if (acceptedSelectionSnapshot.ValueKind != System.Text.Json.JsonValueKind.Object ||
            !acceptedSelectionSnapshot.TryGetProperty("providers", out var providersElement) ||
            providersElement.ValueKind != System.Text.Json.JsonValueKind.Array)
            throw new CoordinationException(
                "source_control_policy_selection_missing", StatusCodes.Status503ServiceUnavailable);

        ImmutableArray<EffectiveProviderSelection> selections;
        try
        {
            selections = JsonSerializer.Deserialize<ImmutableArray<EffectiveProviderSelection>>(
                providersElement.GetRawText(), ProviderSelectionJsonOptions);
        }
        catch (JsonException exception)
        {
            throw new CoordinationException(
                "source_control_policy_selection_invalid", StatusCodes.Status503ServiceUnavailable, exception);
        }
        var selected = selections
            .Where(item => item?.Seam == ProviderSeam.Policy)
            .ToArray();
        if (selected.Length != 1 ||
            selected[0].Cardinality != ProviderCardinality.PlatformSingleton ||
            selected[0].MeterSource is not null ||
            selected[0].Candidates.IsDefault ||
            selected[0].Candidates.Length != 1)
            throw new CoordinationException(
                "source_control_policy_selection_invalid", StatusCodes.Status503ServiceUnavailable);

        var effective = selected[0].Candidates[0];
        if (effective.ProviderId != AgtPolicyProvider.ProviderId ||
            effective.AdapterVersion != AgtPolicyProvider.AdapterVersion.ToString() ||
            effective.OptionsSchemaVersion != policyOptions.OptionsSchemaVersion ||
            effective.OptionsRevision != policyOptions.OptionsRevision ||
            !effective.AdvertisedCapabilities.ToImmutableHashSet(StringComparer.Ordinal)
                .SetEquals(PolicyProviderCapabilities.All))
            throw new CoordinationException(
                "source_control_policy_selection_mismatch", StatusCodes.Status503ServiceUnavailable);

        catalog.TryGetDefault(ProviderSeam.Policy, out var defaultPolicyId);
        if (!string.Equals(defaultPolicyId, effective.ProviderId, StringComparison.Ordinal))
            throw new CoordinationException(
                "source_control_policy_provider_unavailable", StatusCodes.Status503ServiceUnavailable);
        var resolution = resolver.Resolve(new ProviderResolutionRequest(
            ProviderSeam.Policy,
            ProjectOverrideId: null,
            AgtPolicyProvider.AdapterVersion,
            policyOptions.OptionsSchemaVersion,
            effective.RequiredCapabilities.ToImmutableHashSet(StringComparer.Ordinal)));
        if (!resolution.IsSuccess || resolution.Value?.Candidate is not { } candidate ||
            candidate.OptionsRevision != effective.OptionsRevision ||
            candidate.AdapterVersion.ToString() != effective.AdapterVersion)
            throw new CoordinationException(
                "source_control_policy_provider_unavailable", StatusCodes.Status503ServiceUnavailable);

        var negotiation = await policyProvider.NegotiateAsync(
            candidate, policyOptions, cancellationToken).ConfigureAwait(false);
        if (!negotiation.IsSuccess || negotiation.Value is null)
            throw new CoordinationException(
                "source_control_policy_provider_unavailable", StatusCodes.Status503ServiceUnavailable);
        var pinned = resolver.Pin(
            runId,
            candidate,
            policyOptions.ResourceId,
            negotiation.Value);
        if (!pinned.IsSuccess || pinned.Value is null)
            throw new CoordinationException(
                "source_control_policy_provider_unavailable", StatusCodes.Status503ServiceUnavailable);
        return pinned.Value;
    }

    private static Task<IResult> ReadMergeIntentAsync(
        string projectId,
        string runId,
        string sessionId,
        string intentId,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        SourceControlOwnerStore sourceControlOwner,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var actor = RequireOwnerActor(context, options, projectId, runId);
            var identity = new SessionIdentity(projectId, runId, sessionId);
            var selection = await projects.ReadAcceptedSelectionWithAuthorityAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            var intent = await sourceControlOwner.ReadMergeIntentAsync(
                actor, identity, selection, intentId, cancellationToken).ConfigureAwait(false);
            return Results.Ok(ToView(intent));
        }, cancellationToken);

    private static Task<IResult> RejectDirectGitHubWebhookAsync(
        HttpContext context,
        CancellationToken cancellationToken) =>
        ExecuteAsync(() =>
        {
            context.Response.Headers.CacheControl = "no-store";
            _ = cancellationToken;
            return Task.FromResult<IResult>(
                Results.Json(new { error = "authenticated_relay_required" },
                    statusCode: StatusCodes.Status403Forbidden));
        }, cancellationToken);

    private static Task<IResult> ReceiveRelayedGitHubWebhookAsync(
        string projectId,
        string runId,
        string sessionId,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinatorDecisionOwnerStore decisions,
        SourceControlOwnerStore sourceControlOwner,
        SourceControlSecretRedemptionClient redemption,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var actor = RequireOwnerActor(context, options, projectId, runId);
            var identity = new SessionIdentity(projectId, runId, sessionId);
            var deliveryId = context.Request.Headers["X-GitHub-Delivery"].ToString();
            var eventName = context.Request.Headers["X-GitHub-Event"].ToString();
            var signature = context.Request.Headers["X-Hub-Signature-256"].ToString();
            if (!Guid.TryParse(deliveryId, out _) ||
                eventName.Length is 0 or > 64 ||
                eventName.Any(character =>
                    !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-')))
                throw new CoordinationException(
                    "source_control_webhook_headers_invalid", StatusCodes.Status400BadRequest);
            var rawBody = await ReadWebhookBodyAsync(context.Request, cancellationToken).ConfigureAwait(false);

            var selection = await projects.ReadAcceptedSelectionWithAuthorityAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            var initialDecision = await decisions.ReadCurrentAsync(
                actor, identity, selection, cancellationToken).ConfigureAwait(false);
            var settings = SourceControlProjectConfigurationResolver.Resolve(selection.Selection.Snapshot);
            var pin = await sourceControlOwner.ReadRepositoryPinAsync(
                actor, identity, selection, cancellationToken).ConfigureAwait(false);
            var webhookCredential = pin.WebhookCredential;
            EnsurePinnedConfiguration(pin, settings);
            if (settings.WebhookSecretReference is null ||
                webhookCredential is null ||
                webhookCredential.Purpose != SourceControlSecretPurposes.Webhook)
                throw new CoordinationException(
                    "source_control_webhook_not_configured", StatusCodes.Status409Conflict);

            var delivery = await redemption.WithCredentialAsync(
                context,
                pin.AcceptedRun,
                webhookCredential,
                async (credential, token) =>
                {
                    if (!GitHubWebhookSignatureVerifier.VerifyRawBody(rawBody, signature, credential))
                        throw new CoordinationException(
                            "source_control_webhook_signature_invalid", StatusCodes.Status401Unauthorized);

                    GitHubWebhookEnvelope envelope;
                    try
                    {
                        envelope = GitHubWebhookSignatureVerifier.ParseVerifiedPayload(
                            rawBody,
                            deliveryId,
                            eventName,
                            pin.Repository,
                            pin.ProviderRepositoryId);
                    }
                    catch (SourceControlOperationException exception)
                        when (exception.Code == SourceControlFailureCode.InvalidBinding)
                    {
                        throw new CoordinationException(
                            "source_control_webhook_repository_mismatch", StatusCodes.Status409Conflict, exception);
                    }
                    catch (SourceControlOperationException exception)
                    {
                        throw new CoordinationException(
                            "source_control_webhook_payload_invalid", StatusCodes.Status400BadRequest, exception);
                    }

                    await RequireUnchangedAuthorizedSelectionAsync(
                        context, projectId, runId, selection, projects, token).ConfigureAwait(false);
                    var latestDecision = await decisions.ReadCurrentAsync(
                        actor, identity, selection, token).ConfigureAwait(false);
                    if (latestDecision.StateVersion != initialDecision.StateVersion ||
                        latestDecision.State.Fence != initialDecision.State.Fence)
                        throw new CoordinationException(
                            "source_control_webhook_authority_changed", StatusCodes.Status409Conflict);

                    var payloadHash = Convert.ToHexString(SHA256.HashData(rawBody)).ToLowerInvariant();
                    var duplicate = await sourceControlOwner.RecordWebhookDeliveryAsync(
                        actor,
                        identity,
                        selection,
                        pin,
                        envelope,
                        payloadHash,
                        initialDecision.StateVersion,
                        token).ConfigureAwait(false);
                    return (Envelope: envelope, Duplicate: duplicate);
                },
                cancellationToken).ConfigureAwait(false);

            return delivery.Duplicate
                ? Results.Ok(new SourceControlWebhookDeliveryView(
                    delivery.Envelope.DeliveryId, "duplicate"))
                : Results.Accepted(value: new SourceControlWebhookDeliveryView(
                    delivery.Envelope.DeliveryId, "accepted"));
        }, cancellationToken);

    private static async Task<byte[]> ReadWebhookBodyAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        const int maximumBodyBytes = 1024 * 1024;
        if (request.ContentLength is > maximumBodyBytes)
            throw new CoordinationException(
                "source_control_webhook_body_too_large", StatusCodes.Status413PayloadTooLarge);

        await using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (buffer.Length + read > maximumBodyBytes)
                throw new CoordinationException(
                    "source_control_webhook_body_too_large", StatusCodes.Status413PayloadTooLarge);
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return buffer.ToArray();
    }

    private static async Task<SourceControlRunContext> ReadPinnedSourceControlRunAsync(
        HttpContext context,
        string projectId,
        string runId,
        string sessionId,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinatorDecisionOwnerStore decisions,
        SourceControlOwnerStore sourceControlOwner,
        CancellationToken cancellationToken)
    {
        var actor = RequireOwnerActor(context, options, projectId, runId);
        var identity = new SessionIdentity(projectId, runId, sessionId);
        var selection = await projects.ReadAcceptedSelectionWithAuthorityAsync(
            context, projectId, runId, cancellationToken).ConfigureAwait(false);
        var current = await decisions.ReadCurrentAsync(
            actor, identity, selection, cancellationToken).ConfigureAwait(false);
        var pin = await sourceControlOwner.ReadRepositoryPinAsync(
            actor, identity, selection, cancellationToken).ConfigureAwait(false);
        var settings = SourceControlProjectConfigurationResolver.Resolve(selection.Selection.Snapshot);
        EnsurePinnedConfiguration(pin, settings);
        if (pin.AcceptedRun.Fence != current.State.Fence)
            throw new CoordinationException(
                "source_control_run_binding_changed", StatusCodes.Status409Conflict);
        return new SourceControlRunContext(
            actor, identity, selection, pin, current.StateVersion);
    }

    private static async Task RequireCurrentSourceControlRunAsync(
        HttpContext context,
        SourceControlRunContext run,
        ProjectsRunSelectionClient projects,
        CoordinatorDecisionOwnerStore decisions,
        CancellationToken cancellationToken)
    {
        await RequireUnchangedAuthorizedSelectionAsync(
            context,
            run.Identity.ProjectId,
            run.Identity.RunId,
            run.Selection,
            projects,
            cancellationToken).ConfigureAwait(false);
        var current = await decisions.ReadCurrentAsync(
            run.Actor, run.Identity, run.Selection, cancellationToken).ConfigureAwait(false);
        if (current.StateVersion != run.DecisionStateVersion ||
            current.State.Fence != run.Pin.AcceptedRun.Fence)
            throw new CoordinationException(
                "source_control_run_binding_changed", StatusCodes.Status409Conflict);
    }

    private static void EnsureAdapterMatchesPin(
        ISourceControlAdapter adapter,
        SourceControlRepositoryPin pin)
    {
        if (adapter.ProviderId != pin.ProviderBinding.ProviderId ||
            pin.ProviderBinding.Seam != ProviderSeam.SourceControl ||
            pin.ProviderBinding.Resource.Seam != ProviderSeam.SourceControl)
            throw new SourceControlOperationException(
                SourceControlFailureCode.CapabilityUnavailable,
                "The pinned SourceControl provider adapter is not registered.");
    }

    private static void EnsureCredentialPurpose(
        SourceControlCredentialReference credential,
        string expectedPurpose)
    {
        if (credential.Purpose != expectedPurpose)
            throw new CoordinationException(
                "source_control_credential_purpose_invalid", StatusCodes.Status409Conflict);
    }

    private static void EnsureApiCredential(SourceControlRepositoryPin pin)
    {
        if (pin.GitHubAppBinding is not null)
        {
            if (pin.ApiCredential is not null || pin.CheckoutCredential is not null)
                throw new CoordinationException(
                    "source_control_credential_binding_invalid", StatusCodes.Status409Conflict);
            return;
        }

        var credential = pin.ApiCredential
            ?? throw new CoordinationException(
                "source_control_api_credential_missing", StatusCodes.Status409Conflict);
        EnsureCredentialPurpose(credential, SourceControlSecretPurposes.Api);
    }

    private static void EnsureCheckoutCapability(SourceControlRepositoryPin pin)
    {
        if (!pin.ProviderBinding.NegotiatedCapabilities.Contains(
                SourceControlCapabilities.RepositoryCheckout))
            throw new SourceControlOperationException(
                SourceControlFailureCode.CapabilityUnavailable,
                "The pinned SourceControl provider does not support repository checkout.");
    }

    private static void EnsureIssueWriteCapability(SourceControlRepositoryPin pin)
    {
        if (!pin.ProviderBinding.NegotiatedCapabilities.Contains(
                SourceControlCapabilities.IssueWrite) ||
            pin.GitHubAppBinding is { IssueWriteGranted: false })
            throw new SourceControlOperationException(
                SourceControlFailureCode.CapabilityUnavailable,
                "The pinned SourceControl provider does not support issue creation.");
    }

    private static void ValidateIssueRequest(SourceControlIssueRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Title) ||
            request.Title.Length > 256 ||
            request.Body?.Length > 60_000)
            throw new CoordinationException(
                "source_control_issue_request_invalid", StatusCodes.Status400BadRequest);
    }

    private static void ValidatePullRequestRequest(SourceControlPullRequestRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Title) ||
            request.Title.Length > 256 ||
            request.Body?.Length > 60_000 ||
            !IsValidGitBranchName(request.HeadBranch) ||
            !IsValidGitBranchName(request.BaseBranch) ||
            !IsGitSha(request.ExpectedHeadSha) ||
            !IsGitSha(request.ExpectedBaseSha))
            throw new CoordinationException(
                "source_control_pull_request_request_invalid", StatusCodes.Status400BadRequest);
    }

    private static void ValidateWorkspaceRequest(string workspaceId, string baseSha, string branchName)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) ||
            workspaceId.Length > 128 ||
            workspaceId.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')) ||
            !IsGitSha(baseSha) ||
            !IsValidGitBranchName(branchName))
            throw new CoordinationException(
                "source_control_workspace_request_invalid", StatusCodes.Status400BadRequest);
    }

    private static bool IsValidGitBranchName(string? branch) =>
        !string.IsNullOrWhiteSpace(branch) &&
        branch.Length <= 255 &&
        branch[0] != '-' &&
        branch.Split('/').All(segment =>
            segment.Length > 0 &&
            segment is not ("." or "..") &&
            !segment.StartsWith(".", StringComparison.Ordinal) &&
            !segment.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) &&
            segment.All(character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '-' or '_' or '.')) &&
        !branch.Contains("..", StringComparison.Ordinal);

    private static bool IsGitSha(string? value) =>
        value is { Length: 40 } && value.All(Uri.IsHexDigit);

    private static SourceControlAcceptedRunBinding CreateAcceptedRun(
        CoordinationActor actor,
        SessionIdentity identity,
        AuthorizedRunSelection selection,
        long fence)
    {
        var snapshot = selection.Selection;
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(snapshot.Snapshot.GetRawText())));
        return new SourceControlAcceptedRunBinding(
            actor.Issuer,
            actor.Subject,
            selection.Authorization.TenantId,
            identity.ProjectId,
            identity.RunId,
            identity.SessionId,
            hash,
            snapshot.ProjectRevision,
            snapshot.ProjectConfigurationRevision,
            snapshot.PlatformRuntimeRevision,
            snapshot.ContextRevision,
            fence);
    }

    private static async Task RequireUnchangedAuthorizedSelectionAsync(
        HttpContext context,
        string projectId,
        string runId,
        AuthorizedRunSelection initial,
        ProjectsRunSelectionClient projects,
        CancellationToken cancellationToken)
    {
        var refreshed = await projects.ReadAcceptedSelectionWithAuthorityAsync(
            context, projectId, runId, cancellationToken).ConfigureAwait(false);
        if (refreshed.Authorization.ContractVersion != initial.Authorization.ContractVersion ||
            refreshed.Authorization.Issuer != initial.Authorization.Issuer ||
            refreshed.Authorization.ActorId != initial.Authorization.ActorId ||
            refreshed.Authorization.TenantId != initial.Authorization.TenantId ||
            refreshed.Authorization.BoundProjectId != initial.Authorization.BoundProjectId ||
            refreshed.Authorization.BoundRunId != initial.Authorization.BoundRunId ||
            refreshed.Authorization.MembershipRevision != initial.Authorization.MembershipRevision ||
            ProjectRoleRevision(refreshed, projectId) != ProjectRoleRevision(initial, projectId) ||
            refreshed.Selection.ProjectRevision != initial.Selection.ProjectRevision ||
            refreshed.Selection.ProjectConfigurationRevision != initial.Selection.ProjectConfigurationRevision ||
            refreshed.Selection.PlatformRuntimeRevision != initial.Selection.PlatformRuntimeRevision ||
            !string.Equals(
                refreshed.Selection.ContextRevision, initial.Selection.ContextRevision, StringComparison.Ordinal) ||
            !string.Equals(
                refreshed.Selection.Snapshot.GetRawText(),
                initial.Selection.Snapshot.GetRawText(),
                StringComparison.Ordinal))
            throw new CoordinationException(
                "coordinator_selection_stale", StatusCodes.Status409Conflict);
    }

    private static long ProjectRoleRevision(AuthorizedRunSelection selection, string projectId)
    {
        var revisions = selection.Authorization.EffectiveAuthority
            .Where(entry => entry.ResourceType == "project" && entry.ResourceId == projectId)
            .SelectMany(entry => entry.Permissions)
            .Where(permission => permission.Permission == "acceptRunSelection")
            .Select(permission => permission.RoleRevision)
            .Distinct()
            .ToArray();
        if (revisions.Length != 1 || revisions[0] < 1)
            throw new CoordinationException(
                "source_control_authority_unavailable", StatusCodes.Status403Forbidden);
        return revisions[0];
    }

    private static CoordinationActor RequireOwnerActor(
        HttpContext context,
        OrchestratorOptions options,
        string projectId,
        string runId)
    {
        var actor = CoordinationIdentity.RequireActor(context.User, options.Issuer);
        CoordinationIdentity.RequireScopes(context.User);
        var scope = CoordinationIdentity.RequireRunScope(context.User);
        if (scope.ProjectId != projectId || scope.RunId != runId ||
            !CoordinationIdentity.HasAudience(context.User, options.Audience))
            throw new CoordinationException("caller_binding_mismatch", StatusCodes.Status403Forbidden);
        return actor;
    }

    private static SourceControlMergeIntentView ToView(SourceControlMergeIntentSnapshot intent) =>
        new(
            intent.IntentId,
            intent.ApprovalRequestId,
            intent.State,
            intent.Pin.Repository.FullName,
            intent.PullRequestNumber,
            intent.HeadBranch,
            intent.HeadSha,
            intent.BaseBranch,
            intent.BaseSha,
            intent.Method,
            intent.ApprovalDecisionId,
            intent.ApprovalStateVersion,
            intent.GrantReference,
            intent.MergeSha,
            intent.LastFailureCode);

    private static SourceControlRepositoryPinView ToPinView(SourceControlRepositoryPin pin) =>
        new(
            pin.PinId,
            pin.Repository.FullName,
            pin.ProviderBinding.ProviderId,
            pin.ProviderBinding.Resource.ResourceId,
            pin.ProviderBinding.Resource.Generation,
            pin.ProviderRepositoryId,
            pin.DefaultBranch,
            pin.IsPrivate,
            pin.PinnedAt);

    private static void EnsurePinnedConfiguration(
        SourceControlRepositoryPin pin,
        SourceControlProjectSettings settings)
    {
        var appBindingMatches = pin.GitHubAppBinding is { } appBinding &&
            settings.AuthMode == SourceControlAuthMode.GitHubApp &&
            settings.IdentityConnectionId == appBinding.IdentityConnectionId;
        var secretBindingMatches = pin.GitHubAppBinding is null &&
            settings.AuthMode != SourceControlAuthMode.GitHubApp &&
            SameSecretReference(settings.ApiSecretReference, pin.ApiCredential?.Secret);
        if (settings.Repository != pin.Repository ||
            !(appBindingMatches || secretBindingMatches) ||
            !SameSecretReference(settings.CheckoutSecretReference, pin.CheckoutCredential?.Secret) ||
            !SameSecretReference(settings.WebhookSecretReference, pin.WebhookCredential?.Secret))
            throw new CoordinationException(
                "source_control_accepted_configuration_changed", StatusCodes.Status409Conflict);
    }

    private static SecretRef RequireSecretApiReference(SourceControlProjectSettings settings) =>
        settings.ApiSecretReference
        ?? throw new CoordinationException(
            "source_control_github_app_selection_required", StatusCodes.Status409Conflict);

    private static bool IsExactIdentityRepositorySelectionCode(string selectionCode) =>
        selectionCode.Length == 64 &&
        selectionCode.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string HashIdentityRepositorySelectionCode(string selectionCode) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.ASCII.GetBytes(selectionCode)));

    private static bool SameSecretReference(SecretRef? left, SecretRef? right) =>
        left is null
            ? right is null
            : right is not null && left.Id == right.Id && left.Version == right.Version;

    private static void ValidateRequest(PrepareSourceControlMergeIntentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) ||
            request.IdempotencyKey.Length > 128 ||
            !request.IdempotencyKey.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':') ||
            request.PullRequestNumber <= 0 ||
            !Enum.IsDefined(request.Method))
            throw new CoordinationException(
                "source_control_merge_request_invalid", StatusCodes.Status400BadRequest);
    }

    private static async Task<IResult> ExecuteAsync(
        Func<Task<IResult>> action,
        CancellationToken cancellationToken)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CoordinationException exception)
        {
            return Results.Json(new { error = exception.Code }, statusCode: exception.StatusCode);
        }
        catch (SourceControlOperationException exception)
        {
            var status = exception.Code switch
            {
                SourceControlFailureCode.InvalidRequest => StatusCodes.Status400BadRequest,
                SourceControlFailureCode.NotFound => StatusCodes.Status404NotFound,
                SourceControlFailureCode.PermissionDenied => StatusCodes.Status403Forbidden,
                SourceControlFailureCode.Conflict or SourceControlFailureCode.StaleRevision or
                    SourceControlFailureCode.PullRequestMismatch or SourceControlFailureCode.InvalidBinding =>
                    StatusCodes.Status409Conflict,
                SourceControlFailureCode.RateLimited => StatusCodes.Status429TooManyRequests,
                SourceControlFailureCode.RemoteOutcomeUncertain or SourceControlFailureCode.RemoteUnavailable or
                    SourceControlFailureCode.InvalidResponse or SourceControlFailureCode.CapabilityUnavailable =>
                    StatusCodes.Status503ServiceUnavailable,
                _ => StatusCodes.Status502BadGateway
            };
            return Results.Json(
                new { error = "source_control_" + ToErrorSuffix(exception.Code) },
                statusCode: status);
        }
        catch (SourceControlProjectConfigurationException exception)
        {
            return Results.Json(
                new { error = exception.Failure == SourceControlProjectConfigurationFailure.Missing
                    ? "source_control_not_configured"
                    : "source_control_configuration_invalid" },
                statusCode: StatusCodes.Status409Conflict);
        }
        catch (SourceControlProviderSelectionException)
        {
            return Results.Json(
                new { error = "source_control_provider_unavailable" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (GitWorkspaceException exception)
        {
            var status = exception.Code switch
            {
                GitWorkspaceFailureCode.InvalidRequest => StatusCodes.Status400BadRequest,
                GitWorkspaceFailureCode.PathConflict or GitWorkspaceFailureCode.CorruptWorkspace =>
                    StatusCodes.Status409Conflict,
                GitWorkspaceFailureCode.WorkspaceUnavailable => StatusCodes.Status404NotFound,
                GitWorkspaceFailureCode.DiffTooLarge => StatusCodes.Status413PayloadTooLarge,
                GitWorkspaceFailureCode.CapabilityUnavailable or GitWorkspaceFailureCode.GitUnavailable =>
                    StatusCodes.Status503ServiceUnavailable,
                GitWorkspaceFailureCode.GitFailed => StatusCodes.Status502BadGateway,
                _ => StatusCodes.Status502BadGateway
            };
            return Results.Json(
                new { error = "source_control_workspace_" + exception.Code.ToString().ToLowerInvariant() },
                statusCode: status);
        }
        catch (ArgumentException)
        {
            return Results.Json(new { error = "invalid_request" }, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (System.Text.Json.JsonException)
        {
            return Results.Json(new { error = "invalid_json" }, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static string ToErrorSuffix(SourceControlFailureCode code) =>
        code switch
        {
            SourceControlFailureCode.InvalidBinding => "binding_invalid",
            SourceControlFailureCode.CapabilityUnavailable => "capability_unavailable",
            SourceControlFailureCode.InvalidRequest => "request_invalid",
            SourceControlFailureCode.NotFound => "not_found",
            SourceControlFailureCode.PermissionDenied => "permission_denied",
            SourceControlFailureCode.Conflict => "conflict",
            SourceControlFailureCode.StaleRevision => "stale_revision",
            SourceControlFailureCode.PullRequestMismatch => "pull_request_mismatch",
            SourceControlFailureCode.RemoteOutcomeUncertain => "outcome_uncertain",
            SourceControlFailureCode.RateLimited => "rate_limited",
            SourceControlFailureCode.RemoteUnavailable => "remote_unavailable",
            SourceControlFailureCode.InvalidResponse => "response_invalid",
            _ => "unavailable"
        };
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SourceControlPinRequest(string? SelectionCode);

internal sealed record PrepareSourceControlMergeIntentRequest(
    string IdempotencyKey,
    long ExpectedStateVersion,
    string WorkflowId,
    string DefinitionRevision,
    string WorkPlanId,
    string WorkflowStepId,
    long PullRequestNumber,
    SourceControlMergeMethod Method);

internal sealed record SourceControlRunContext(
    CoordinationActor Actor,
    SessionIdentity Identity,
    AuthorizedRunSelection Selection,
    SourceControlRepositoryPin Pin,
    long DecisionStateVersion);

internal sealed record PrepareSourceControlWorkspaceRequest(
    string WorkspaceId,
    string BaseSha,
    string BranchName);

internal sealed record PrepareSourceControlWorkspaceRevisionRequest(
    string BaseSha,
    string BranchName);

internal sealed record SourceControlWorkspaceView(
    string WorkspaceId,
    string WorkspacePath,
    string BaseSha,
    string BranchName);

internal sealed record SourceControlWorkspaceDiffView(
    string WorkspaceId,
    string BaseSha,
    string HeadSha,
    string Status,
    string Patch);

internal sealed record SourceControlOutputCaptureSummaryView(
    string CaptureId,
    Guid EventId,
    long EventPosition,
    DateTimeOffset CapturedAt,
    string WorkspaceId,
    string BaseSha,
    string OutputTreeSha,
    string ManifestSha256,
    long ManifestByteLength,
    string PatchSha256,
    long PatchByteLength,
    long PackageByteLength);

internal sealed record SourceControlOutputCapturePageView(
    ImmutableArray<SourceControlOutputCaptureSummaryView> Captures,
    DateTimeOffset? NextBeforeCapturedAt,
    string? NextBeforeCaptureId);

internal sealed record SourceControlOutputCaptureDetailView(
    SourceControlOutputCaptureSummaryView Capture,
    GitWorkspaceCapturedOutputManifest Manifest);

internal sealed record SourceControlOutputCaptureDiffView(
    SourceControlOutputCaptureSummaryView Capture,
    string Patch);

internal sealed record AuthorizedOutputCaptureScope(AuthorizedRunSelection Selection);

internal sealed record SourceControlMergeApprovalView(
    string IntentId,
    string ApprovalRequestId,
    string State,
    Guid DecisionId,
    long StateVersion,
    CoordinatorGateRequest? PendingGate);

internal sealed record SourceControlWebhookDeliveryView(
    string DeliveryId,
    string State);

internal sealed record SourceControlRepositoryPinView(
    string PinId,
    string Repository,
    string ProviderId,
    string ResourceId,
    long ResourceGeneration,
    long ProviderRepositoryId,
    string DefaultBranch,
    bool IsPrivate,
    DateTimeOffset PinnedAt);

internal sealed record SourceControlMergeIntentView(
    string IntentId,
    string ApprovalRequestId,
    string State,
    string Repository,
    long PullRequestNumber,
    string HeadBranch,
    string HeadSha,
    string BaseBranch,
    string BaseSha,
    SourceControlMergeMethod Method,
    Guid? ApprovalDecisionId,
    long? ApprovalStateVersion,
    ExecutableActionGrantReference? GrantReference,
    string? MergeSha,
    string? LastFailureCode);

internal sealed record SourceControlMergeExecutionResult(
    string State,
    string? MergeSha,
    string? FailureCode);

internal sealed record SourceControlMergeExecutionView(
    string IntentId,
    string State,
    string? MergeSha,
    string? FailureCode);
