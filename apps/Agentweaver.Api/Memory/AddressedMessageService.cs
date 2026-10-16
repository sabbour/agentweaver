using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Security;
using Agentweaver.Domain;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;

namespace Agentweaver.Api.Memory;

public sealed record SendAddressedMessage(
    string Recipient, [property: JsonPropertyName("target_run_id")] string TargetRunId,
    string Content, [property: JsonPropertyName("idempotency_key")] string IdempotencyKey,
    [property: JsonPropertyName("reply_to_id")] string? ReplyToId = null,
    [property: JsonPropertyName("reference_kind")] string? ReferenceKind = null,
    [property: JsonPropertyName("reference_id")] string? ReferenceId = null,
    [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt = null);

public sealed record RetryAddressedMessage(
    [property: JsonPropertyName("idempotency_key")] string IdempotencyKey,
    [property: JsonPropertyName("target_run_id")] string? TargetRunId = null);

public sealed class AddressedMessageError(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed class AddressedMessageService(MemoryDbContext db, IRunStore runs)
{
    public async Task<AddressedMessage> SendAsync(
        string projectId, VerifiedAuthor sender, SendAddressedMessage request,
        Func<string, bool> isActiveMember, CancellationToken ct) =>
        await SendCoreAsync(projectId, sender, request, isActiveMember, ct, null);

    private async Task<AddressedMessage> SendCoreAsync(
        string projectId, VerifiedAuthor sender, SendAddressedMessage request,
        Func<string, bool> isActiveMember, CancellationToken ct, AddressedMessage? retryOf)
    {
        if (string.IsNullOrWhiteSpace(request.Recipient)
            || string.IsNullOrWhiteSpace(request.TargetRunId)
            || string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || request.IdempotencyKey.Length > 128
            || string.IsNullOrWhiteSpace(request.Content)
            || request.Content.Length > 16000
            || request.ReferenceKind is not (null or "backlog_task" or "work_plan" or "finding")
            || (request.ReferenceKind is null) != (request.ReferenceId is null)
            || request.ReferenceId?.Length > 128)
            throw new AddressedMessageError("invalid_message");

        var recipient = request.Recipient.Trim();
        var targetId = request.TargetRunId.Trim();
        var key = request.IdempotencyKey.Trim();
        var now = DateTimeOffset.UtcNow;
        // The unique key is checked before run state: a lost acceptance response can always
        // recover the original message, including after either run has ended.
        var existing = await db.AddressedMessages.AsNoTracking().SingleOrDefaultAsync(
            m => m.ProjectId == projectId && m.SenderIdentity == sender.SourceIdentity
                && m.IdempotencyKey == key, ct);
        if (existing is not null) return Same(existing) ? existing : throw new AddressedMessageError("idempotency_conflict");

        if (request.ExpiresAt is { } expiry && (expiry <= now || expiry > now.AddDays(7)))
            throw new AddressedMessageError("invalid_expiry");
        if (!isActiveMember(recipient)) throw new AddressedMessageError("recipient_unavailable");
        if (sender.SourceRunId is not null)
            await RequireActiveRunAsync(sender.SourceRunId, projectId, sender.AgentName, ct);
        var target = await RequireActiveRunAsync(targetId, projectId, recipient, ct);
        if (target.Status != RunStatus.InProgress)
            throw new AddressedMessageError("target_not_running");

        AddressedMessage? reply = null;
        if (request.ReplyToId is not null && retryOf is null)
        {
            reply = await db.AddressedMessages.AsNoTracking().SingleOrDefaultAsync(
                m => m.Id == request.ReplyToId && m.ProjectId == projectId, ct);
            var operatorFollowup = sender.SourceRunId is null && reply is not null
                && reply.SourceRunId is null && reply.SenderIdentity == sender.SourceIdentity
                && reply.TargetRunId == targetId
                && string.Equals(reply.Recipient, recipient, StringComparison.OrdinalIgnoreCase);
            var agentReply = reply is not null
                && string.Equals(reply.Recipient, sender.AgentName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(reply.Sender, recipient, StringComparison.OrdinalIgnoreCase)
                && reply.SourceRunId is not null && reply.SourceRunId == targetId
                && reply.TargetRunId == sender.SourceRunId;
            if (reply is null || reply.Status != AddressedMessageStates.Acknowledged
                || !(operatorFollowup || agentReply))
                throw new AddressedMessageError("reply_unavailable");
        }

        var message = new AddressedMessage
        {
            Id = Guid.NewGuid().ToString("N"),
            ProjectId = projectId,
            Sender = sender.AgentName,
            SenderIdentity = sender.SourceIdentity,
            Recipient = recipient,
            SourceRunId = sender.SourceRunId,
            TargetRunId = targetId,
            ThreadId = retryOf?.ThreadId ?? reply?.ThreadId ?? Guid.NewGuid().ToString("N"),
            ReplyToId = retryOf?.ReplyToId ?? reply?.Id,
            ReferenceKind = request.ReferenceKind,
            ReferenceId = request.ReferenceId,
            IdempotencyKey = key,
            Content = request.Content,
            CreatedAt = now,
            ExpiresAt = request.ExpiresAt?.ToUniversalTime() ?? now.AddDays(1),
        };
        db.AddressedMessages.Add(message);
        try
        {
            await db.SaveChangesAsync(ct);
            return message;
        }
        catch (DbUpdateException)
        {
            db.Entry(message).State = EntityState.Detached;
            existing = await db.AddressedMessages.AsNoTracking().SingleOrDefaultAsync(
                m => m.ProjectId == projectId && m.SenderIdentity == sender.SourceIdentity
                    && m.IdempotencyKey == key, ct);
            if (existing is null) throw;
            return Same(existing) ? existing : throw new AddressedMessageError("idempotency_conflict");
        }

        bool Same(AddressedMessage stored) =>
            stored.Recipient == recipient && stored.TargetRunId == targetId
            && stored.Content == request.Content && stored.ReplyToId == request.ReplyToId
            && stored.ReferenceKind == request.ReferenceKind && stored.ReferenceId == request.ReferenceId
            && (request.ExpiresAt is null || stored.ExpiresAt == request.ExpiresAt.Value);
    }

    public async Task<IReadOnlyList<AddressedMessage>> ListAsync(
        string projectId, string? runId, int limit, CancellationToken ct)
    {
        await ExpireAsync(projectId, ct);
        return await db.AddressedMessages.AsNoTracking()
            .Where(m => m.ProjectId == projectId && (runId == null || m.TargetRunId == runId || m.SourceRunId == runId))
            .OrderByDescending(m => m.CreatedAt).Take(Math.Clamp(limit, 1, 100)).ToListAsync(ct);
    }

    public async Task<AddressedMessage> RetryAsync(
        string projectId, string id, VerifiedAuthor sender, RetryAddressedMessage request,
        Func<string, bool> isActiveMember, CancellationToken ct)
    {
        var original = await GetAsync(projectId, id, ct);
        if (original is null || original.SenderIdentity != sender.SourceIdentity)
            throw new AddressedMessageError("message_unavailable");
        if (original.Status is not (AddressedMessageStates.Expired or AddressedMessageStates.Undeliverable))
            throw new AddressedMessageError("message_not_retryable");
        return await SendCoreAsync(projectId, sender, new SendAddressedMessage(
            original.Recipient, request.TargetRunId ?? original.TargetRunId, original.Content,
            request.IdempotencyKey, original.ReplyToId, original.ReferenceKind, original.ReferenceId),
            isActiveMember, ct, original);
    }

    public async Task<AddressedMessage?> GetAsync(string projectId, string id, CancellationToken ct)
    {
        await ExpireAsync(projectId, ct);
        return await db.AddressedMessages.AsNoTracking().SingleOrDefaultAsync(
            m => m.ProjectId == projectId && m.Id == id, ct);
    }

    public Task<bool> HasPendingAsync(string projectId, string targetRunId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        return db.AddressedMessages.AsNoTracking().AnyAsync(m =>
            m.ProjectId == projectId && m.TargetRunId == targetRunId && m.ExpiresAt > now
            && (m.Status == AddressedMessageStates.Accepted
                || m.Status == AddressedMessageStates.Claimed && m.ClaimedUntil <= now), ct);
    }

    public Task<bool> HasUnfinishedAsync(string projectId, string targetRunId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        return db.AddressedMessages.AsNoTracking().AnyAsync(m =>
            m.ProjectId == projectId && m.TargetRunId == targetRunId && m.ExpiresAt > now
            && (m.Status == AddressedMessageStates.Accepted || m.Status == AddressedMessageStates.Claimed), ct);
    }

    // Called at a recipient turn boundary, never while the model is mid-turn.
    public async Task<AddressedMessage?> ClaimAsync(
        string projectId, string targetRunId, string recipient, string owner, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(owner) || owner.Length > 128)
            throw new AddressedMessageError("invalid_claim_owner");
        await RequireActiveRunAsync(targetRunId, projectId, recipient, ct);
        await ExpireAsync(projectId, ct);
        var now = DateTimeOffset.UtcNow;
        var candidates = await db.AddressedMessages.AsNoTracking()
            .Where(m => m.ProjectId == projectId && m.TargetRunId == targetRunId
                && (m.Status == AddressedMessageStates.Accepted
                    || m.Status == AddressedMessageStates.Claimed && m.ClaimedUntil <= now))
            .OrderBy(m => m.CreatedAt).Take(16).ToListAsync(ct);
        foreach (var candidate in candidates)
        {
            var updated = await db.AddressedMessages.Where(m => m.Id == candidate.Id
                    && m.Fence == candidate.Fence && m.ExpiresAt > now
                    && (m.Status == AddressedMessageStates.Accepted
                        || m.Status == AddressedMessageStates.Claimed && m.ClaimedUntil <= now))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.Status, AddressedMessageStates.Claimed)
                    .SetProperty(m => m.ClaimOwner, owner)
                    .SetProperty(m => m.ClaimedUntil, now.AddMinutes(2))
                    .SetProperty(m => m.AcknowledgedAt, (DateTimeOffset?)null)
                    .SetProperty(m => m.Fence, m => m.Fence + 1), ct);
            if (updated == 1) return await GetAsync(projectId, candidate.Id, ct);
        }
        return null;
    }

    public async Task<AddressedMessage> DeliverAsync(
        string projectId, string id, string recipientRunId, string recipient, string owner, long fence, CancellationToken ct)
    {
        await RequireActiveRunAsync(recipientRunId, projectId, recipient, ct);
        var now = DateTimeOffset.UtcNow;
        var updated = await db.AddressedMessages
            .Where(m => m.Id == id && m.ProjectId == projectId && m.TargetRunId == recipientRunId
                && m.Status == AddressedMessageStates.Claimed && m.ClaimOwner == owner
                && m.Fence == fence && m.ClaimedUntil > now && m.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, m => m.AcknowledgedAt == null
                    ? AddressedMessageStates.Delivered : AddressedMessageStates.Acknowledged)
                .SetProperty(m => m.DeliveredAt, now)
                .SetProperty(m => m.ClaimOwner, (string?)null)
                .SetProperty(m => m.ClaimedUntil, (DateTimeOffset?)null), ct);
        if (updated != 1) throw new AddressedMessageError("claim_lost");
        return (await GetAsync(projectId, id, ct))!;
    }

    public async Task RenewClaimAsync(
        string projectId, string id, string recipientRunId, string recipient,
        string owner, long fence, CancellationToken ct)
    {
        await RequireActiveRunAsync(recipientRunId, projectId, recipient, ct);
        var now = DateTimeOffset.UtcNow;
        var updated = await db.AddressedMessages
            .Where(m => m.Id == id && m.ProjectId == projectId && m.TargetRunId == recipientRunId
                && m.Status == AddressedMessageStates.Claimed && m.ClaimOwner == owner
                && m.Fence == fence && m.ClaimedUntil > now && m.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.ClaimedUntil, now.AddMinutes(2)), ct);
        if (updated != 1) throw new AddressedMessageError("claim_lost");
    }

    public async Task<AddressedMessage> AcknowledgeAsync(
        string projectId, string id, string recipientRunId, string recipient, CancellationToken ct)
    {
        await RequireActiveRunAsync(recipientRunId, projectId, recipient, ct);
        var now = DateTimeOffset.UtcNow;
        await db.AddressedMessages
            .Where(m => m.Id == id && m.ProjectId == projectId && m.TargetRunId == recipientRunId
                && m.Status == AddressedMessageStates.Claimed && m.ClaimOwner != null
                && m.ClaimOwner.StartsWith("turn:") && m.ClaimedUntil > now && m.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.AcknowledgedAt, now), ct);
        await db.AddressedMessages
            .Where(m => m.Id == id && m.ProjectId == projectId && m.TargetRunId == recipientRunId
                && m.Status == AddressedMessageStates.Delivered && m.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, AddressedMessageStates.Acknowledged)
                .SetProperty(m => m.AcknowledgedAt, now), ct);
        var message = await GetAsync(projectId, id, ct);
        if (message is null || message.TargetRunId != recipientRunId
            || message.Status != AddressedMessageStates.Acknowledged
                && (message.Status != AddressedMessageStates.Claimed || message.AcknowledgedAt is null))
            throw new AddressedMessageError("message_not_delivered");
        return message;
    }

    public async Task<AddressedMessage> MarkUndeliverableAsync(
        string projectId, string id, string targetRunId, string recipient, string reason, CancellationToken ct)
    {
        if (reason is not ("target_cancelled" or "recipient_retired" or "target_completed"))
            throw new AddressedMessageError("invalid_failure_reason");
        var message = await GetAsync(projectId, id, ct);
        if (message is null || message.TargetRunId != targetRunId
            || !string.Equals(message.Recipient, recipient, StringComparison.OrdinalIgnoreCase))
            throw new AddressedMessageError("message_unavailable");
        await db.AddressedMessages.Where(m => m.Id == id && m.ProjectId == projectId
            && (m.Status == AddressedMessageStates.Accepted || m.Status == AddressedMessageStates.Claimed))
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, AddressedMessageStates.Undeliverable)
                .SetProperty(m => m.FailureReason, reason)
                .SetProperty(m => m.AcknowledgedAt, (DateTimeOffset?)null)
                .SetProperty(m => m.ClaimOwner, (string?)null)
                .SetProperty(m => m.ClaimedUntil, (DateTimeOffset?)null), ct);
        return (await GetAsync(projectId, id, ct))!;
    }

    public async Task ReconcileAsync(
        string projectId, Func<string, bool> isActiveMember, CancellationToken ct)
    {
        await ExpireAsync(projectId, ct);
        DateTimeOffset? cursorTime = null;
        string? cursorId = null;
        while (true)
        {
            var pending = await db.AddressedMessages.AsNoTracking()
                .Where(m => m.ProjectId == projectId
                    && (m.Status == AddressedMessageStates.Accepted || m.Status == AddressedMessageStates.Claimed)
                    && (cursorTime == null || m.CreatedAt > cursorTime
                        || m.CreatedAt == cursorTime && string.Compare(m.Id, cursorId) > 0))
                .OrderBy(m => m.CreatedAt).ThenBy(m => m.Id).Take(100).ToListAsync(ct);
            if (pending.Count == 0) break;
            foreach (var message in pending)
            {
                string? reason = null;
                if (!isActiveMember(message.Recipient))
                    reason = "recipient_retired";
                else if (!RunId.TryParse(message.TargetRunId, out var parsed))
                    reason = "target_completed";
                else
                {
                    var run = await runs.GetAsync(parsed, ct);
                    if (run is null || run.ArchivedAt is not null || run.EndedAt is not null
                        || run.Status != RunStatus.InProgress)
                        reason = run?.Result is "cancelled" or "steering_stop"
                            ? "target_cancelled"
                            : "target_completed";
                }
                if (reason is not null)
                    await MarkUndeliverableAsync(projectId, message.Id, message.TargetRunId,
                        message.Recipient, reason, ct);
            }
            cursorTime = pending[^1].CreatedAt;
            cursorId = pending[^1].Id;
        }
    }

    private Task<int> ExpireAsync(string projectId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        return db.AddressedMessages.Where(m => m.ProjectId == projectId && m.ExpiresAt <= now
            && (m.Status == AddressedMessageStates.Accepted || m.Status == AddressedMessageStates.Claimed
                || m.Status == AddressedMessageStates.Delivered))
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, AddressedMessageStates.Expired)
                .SetProperty(m => m.FailureReason, "ttl_elapsed")
                .SetProperty(m => m.AcknowledgedAt, (DateTimeOffset?)null)
                .SetProperty(m => m.ClaimOwner, (string?)null)
                .SetProperty(m => m.ClaimedUntil, (DateTimeOffset?)null), ct);
    }

    private async Task<Run> RequireActiveRunAsync(
        string id, string projectId, string agent, CancellationToken ct)
    {
        if (!RunId.TryParse(id, out var parsed)) throw new AddressedMessageError("run_unavailable");
        var run = await runs.GetAsync(parsed, ct);
        if (run is null || run.ProjectId?.ToString() != projectId
            || !string.Equals(run.AgentName, agent, StringComparison.OrdinalIgnoreCase)
            || run.ArchivedAt is not null
            || run.Status != RunStatus.InProgress)
            throw new AddressedMessageError("run_unavailable");
        return run;
    }
}
