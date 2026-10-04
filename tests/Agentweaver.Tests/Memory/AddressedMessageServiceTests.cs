using System.Reflection;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Security;
using Agentweaver.Domain;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Tests.Memory;

public sealed class AddressedMessageServiceTests
{
    private const string Project = "aaaaaaaa-aaaa-4aaa-aaaa-aaaaaaaaaaaa";
    private const string Source = "bbbbbbbb-bbbb-4bbb-bbbb-bbbbbbbbbbbb";
    private const string Target = "cccccccc-cccc-4ccc-cccc-cccccccccccc";
    private static readonly VerifiedAuthor Sender = new(
        "Tank", "run", $"run:{Source}", Source, false);
    private static readonly VerifiedAuthor Recipient = new(
        "Link", "run", $"run:{Target}", Target, false);

    [Fact]
    public async Task LostAcceptanceResponse_AndDuplicateKey_DoNotCreateAnotherMessage()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.Service;
        var request = NewRequest();
        var first = await service.SendAsync(Project, Sender, request, Active, default);
        var retry = await service.SendAsync(Project, Sender, request, Active, default);
        retry.Id.Should().Be(first.Id);
        (await service.ListAsync(Project, null, 50, default)).Should().ContainSingle();
        var conflicting = () => service.SendAsync(Project, Sender, request with { Content = "changed" }, Active, default);
        (await conflicting.Should().ThrowAsync<AddressedMessageError>())
            .Which.Code.Should().Be("idempotency_conflict");
    }

    [Fact]
    public async Task ExpiredExplicitExpiry_StillReplaysTheOriginalAcceptedMessage()
    {
        await using var fixture = await Fixture.CreateAsync();
        var expires = DateTimeOffset.UtcNow.AddSeconds(2);
        var request = NewRequest() with { ExpiresAt = expires };
        var sent = await fixture.Service.SendAsync(Project, Sender, request, Active, default);
        await Task.Delay(TimeSpan.FromSeconds(2.1));
        var replay = await fixture.Service.SendAsync(Project, Sender, request, Active, default);
        replay.Id.Should().Be(sent.Id);
    }

    [Fact]
    public async Task OffsetExpiry_ExpiresAtTheSameInstantAsUtc()
    {
        await using var fixture = await Fixture.CreateAsync();
        var expires = DateTimeOffset.UtcNow.AddMinutes(1).ToOffset(TimeSpan.FromHours(2));
        var sent = await fixture.Service.SendAsync(Project, Sender,
            NewRequest() with { ExpiresAt = expires }, Active, default);
        await fixture.Db.AddressedMessages.Where(m => m.Id == sent.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.ExpiresAt,
                DateTimeOffset.UtcNow.AddMinutes(-1).ToOffset(TimeSpan.FromHours(2))));
        (await fixture.Service.GetAsync(Project, sent.Id, default))!.Status
            .Should().Be(AddressedMessageStates.Expired);
    }

    [Fact]
    public async Task ExpiredLease_ReclaimsWithNewFence_AndRejectsStaleDelivery()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.Service;
        var sent = await service.SendAsync(Project, Sender, NewRequest(), Active, default);
        var claim = (await service.ClaimAsync(Project, Target, "Link", "owner-1", default))!;
        claim.Status.Should().Be(AddressedMessageStates.Claimed);
        await fixture.Db.AddressedMessages.Where(m => m.Id == sent.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.ClaimedUntil, DateTimeOffset.UtcNow.AddMinutes(-1)));
        var recovered = (await service.ClaimAsync(Project, Target, "Link", "owner-2", default))!;
        recovered.Fence.Should().Be(claim.Fence + 1);
        var stale = () => service.DeliverAsync(Project, sent.Id, Target, "Link", "owner-1", claim.Fence, default);
        (await stale.Should().ThrowAsync<AddressedMessageError>()).Which.Code.Should().Be("claim_lost");
        var delivered = await service.DeliverAsync(Project, sent.Id, Target, "Link", "owner-2", recovered.Fence, default);
        delivered.Status.Should().Be(AddressedMessageStates.Delivered);
        (await service.AcknowledgeAsync(Project, sent.Id, Target, "Link", default)).Status
            .Should().Be(AddressedMessageStates.Acknowledged);
        (await service.AcknowledgeAsync(Project, sent.Id, Target, "Link", default)).Status
            .Should().Be(AddressedMessageStates.Acknowledged);
    }

    [Fact]
    public async Task CrossProject_CompletedRun_RetiredMember_AndLateReply_AreRejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.Service;
        var crossProject = () => service.SendAsync(Guid.NewGuid().ToString(), Sender, NewRequest(), Active, default);
        (await crossProject.Should().ThrowAsync<AddressedMessageError>()).Which.Code.Should().Be("run_unavailable");
        var retired = () => service.SendAsync(Project, Sender, NewRequest(), _ => false, default);
        (await retired.Should().ThrowAsync<AddressedMessageError>()).Which.Code.Should().Be("recipient_unavailable");
        var first = await service.SendAsync(Project, Sender, NewRequest(), Active, default);
        var earlyReply = () => service.SendAsync(Project, Recipient,
            new("Tank", Source, "reply", "reply-1", first.Id), Active, default);
        (await earlyReply.Should().ThrowAsync<AddressedMessageError>()).Which.Code.Should().Be("reply_unavailable");
        fixture.Runs[Target] = fixture.Runs[Target] with { Status = RunStatus.Completed };
        var completed = () => service.SendAsync(Project, Sender, NewRequest() with { IdempotencyKey = "new" }, Active, default);
        (await completed.Should().ThrowAsync<AddressedMessageError>()).Which.Code.Should().Be("run_unavailable");
        var noClaim = () => service.ClaimAsync(Project, Target, "Link", "owner", default);
        (await noClaim.Should().ThrowAsync<AddressedMessageError>()).Which.Code.Should().Be("run_unavailable");
    }

    [Fact]
    public async Task Expiry_DoesNotAcknowledgeOrChangeTaskState()
    {
        await using var fixture = await Fixture.CreateAsync();
        var sent = await fixture.Service.SendAsync(Project, Sender,
            NewRequest() with { ReferenceKind = "backlog_task", ReferenceId = "task-1" }, Active, default);
        await fixture.Db.AddressedMessages.Where(m => m.Id == sent.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        var expired = await fixture.Service.GetAsync(Project, sent.Id, default);
        expired!.Status.Should().Be(AddressedMessageStates.Expired);
        expired.FailureReason.Should().Be("ttl_elapsed");
        expired.ReferenceId.Should().Be("task-1");
        expired.AcknowledgedAt.Should().BeNull();
    }

    [Fact]
    public async Task ReplyRequiresAcknowledgedOriginal_AndCorrelatesToSameThread()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.Service;
        var original = await service.SendAsync(Project, Sender, NewRequest(), Active, default);
        var claim = (await service.ClaimAsync(Project, Target, "Link", "recipient", default))!;
        await service.DeliverAsync(Project, original.Id, Target, "Link", "recipient", claim.Fence, default);
        await service.AcknowledgeAsync(Project, original.Id, Target, "Link", default);

        var reply = await service.SendAsync(Project, Recipient,
            new("Tank", Source, "The finding is confirmed", "reply-1", original.Id), Active, default);
        reply.ThreadId.Should().Be(original.ThreadId);
        reply.ReplyToId.Should().Be(original.Id);
        reply.TargetRunId.Should().Be(Source);
        var wrongRecipient = () => service.AcknowledgeAsync(Project, reply.Id, Target, "Link", default);
        (await wrongRecipient.Should().ThrowAsync<AddressedMessageError>())
            .Which.Code.Should().Be("message_not_delivered");
    }

    [Fact]
    public async Task ExplicitUndeliverable_RecordsReasonWithoutForgingReceipt()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.Service;
        var message = await service.SendAsync(Project, Sender, NewRequest(), Active, default);
        var failed = await service.MarkUndeliverableAsync(
            Project, message.Id, Target, "Link", "target_cancelled", default);
        failed.Status.Should().Be(AddressedMessageStates.Undeliverable);
        failed.FailureReason.Should().Be("target_cancelled");
        failed.AcknowledgedAt.Should().BeNull();
        (await service.ClaimAsync(Project, Target, "Link", "recipient", default)).Should().BeNull();
    }

    [Fact]
    public async Task Reconciliation_TerminalOrRetiredRecipient_MakesPendingMessagesUndeliverable()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.Service.SendAsync(Project, Sender, NewRequest(), Active, default);
        fixture.Runs[Target] = fixture.Runs[Target] with
        {
            Status = RunStatus.Completed, EndedAt = DateTimeOffset.UtcNow,
        };
        await fixture.Service.ReconcileAsync(Project, Active, default);
        (await fixture.Service.GetAsync(Project, first.Id, default))!.FailureReason
            .Should().Be("target_completed");

        fixture.Runs[Target] = fixture.Runs[Target] with
        {
            Status = RunStatus.InProgress, EndedAt = null,
        };
        var second = await fixture.Service.SendAsync(Project, Sender,
            NewRequest() with { IdempotencyKey = "second" }, Active, default);
        await fixture.Service.ReconcileAsync(Project, _ => false, default);
        var retired = await fixture.Service.GetAsync(Project, second.Id, default);
        retired!.Status.Should().Be(AddressedMessageStates.Undeliverable);
        retired.FailureReason.Should().Be("recipient_retired");
    }

    [Fact]
    public async Task Reconciliation_AdvancesBeyondFirstHundredHealthyMessages()
    {
        await using var fixture = await Fixture.CreateAsync();
        for (var i = 0; i < 101; i++)
            await fixture.Service.SendAsync(Project, Sender,
                NewRequest() with { IdempotencyKey = $"send-{i}" }, Active, default);
        await fixture.Service.ReconcileAsync(Project, _ => false, default);
        (await fixture.Db.AddressedMessages.CountAsync(
            m => m.Status == AddressedMessageStates.Undeliverable)).Should().Be(101);
    }

    private static SendAddressedMessage NewRequest() =>
        new("Link", Target, "Please check this", "send-1");
    private static bool Active(string _) => true;

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public MemoryDbContext Db { get; }
        public Dictionary<string, Run> Runs { get; }
        public AddressedMessageService Service { get; }

        private Fixture(SqliteConnection connection, MemoryDbContext db)
        {
            _connection = connection;
            Db = db;
            Runs = new Dictionary<string, Run>
            {
                [Source] = CreateRun(Source, "Tank"),
                [Target] = CreateRun(Target, "Link"),
            };
            var runStore = DispatchProxy.Create<IRunStore, RunStoreProxy>();
            ((RunStoreProxy)runStore).Runs = Runs;
            Service = new AddressedMessageService(db, runStore);
        }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<MemoryDbContext>().UseSqlite(connection).Options;
            var db = new MemoryDbContext(options);
            await db.Database.EnsureCreatedAsync();
            return new Fixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }

        private static Run CreateRun(string id, string agent) => new()
        {
            Id = RunId.Parse(id),
            ProjectId = ProjectId.Parse(Project),
            AgentName = agent,
            RepositoryPath = ".",
            OriginatingBranch = "dev",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "message test",
            SubmittingUser = "test",
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
        };
    }

    public class RunStoreProxy : DispatchProxy
    {
        public Dictionary<string, Run> Runs { get; set; } = [];
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name == nameof(IRunStore.GetAsync))
                return Task.FromResult(Runs.GetValueOrDefault(args![0]!.ToString()!));
            throw new NotSupportedException(method?.Name);
        }
    }
}
