using System.Security.Claims;
using System.Net.Http.Json;
using Agentweaver.Abstractions;
using Agentweaver.EventsAndSessions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Encodings.Web;
using Xunit;

namespace Agentweaver.EventsAndSessions.Tests;

public sealed class SessionsEndpointFakeTests
{
    [Fact]
    public async Task EndpointUsesFakeJournalAndReturnsOnlyBoundedPersistenceFailure()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization();
        var journal = new FakeJournal();
        var binder = new FakeBinder();
        builder.Services.AddSingleton<ICoordinationOwnerClient, FakeCoordinationOwnerClient>();
        builder.Services.AddSingleton<ISessionsJournal>(journal);
        builder.Services.AddSingleton<ISessionsProviderBinder>(binder);
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapEventsAndSessionsEndpoints();
        await app.StartAsync();

        using var client = app.GetTestClient();
        using var response = await client.PostAsync("/internal/sessions/session-1", content: null);
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("journal_unavailable", body, StringComparison.Ordinal);
        Assert.DoesNotContain("secret=do-not-return", body, StringComparison.Ordinal);
        Assert.True(binder.Binding.Matches(journal.CreatedBinding!));

        using var replayResponse = await client.GetAsync("/internal/sessions/session-1/events");
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, replayResponse.StatusCode);
        Assert.True(journal.CreatedBinding!.Matches(binder.VerifiedBinding!));

        using var runReplayResponse =
            await client.GetAsync("/internal/projects/project-1/runs/run-1/events");
        Assert.Equal(System.Net.HttpStatusCode.OK, runReplayResponse.StatusCode);
        Assert.Equal(("project-1", "run-1"), journal.LastRunScope);
        Assert.True(journal.CreatedBinding.Matches(binder.VerifiedBinding!));

        using var forkResponse = await client.PostAsJsonAsync(
            "/internal/sessions/session-1/fork",
            new SessionForkRequest("fork-target", Guid.NewGuid(), "cursor", "fork-once"));
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, forkResponse.StatusCode);
        Assert.True(forkResponse.Headers.CacheControl?.NoStore == true);
        Assert.Contains("session_fork_unavailable",
            await forkResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private sealed class FakeCoordinationOwnerClient : ICoordinationOwnerClient
    {
        public Task<MessageRouteBinding> ValidateMessageRouteAsync(
            HttpContext context,
            string projectId,
            string runId,
            MessageRouteValidationRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CoordinationSessionBinding> GetSessionBindingAsync(
            HttpContext context,
            string projectId,
            string runId,
            string sessionId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SessionForkAdmissionReceipt> ValidateSessionForkAdmissionAsync(
            HttpContext context,
            SessionIdentity source,
            SessionForkRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SessionForkAdmissionReceipt(
                Guid.NewGuid(),
                source,
                request.TargetSessionId,
                request.SourceEventId,
                request.SourceCursor,
                request.IdempotencyKey,
                1,
                "https://identity.test",
                context.User.FindFirstValue("sub")!,
                new string('A', 64)));
    }

    private sealed class FakeBinder : ISessionsProviderBinder
    {
        public SessionProviderBinding Binding { get; } = new(
                "project-1", "run-1", "postgres.native-sessions", new Version(1, 0, 0),
                1, "options-v1", "resource-1", 1,
                System.Collections.Immutable.ImmutableHashSet<string>.Empty);
        public SessionProviderBinding? VerifiedBinding { get; private set; }

        public Task<SessionProviderBinding> ResolveAndPinAsync(
            ClaimsPrincipal principal, CancellationToken cancellationToken = default) =>
            Task.FromResult(Binding);

        public Task VerifyPinnedAsync(
            ClaimsPrincipal principal,
            SessionProviderBinding binding,
            CancellationToken cancellationToken = default)
        {
            VerifiedBinding = binding;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeJournal : ISessionsJournal
    {
        public SessionProviderBinding? CreatedBinding { get; private set; }
        public (string ProjectId, string RunId)? LastRunScope { get; private set; }

        public Task<SessionRecord> CreateSessionAsync(
            ClaimsPrincipal principal,
            string sessionId,
            SessionProviderBinding binding,
            CancellationToken cancellationToken = default)
        {
            CreatedBinding = binding;
            throw new InvalidOperationException("database secret=do-not-return");
        }

        public Task<SessionProviderBinding> GetProviderBindingAsync(
            ClaimsPrincipal principal, string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(CreatedBinding
                ?? throw new InvalidOperationException("A fake provider binding was not created."));

        public Task<SessionProviderBinding> GetRunProviderBindingAsync(
            ClaimsPrincipal principal,
            string projectId,
            string runId,
            CancellationToken cancellationToken = default)
        {
            LastRunScope = (projectId, runId);
            return Task.FromResult(CreatedBinding
                ?? throw new InvalidOperationException("A fake provider binding was not created."));
        }

        public Task<SessionAppendResult> AppendAsync(
            ClaimsPrincipal principal,
            string sessionId,
            AppendSessionEvent input,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SessionForkResult> ForkFromExplicitEventAsync(
            ClaimsPrincipal principal,
            string sourceSessionId,
            SessionForkRequest request,
            Func<CancellationToken, Task> validateAdmission,
            CancellationToken cancellationToken = default) =>
            throw new SessionForkUnsupportedException("The fake journal does not support session forks.");

        public Task<SessionEventPage> ReplayAsync(
            ClaimsPrincipal principal,
            SessionEventPageRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("database secret=do-not-return");

        public Task<SessionEventPage> ReplayRunAsync(
            ClaimsPrincipal principal,
            SessionRunEventPageRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SessionEventPage([], null, HasMore: false));

        public IAsyncEnumerable<SessionEventDelivery> SubscribeAsync(
            ClaimsPrincipal principal,
            SessionSubscriptionRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<SessionEventDelivery> SubscribeRunAsync(
            ClaimsPrincipal principal,
            SessionRunSubscriptionRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity(
                [
                    new Claim("sub", "33333333-3333-3333-3333-333333333333"),
                    new Claim("iss", "https://identity.test"),
                    new Claim("scope", "openid"),
                    new Claim("aud", "agentweaver.events"),
                    new Claim("project_id", "project-1"),
                    new Claim("run_id", "run-1")
                ],
                Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
