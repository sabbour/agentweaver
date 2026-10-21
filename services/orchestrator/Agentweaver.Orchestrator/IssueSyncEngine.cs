using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator.Core;
using Agentweaver.Providers;
using Agentweaver.SourceControl;

namespace Agentweaver.Orchestrator;

internal sealed class IssueSyncEngine(IssueSyncStore store, BacklogOwnerStore backlog,
    IIssueSyncAuthority authority, ISourceControlAdapter source, ISourceControlIssueAdapter issues,
    ProviderResolver resolver, TimeProvider time)
{
    internal async Task CycleAsync(string projectId, CancellationToken stopping)
    {
        var connection = await store.TryLockAsync(projectId, stopping);
        if (connection is null)
            return;
        try
        {
            var project = await store.ReadAsync(projectId, stopping);
            if (project is null || !project.Settings.Enabled || project.NextCycleAt > time.GetUtcNow())
                return;
            project.Settings.Validate();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            deadline.CancelAfter(TimeSpan.FromSeconds(project.Settings.MaxCycleSeconds));
            var ct = deadline.Token;
            var page = project.ImportPage;
            var cursor = project.TaskCursor;
            SecretCredential? credential = null;
            try
            {
                await store.SetStatusAsync(project, "running", null, page, cursor, ct);
                var minted = await authority.MintAsync(project.Settings.GrantId, ct);
                RequireGrant(project, minted.Grant);
                if (string.IsNullOrWhiteSpace(minted.AccessToken) || minted.ExpiresAt <= time.GetUtcNow())
                    throw new CoordinationException("issue_sync_credential_invalid", 403);
                credential = new(minted.AccessToken, minted.ExpiresAt, time);
                var candidateResult = resolver.Resolve(new(ProviderSeam.SourceControl, null,
                    new Version(1, 0, 0), GitHubSourceControlAdapter.CurrentOptionsSchemaVersion,
                    ImmutableHashSet.Create(StringComparer.Ordinal,
                        SourceControlCapabilities.RepositoryRead, SourceControlCapabilities.IssueWrite)));
                if (!candidateResult.IsSuccess || candidateResult.Value?.Candidate is not { } candidate ||
                    candidate.ProviderId != source.ProviderId || source.ProviderId != SourceControlProviderIds.GitHub)
                    throw new CoordinationException("issue_sync_provider_unavailable", 409);
                var repository = project.GrantProof.Project.Repository;
                var negotiated = await source.NegotiateRepositoryAsync(candidate, repository, credential, ct);
                var pin = resolver.Pin("issue-sync:" + project.Settings.GrantId.ToString("N"), candidate,
                    negotiated.Resource.Resource.ResourceId, negotiated.Resource);
                if (!pin.IsSuccess)
                    throw new CoordinationException("issue_sync_provider_binding_invalid", 409);
                var context = new SourceControlOperationContext(pin.Value!, repository, credential);
                async Task Revalidate(CancellationToken token)
                {
                    var current = await store.ReadAsync(projectId, token);
                    if (current?.Revision != project.Revision || !current.Settings.Enabled ||
                        !credential.IsUsable())
                        throw new CoordinationException("issue_sync_settings_changed", 409);
                    RequireGrant(project, await authority.ValidateAsync(project.Settings.GrantId, token));
                }

                await Revalidate(ct);
                await source.VerifyCurrentBindingAsync(context, SourceControlCapabilities.IssueWrite, ct);
                var imported = await issues.ListIssuesAsync(context, page, project.Settings.MaxItems, ct);
                foreach (var remote in imported.Issues)
                {
                    var graph = await ReadGraph(projectId, ct);
                    if (graph.Tasks.Any(task => Matches(task.RepositoryIssue, repository, remote.Number)))
                        continue;
                    // A durable pending export owns this marker even if its POST response was lost.
                    var pending = await FindPendingAsync(projectId, remote.Body, graph.Tasks, ct);
                    if (pending is not null)
                    {
                        await Revalidate(ct);
                        await store.WriteReceiptAsync(projectId, pending with { IssueNumber = remote.Number }, ct);
                        continue;
                    }
                    if (remote.Closed)
                        continue;
                    var id = "issue-" + Hash(projectId + "\n" + repository.FullName.ToLowerInvariant() + "\n" + remote.Number);
                    var content = ToContent(remote, repository);
                    var result = await backlog.AddTaskAsync(new(projectId, id), content, graph.Revision, ct, Revalidate);
                    RequireResult(result);
                    await Revalidate(ct);
                    await store.WriteReceiptAsync(projectId,
                        new(id, repository.FullName.ToLowerInvariant(), remote.Number, remote, false), ct);
                }
                page = imported.NextPage ?? 1;
                var tasks = (await ReadGraph(projectId, ct)).Tasks
                    .Where(task => !task.IsArchived && string.CompareOrdinal(task.Reference.TaskId, cursor) > 0)
                    .OrderBy(task => task.Reference.TaskId, StringComparer.Ordinal)
                    .Take(project.Settings.MaxItems).ToArray();
                var pendingCreation = false;
                foreach (var task in tasks)
                {
                    await Revalidate(ct);
                    if (task.RepositoryIssue is { } link &&
                        !Matches(link, repository, link.IssueNumber))
                    {
                        cursor = task.Reference.TaskId;
                        continue;
                    }
                    var receipt = await store.ReadReceiptAsync(projectId, task.Reference.TaskId, ct);
                    if (receipt is not null && receipt.Repository != repository.FullName.ToLowerInvariant())
                        throw new CoordinationException("issue_sync_repository_changed", 409);
                    var number = task.RepositoryIssue?.IssueNumber ?? receipt?.IssueNumber;
                    if (task.RepositoryIssue is not null && receipt?.IssueNumber is { } mapped &&
                        mapped != task.RepositoryIssue.IssueNumber)
                        throw new CoordinationException("issue_sync_mapping_changed", 409);
                    if (number is null)
                    {
                        if (receipt?.CreatePending == true)
                        {
                            pendingCreation = true;
                            cursor = task.Reference.TaskId;
                            continue;
                        }
                        if (string.IsNullOrWhiteSpace(task.Title))
                            throw new CoordinationException("issue_sync_task_title_required", 409);
                        receipt = new(task.Reference.TaskId, repository.FullName.ToLowerInvariant(), null, null, true);
                        await Revalidate(ct);
                        await store.WriteReceiptAsync(projectId, receipt, ct);
                        try
                        {
                            var created = await source.CreateIssueAsync(context,
                                new(task.Title, WithMarker(task.Description ?? "", projectId, task.Reference.TaskId)), ct);
                            number = created.Number;
                            await store.WriteReceiptAsync(projectId, receipt with { IssueNumber = number }, ct);
                        }
                        catch (SourceControlOperationException error) when (
                            error.Code is SourceControlFailureCode.PermissionDenied or
                                SourceControlFailureCode.NotFound or SourceControlFailureCode.Conflict or
                                SourceControlFailureCode.InvalidRequest or SourceControlFailureCode.RateLimited or
                                SourceControlFailureCode.CapabilityUnavailable)
                        {
                            // Only a definitive rejection permits the next cycle to send a new POST.
                            await store.WriteReceiptAsync(projectId, receipt with { CreatePending = false }, CancellationToken.None);
                            throw;
                        }
                    }
                    await ReconcileAsync(project, context, task.Reference, number.Value, Revalidate, ct);
                    cursor = task.Reference.TaskId;
                }
                if (tasks.Length < project.Settings.MaxItems)
                    cursor = "";
                await Revalidate(ct);
                await store.SetStatusAsync(project, pendingCreation ? "failed" : "succeeded",
                    pendingCreation ? "issue_sync_create_outcome_pending" : null, page, cursor, ct);
            }
            catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
            {
                await store.SetStatusAsync(project, "failed", "issue_sync_cycle_timeout", page, cursor, stopping);
            }
            catch (Exception error) when (error is SourceControlOperationException or RuntimeAuthorizationException or
                CoordinationException or HttpRequestException or System.Text.Json.JsonException)
            {
                var code = error switch
                {
                    SourceControlOperationException provider => "issue_sync_provider_" + provider.Code.ToString().ToLowerInvariant(),
                    RuntimeAuthorizationException auth => auth.Code,
                    CoordinationException owner => owner.Code,
                    _ => "issue_sync_upstream_unavailable"
                };
                await store.SetStatusAsync(project, "failed", code, page, cursor, stopping);
            }
            finally { credential?.Invalidate(); }
        }
        finally { await IssueSyncStore.UnlockAsync(connection, projectId); }
    }

    private async Task ReconcileAsync(IssueSyncProject project, SourceControlOperationContext context,
        BacklogTaskReference taskReference, long number, Func<CancellationToken, Task> revalidate, CancellationToken ct)
    {
        var graph = await ReadGraph(project.ProjectId, ct);
        var task = graph.Tasks.Single(item => item.Reference == taskReference);
        if (task.IsArchived)
            throw new CoordinationException("issue_sync_task_archived", 409);
        var receipt = await store.ReadReceiptAsync(project.ProjectId, taskReference.TaskId, ct);
        var remote = await issues.ReadIssueAsync(context, number, ct);
        var remoteContent = remote with { Body = StripMarker(remote.Body, project.ProjectId, taskReference.TaskId) };
        var local = new SourceControlIssueContent(number, task.Title, task.Description ?? "",
            task.Lifecycle == BacklogTaskLifecycle.Closed);
        // On first attachment an existing repository link reads provider content; a pending export starts from local content.
        var desired = receipt?.Baseline is { } baseline ? Merge(local, remoteContent, baseline) :
            receipt?.CreatePending == true ? local : remoteContent;
        var marker = receipt?.CreatePending == true || remote.Body != remoteContent.Body;
        var external = desired with { Body = marker ? WithMarker(desired.Body, project.ProjectId, taskReference.TaskId) : desired.Body };
        if (remote != external)
        {
            await revalidate(ct);
            await source.VerifyCurrentBindingAsync(context, SourceControlCapabilities.IssueWrite, ct);
            await issues.UpdateIssueAsync(context, external, ct);
        }
        var content = ToContent(desired, context.Repository);
        if (local != desired || !Matches(task.RepositoryIssue, context.Repository, number))
        {
            var result = await backlog.SetTaskContentAsync(taskReference, content, graph.Revision, task.Revision, ct, revalidate);
            RequireResult(result);
        }
        await revalidate(ct);
        await store.WriteReceiptAsync(project.ProjectId,
            new(taskReference.TaskId, context.Repository.FullName.ToLowerInvariant(), number, desired, false), ct);
    }

    internal static SourceControlIssueContent Merge(SourceControlIssueContent local,
        SourceControlIssueContent remote, SourceControlIssueContent baseline) => new(remote.Number,
        local.Title != baseline.Title ? local.Title : remote.Title,
        local.Body != baseline.Body ? local.Body : remote.Body,
        local.Closed != baseline.Closed ? local.Closed : remote.Closed);

    private async Task<IssueSyncReceipt?> FindPendingAsync(string projectId, string body,
        ImmutableArray<BacklogTaskSnapshot> tasks, CancellationToken ct)
    {
        foreach (var task in tasks.Where(task => body.EndsWith(Marker(projectId, task.Reference.TaskId), StringComparison.Ordinal)))
        {
            var receipt = await store.ReadReceiptAsync(projectId, task.Reference.TaskId, ct);
            if (receipt?.CreatePending == true)
                return receipt;
        }
        return null;
    }

    private async Task<BacklogDependencyGraph> ReadGraph(string project, CancellationToken ct)
    {
        var result = await backlog.ReadGraphAsync(project, ct);
        RequireResult(result);
        return result.Value!;
    }

    private static void RequireResult<T>(BacklogCoreResult<T> result) where T : class
    {
        if (!result.IsSuccess)
            throw new CoordinationException("issue_sync_backlog_" + result.Issues[0].Code.ToString().ToLowerInvariant(), 409);
    }

    internal static void RequireGrant(IssueSyncProject project, IssueSyncGrantReceipt grant)
    {
        if (grant is null || grant.Project is null || !grant.Active || grant.GrantId != project.Settings.GrantId ||
            grant.Project != project.GrantProof.Project || grant.Project.ProjectId != project.ProjectId ||
            grant.Project.TenantId != project.TenantId)
            throw new CoordinationException("issue_sync_grant_scope_denied", 403);
    }

    private static BacklogTaskContent ToContent(SourceControlIssueContent issue, SourceControlRepositoryIdentity repository) =>
        new(issue.Title, issue.Body, issue.Closed ? BacklogTaskLifecycle.Closed : BacklogTaskLifecycle.Open,
            new(SourceControlProviderIds.GitHub, repository, issue.Number));
    private static bool Matches(BacklogRepositoryIssueLink? link, SourceControlRepositoryIdentity repository, long number) =>
        link is not null && link.ProviderId == SourceControlProviderIds.GitHub && link.IssueNumber == number &&
        string.Equals(link.Repository.FullName, repository.FullName, StringComparison.OrdinalIgnoreCase);
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static string Marker(string projectId, string taskId) => "<!-- agentweaver-issue-sync:" + Hash(projectId + "\n" + taskId) + " -->";
    private static string WithMarker(string body, string projectId, string taskId) => body + "\n\n" + Marker(projectId, taskId);
    private static string StripMarker(string body, string projectId, string taskId)
    {
        var suffix = "\n\n" + Marker(projectId, taskId);
        return body.EndsWith(suffix, StringComparison.Ordinal) ? body[..^suffix.Length] : body;
    }
}
