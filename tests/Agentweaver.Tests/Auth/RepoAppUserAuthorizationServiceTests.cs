using System.Net;
using System.Security.Claims;
using System.Text;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Agentweaver.Tests.Auth;

public sealed class RepoAppUserAuthorizationServiceTests
{
    [Fact]
    public async Task Begin_UsesPkceS256AndAnOpaqueAllowlistedReturnRoute()
    {
        await using var database = await OpenDatabaseAsync();
        var secrets = new InMemorySecretStore();
        var service = CreateService(database, secrets, new StubHttpClientFactory());

        var result = await service.BeginAsync(Human("entra"), HumanPrincipal(), "settings");

        result.Outcome.Should().Be(RepoAppAuthorizationOutcome.Success);
        result.TransactionId.Should().HaveLength(43);
        result.AuthorizationUrl.Should().Contain("code_challenge_method=S256");
        result.AuthorizationUrl.Should().NotContain(result.TransactionId);
        result.CallbackCookie.Should().HaveLength(43);

        var state = Query(result.AuthorizationUrl!, "state");
        var challenge = Query(result.AuthorizationUrl!, "code_challenge");
        var stored = await database.GitHubAuthorizations.SingleAsync();
        stored.State.Should().Be(state);
        stored.ExternalTransactionId.Should().Be(result.TransactionId);
        stored.ReturnRouteKey.Should().Be("settings");
        stored.PkceVerifierProtected.Should().NotBeNullOrWhiteSpace();
        stored.CallbackCookieHash.Should().NotBe(result.CallbackCookie);
        var verifier = await secrets.GetSecretAsync(stored.PkceVerifierProtected);
        RepoAppUserAuthorizationService.CreateS256Challenge(verifier.Value!).Should().Be(challenge);

        var rejected = await service.BeginAsync(Human("another-entra"), HumanPrincipal(), "https://attacker.invalid");
        rejected.Outcome.Should().Be(RepoAppAuthorizationOutcome.GitHubBindingUnavailable);
    }

    [Fact]
    public async Task McpBrowserHandoff_TransfersCallbackCookieOnceWithoutExposingOAuthState()
    {
        await using var database = await OpenDatabaseAsync();
        var secrets = new InMemorySecretStore();
        var service = CreateService(database, secrets, new StubHttpClientFactory());

        var begin = await service.BeginMcpHandoffAsync(Human("entra"), HumanPrincipal(), "settings");

        begin.Outcome.Should().Be(RepoAppAuthorizationOutcome.Success);
        begin.TransactionId.Should().HaveLength(43);
        begin.BrowserUrl.Should().Be(
            $"https://agentweaver.test/auth/github/repo-app/handoff/{begin.TransactionId}");
        System.Text.Json.JsonSerializer.Serialize(begin).Should()
            .NotContain("state").And.NotContain("cookie").And.NotContain("code_challenge");

        var handoff = await service.TakeMcpBrowserHandoffAsync(begin.TransactionId!, "browser-session", "entra");
        handoff.Should().NotBeNull();
        handoff!.Value.AuthorizationUrl.Should().Contain("state=").And.NotContain(begin.TransactionId!);
        handoff.Value.CallbackCookie.Should().NotBeNullOrWhiteSpace();
        (await service.TakeMcpBrowserHandoffAsync(begin.TransactionId!, "browser-session", "entra")).Should().BeNull(
            "a callback cookie is transferred only to the first browser opening the opaque handoff URL");
    }

    [Fact]
    public async Task McpBrowserHandoff_RequiresTheInitiatingEntraBrowserSessionThroughCallback()
    {
        await using var database = await OpenDatabaseAsync();
        var secrets = new InMemorySecretStore();
        var factory = new StubHttpClientFactory(TokenResponse());
        var service = CreateService(database, secrets, factory);
        var begin = await service.BeginMcpHandoffAsync(Human("entra"), HumanPrincipal(), "settings");
        var attackerHandoff = await service.TakeMcpBrowserHandoffAsync(
            begin.TransactionId!, "attacker-session", "attacker");
        attackerHandoff.Should().BeNull();
        var state = (await database.GitHubAuthorizations.SingleAsync()).State;

        var handoff = await service.TakeMcpBrowserHandoffAsync(begin.TransactionId!, "initiator-session", "entra");
        handoff.Should().NotBeNull();
        (await service.TakeMcpBrowserHandoffAsync(begin.TransactionId!, "other-entra-browser", "entra"))
            .Should().BeNull("the callback must remain bound to the browser session that redeemed the handoff");

        (await service.CompleteBrowserCallbackAsync(
            "attacker-session", "attacker", state, "code", handoff!.Value.CallbackCookie))
            .Outcome.Should().Be(RepoAppAuthorizationOutcome.AuthorizationTransactionInvalid);
        (await database.GitHubAuthorizations.SingleAsync()).Status.Should().Be(GitHubAuthorizationStatus.Pending);
        factory.RequestBodies.Should().BeEmpty();

        (await service.CompleteBrowserCallbackAsync(
            "initiator-session", "entra", state, "code", handoff.Value.CallbackCookie))
            .Outcome.Should().Be(RepoAppAuthorizationOutcome.Success);
    }

    [Fact]
    public async Task Callback_RejectsWrongMissingCookieAndWrongSubjectWithoutRedeeming()
    {
        await using var database = await OpenDatabaseAsync();
        var secrets = new InMemorySecretStore();
        var factory = new StubHttpClientFactory();
        var service = CreateService(database, secrets, factory);
        var begin = await service.BeginAsync(Human("entra"), HumanPrincipal(), "settings");
        var state = Query(begin.AuthorizationUrl!, "state");

        (await service.CompleteAsync(Human("entra"), HumanPrincipal(), state, "code", null))
            .Outcome.Should().Be(RepoAppAuthorizationOutcome.AuthorizationTransactionInvalid);
        (await service.CompleteAsync(Human("entra"), HumanPrincipal(), state, "code", "wrong"))
            .Outcome.Should().Be(RepoAppAuthorizationOutcome.AuthorizationTransactionInvalid);
        (await service.CompleteAsync(Human("other"), HumanPrincipal(), state, "code", begin.CallbackCookie))
            .Outcome.Should().Be(RepoAppAuthorizationOutcome.AuthorizationTransactionInvalid);

        (await database.GitHubAuthorizations.SingleAsync()).Status.Should().Be(GitHubAuthorizationStatus.Pending);
        factory.RequestBodies.Should().BeEmpty();
    }

    [Fact]
    public async Task Callback_RejectsExpiredAndWrongPurposeTransactions()
    {
        await using var database = await OpenDatabaseAsync();
        var secrets = new InMemorySecretStore();
        var service = CreateService(database, secrets, new StubHttpClientFactory());

        await new TwoAppPersistenceStore(database).AddAuthorizationAsync(Transaction(
            "expired", "expired-id", "entra", GitHubAppKind.Repo,
            GitHubAuthorizationPurpose.InteractiveRepository, DateTimeOffset.UtcNow.AddMinutes(-1)));
        await new TwoAppPersistenceStore(database).AddAuthorizationAsync(Transaction(
            "wrong-purpose", "purpose-id", "entra", GitHubAppKind.Repo,
            GitHubAuthorizationPurpose.InteractiveCopilot, DateTimeOffset.UtcNow.AddMinutes(1)));
        await new TwoAppPersistenceStore(database).AddAuthorizationAsync(Transaction(
            "wrong-app", "app-id", "entra", GitHubAppKind.Copilot,
            GitHubAuthorizationPurpose.InteractiveRepository, DateTimeOffset.UtcNow.AddMinutes(1)));

        (await service.CompleteAsync(Human("entra"), HumanPrincipal(), "expired", "code", "cookie"))
            .Outcome.Should().Be(RepoAppAuthorizationOutcome.AuthorizationTransactionInvalid);
        (await service.CompleteAsync(Human("entra"), HumanPrincipal(), "wrong-purpose", "code", "cookie"))
            .Outcome.Should().Be(RepoAppAuthorizationOutcome.AuthorizationTransactionInvalid);
        (await service.CompleteAsync(Human("entra"), HumanPrincipal(), "wrong-app", "code", "cookie"))
            .Outcome.Should().Be(RepoAppAuthorizationOutcome.AuthorizationTransactionInvalid);
    }

    [Fact]
    public async Task Poll_ExpiresAnInterruptedRedeemingTransaction()
    {
        await using var database = await OpenDatabaseAsync();
        var record = Transaction(
            "redeeming-state",
            "redeeming-id",
            "entra",
            GitHubAppKind.Repo,
            GitHubAuthorizationPurpose.InteractiveRepository,
            DateTimeOffset.UtcNow.AddMinutes(-1));
        record.Status = GitHubAuthorizationStatus.Redeeming;
        await new TwoAppPersistenceStore(database).AddAuthorizationAsync(record);
        var service = CreateService(database, new InMemorySecretStore(), new StubHttpClientFactory());

        var result = await service.PollAsync(Human("entra"), HumanPrincipal(), "redeeming-id");

        result.Status.Should().Be("expired");
        database.ChangeTracker.Clear();
        (await database.GitHubAuthorizations.SingleAsync()).Status.Should().Be(GitHubAuthorizationStatus.Expired);
    }

    [Fact]
    public async Task Callback_IsSingleUseIncludingConcurrentRedemption()
    {
        await using var connection = await OpenConnectionAsync();
        var options = Options(connection);
        await using var setup = new MemoryDbContext(options);
        await setup.Database.EnsureCreatedAsync();
        var secrets = new InMemorySecretStore();
        var first = CreateService(setup, secrets, new StubHttpClientFactory(TokenResponse()));
        var begin = await first.BeginAsync(Human("entra"), HumanPrincipal(), "projects");
        var state = Query(begin.AuthorizationUrl!, "state");

        await using var secondDb = new MemoryDbContext(options);
        var second = CreateService(secondDb, secrets, new StubHttpClientFactory(TokenResponse()));
        var results = await Task.WhenAll(
            first.CompleteAsync(Human("entra"), HumanPrincipal(), state, "code", begin.CallbackCookie),
            second.CompleteAsync(Human("entra"), HumanPrincipal(), state, "code", begin.CallbackCookie));

        results.Count(x => x.Outcome == RepoAppAuthorizationOutcome.Success).Should().Be(1);
        results.Count(x => x.Outcome == RepoAppAuthorizationOutcome.AuthorizationTransactionConsumed).Should().Be(1);
        (await setup.GitHubAppAuthorizations.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Refresh_PreservesStableGrantVersion_AndRevokeWritesTombstone()
    {
        await using var database = await OpenDatabaseAsync();
        var secrets = new InMemorySecretStore();
        var service = CreateService(database, secrets, new StubHttpClientFactory(
            TokenResponse(),
            TokenResponse("ghu_refreshed", "refresh-rotated")));
        var begin = await service.BeginAsync(Human("entra"), HumanPrincipal(), "settings");
        var completed = await service.CompleteAsync(
            Human("entra"), HumanPrincipal(), Query(begin.AuthorizationUrl!, "state"), "code", begin.CallbackCookie);
        completed.Outcome.Should().Be(RepoAppAuthorizationOutcome.Success);

        var grant = await database.GitHubAppAuthorizations.SingleAsync();
        var beforeRefresh = await secrets.GetSecretAsync(grant.CredentialReference);
        beforeRefresh.Value.Should().Contain("ghu_access").And.Contain("refresh-original");

        (await service.RefreshAsync(Human("entra"), HumanPrincipal())).Should().Be(RepoAppAuthorizationOutcome.Success);
        var afterRefresh = await secrets.GetSecretAsync(grant.CredentialReference);
        afterRefresh.Value.Should().Contain("ghu_refreshed").And.Contain("refresh-rotated");
        (await database.GitHubAppAuthorizations.SingleAsync()).CredentialVersion.Should().Be(grant.CredentialVersion);

        (await service.RevokeAsync(Human("entra"), HumanPrincipal())).Should().Be(RepoAppAuthorizationOutcome.Success);
        (await secrets.GetSecretAsync(grant.CredentialReference)).Value.Should().Contain("revoked")
            .And.NotContain("ghu_").And.NotContain("refresh-");
        database.ChangeTracker.Clear();
        (await database.GitHubAppAuthorizations.SingleAsync()).RevokedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ReauthorizationAndDisconnect_RevokeEveryPriorRepoAppCredential()
    {
        await using var database = await OpenDatabaseAsync();
        var secrets = new InMemorySecretStore();
        var service = CreateService(database, secrets, new StubHttpClientFactory());

        var first = await service.BeginAsync(Human("entra"), HumanPrincipal(), "settings");
        await service.CompleteAsync(
            Human("entra"), HumanPrincipal(), Query(first.AuthorizationUrl!, "state"), "first-code", first.CallbackCookie);
        database.ChangeTracker.Clear();
        var firstCredential = await database.GitHubAppAuthorizations.SingleAsync();

        var second = await service.BeginAsync(Human("entra"), HumanPrincipal(), "settings");
        await service.CompleteAsync(
            Human("entra"), HumanPrincipal(), Query(second.AuthorizationUrl!, "state"), "second-code", second.CallbackCookie);
        database.ChangeTracker.Clear();
        var afterReplacement = await database.GitHubAppAuthorizations.ToListAsync();
        afterReplacement.Should().HaveCount(2);
        afterReplacement.Single(x => x.Id == firstCredential.Id).RevokedAt.Should().NotBeNull();
        (await secrets.GetSecretAsync(firstCredential.CredentialReference)).Value.Should().Contain("revoked");

        (await service.RevokeAsync(Human("entra"), HumanPrincipal())).Should().Be(RepoAppAuthorizationOutcome.Success);
        database.ChangeTracker.Clear();
        (await database.GitHubAppAuthorizations.CountAsync(x => x.RevokedAt == null)).Should().Be(0);
        foreach (var credential in await database.GitHubAppAuthorizations.ToListAsync())
            (await secrets.GetSecretAsync(credential.CredentialReference)).Value.Should().Contain("revoked");
    }

    [Fact]
    public async Task Disconnect_InvalidatesOutstandingRepoAppTransactions()
    {
        await using var database = await OpenDatabaseAsync();
        var secrets = new InMemorySecretStore();
        var service = CreateService(database, secrets, new StubHttpClientFactory());
        var active = await service.BeginAsync(Human("entra"), HumanPrincipal(), "settings");
        await service.CompleteAsync(
            Human("entra"), HumanPrincipal(), Query(active.AuthorizationUrl!, "state"), "active-code", active.CallbackCookie);
        var pending = await service.BeginAsync(Human("entra"), HumanPrincipal(), "settings");

        (await service.RevokeAsync(Human("entra"), HumanPrincipal())).Should().Be(RepoAppAuthorizationOutcome.Success);
        database.ChangeTracker.Clear();
        (await database.GitHubAuthorizations.SingleAsync(
            x => x.State == Query(pending.AuthorizationUrl!, "state"))).Status.Should().Be(GitHubAuthorizationStatus.Failed);
        (await service.CompleteAsync(
            Human("entra"), HumanPrincipal(), Query(pending.AuthorizationUrl!, "state"), "late-code", pending.CallbackCookie))
            .Outcome.Should().Be(RepoAppAuthorizationOutcome.AuthorizationTransactionConsumed);
    }

    [Fact]
    public async Task PostClaimFailure_FinalizesTheTransactionAndTombstonesVerifier()
    {
        await using var database = await OpenDatabaseAsync();
        var secrets = new ThrowingGetSecretStore();
        var service = CreateService(database, secrets, new StubHttpClientFactory());
        var begin = await service.BeginAsync(Human("entra"), HumanPrincipal(), "settings");
        var state = Query(begin.AuthorizationUrl!, "state");

        (await service.CompleteAsync(Human("entra"), HumanPrincipal(), state, "code", begin.CallbackCookie))
            .Outcome.Should().Be(RepoAppAuthorizationOutcome.GitHubBindingUnavailable);
        database.ChangeTracker.Clear();
        var transaction = await database.GitHubAuthorizations.SingleAsync();
        transaction.Status.Should().Be(GitHubAuthorizationStatus.Failed);
        (await secrets.Inner.GetSecretAsync(transaction.PkceVerifierProtected)).Value.Should().Contain("revoked");
    }

    [Fact]
    public async Task HumanPredicate_DeniesInternalAndDoesNotUseCallerUserFallback()
    {
        await using var database = await OpenDatabaseAsync();
        var service = CreateService(database, new InMemorySecretStore(), new StubHttpClientFactory());
        var internalCaller = new CallerContext { User = "entra-looking-user", EntraObjectId = null };
        var internalPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("agentweaver_internal", "true")], "test"));

        HumanEntraSubjectAuthorization.Evaluate(internalCaller, internalPrincipal)
            .Should().Be(HumanEntraSubjectState.HumanEntraSubjectRequired);
        (await service.BeginAsync(internalCaller, internalPrincipal, "settings")).Outcome
            .Should().Be(RepoAppAuthorizationOutcome.HumanEntraSubjectRequired);
        (await service.PollAsync(internalCaller, internalPrincipal, "opaque")).Outcome
            .Should().Be(RepoAppAuthorizationOutcome.HumanEntraSubjectRequired);
    }

    [Fact]
    public async Task TransactionAndAuditSerialization_DoNotExposeProviderSecrets()
    {
        await using var database = await OpenDatabaseAsync();
        var secrets = new InMemorySecretStore();
        var service = CreateService(database, secrets, new StubHttpClientFactory(TokenResponse()));
        var begin = await service.BeginAsync(Human("entra"), HumanPrincipal(), "settings");
        await service.CompleteAsync(Human("entra"), HumanPrincipal(), Query(begin.AuthorizationUrl!, "state"), "code", begin.CallbackCookie);

        var serialized = System.Text.Json.JsonSerializer.Serialize(new
        {
            transaction = await database.GitHubAuthorizations.SingleAsync(),
            audit = await database.GitHubAuditRecords.SingleAsync(),
        });
        serialized.Should().NotContain("ghu_").And.NotContain("refresh-original")
            .And.NotContain("provider-sensitive-error").And.NotContain("code");
    }

    [Fact]
    public async Task ProviderErrorsAreClosedAndCallbackCookieUsesRequiredAttributes()
    {
        await using var database = await OpenDatabaseAsync();
        var service = CreateService(
            database,
            new InMemorySecretStore(),
            new StubHttpClientFactory("""{"error":"provider-sensitive-error"}"""));
        var begin = await service.BeginAsync(Human("entra"), HumanPrincipal(), "settings");
        var context = new DefaultHttpContext();
        RepoAppUserAuthorizationService.SetCallbackCookie(context, begin.CallbackCookie!);

        var result = await service.CompleteAsync(
            Human("entra"), HumanPrincipal(), Query(begin.AuthorizationUrl!, "state"), "code", begin.CallbackCookie);
        result.Outcome.Should().Be(RepoAppAuthorizationOutcome.GitHubBindingUnavailable);
        database.ChangeTracker.Clear();
        (await database.GitHubAuthorizations.SingleAsync()).Status.Should().Be(GitHubAuthorizationStatus.Failed);
        (await database.GitHubAuditRecords.SingleAsync()).ReasonCode.Should().Be(GitHubAuditReasonCode.TransactionInvalid);
        context.Response.Headers.SetCookie.Single().Should()
            .Contain("__Host-agentweaver-repo-app-auth=").And.Contain("path=/").And.Contain("samesite=lax").And.Contain("httponly").And.Contain("secure");
    }

    private static RepoAppUserAuthorizationService CreateService(
        MemoryDbContext database,
        ISecretStore secrets,
        IHttpClientFactory factory) =>
        new(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:RepoApp:ClientId"] = "repo-client",
                ["Auth:RepoApp:ClientSecret"] = "repo-secret",
                ["Auth:RepoApp:CallbackUrl"] = "https://agentweaver.test/auth/github/repo-app/callback",
                ["Auth:RepoApp:FrontendUrl"] = "https://agentweaver.test",
            }).Build(),
            new TwoAppPersistenceStore(database),
            secrets,
            factory);

    private static CallerContext Human(string subject) => new() { User = subject, EntraObjectId = subject };
    private static ClaimsPrincipal HumanPrincipal() =>
        new(new ClaimsIdentity([new Claim("oid", "entra")], "test"));

    private static GitHubAuthorizationRecord Transaction(
        string state,
        string id,
        string subject,
        GitHubAppKind app,
        GitHubAuthorizationPurpose purpose,
        DateTimeOffset expiry) => new()
        {
            State = state,
            ExternalTransactionId = id,
            AppKind = app,
            Purpose = purpose,
            EntraObjectId = subject,
            ExpiresAtUnixMilliseconds = expiry.ToUnixTimeMilliseconds(),
            ReturnRouteKey = "settings",
            PkceVerifierProtected = "pkce-reference",
            CallbackCookieHash = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("cookie"))),
            Status = GitHubAuthorizationStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    private static async Task<MemoryDbContext> OpenDatabaseAsync()
    {
        var connection = await OpenConnectionAsync();
        var database = new MemoryDbContext(Options(connection));
        await database.Database.EnsureCreatedAsync();
        return database;
    }

    private static async Task<SqliteConnection> OpenConnectionAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        return connection;
    }

    private static DbContextOptions<MemoryDbContext> Options(SqliteConnection connection) =>
        new DbContextOptionsBuilder<MemoryDbContext>().UseSqlite(connection).Options;

    private static string Query(string url, string name) =>
        new Uri(url).Query.TrimStart('?').Split('&')
            .Select(p => p.Split('=', 2))
            .Single(p => p[0] == name) is var pair
                ? Uri.UnescapeDataString(pair[1])
                : throw new InvalidOperationException();

    private static string TokenResponse(
        string accessToken = "ghu_access",
        string refreshToken = "refresh-original") =>
        $$"""{"access_token":"{{accessToken}}","refresh_token":"{{refreshToken}}","expires_in":3600,"error":null}""";

    private sealed class StubHttpClientFactory(params string[] responses) : IHttpClientFactory
    {
        private readonly Queue<string> _responses = new(responses.Length == 0 ? [TokenResponse()] : responses);
        public List<string> RequestBodies { get; } = [];

        public HttpClient CreateClient(string name) => new(new StubHandler(RequestBodies, _responses));
    }

    private sealed class StubHandler(List<string> bodies, Queue<string> responses) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responses.Count > 0 ? responses.Dequeue() : TokenResponse()),
            };
        }
    }

    private sealed class ThrowingGetSecretStore : ISecretStore
    {
        public InMemorySecretStore Inner { get; } = new();
        public Task<SecretGetResult> GetSecretAsync(string key, CancellationToken ct = default) =>
            throw new InvalidOperationException("storage failure");
        public Task<string> SetSecretAsync(string key, string value, string? etag = null, CancellationToken ct = default) =>
            Inner.SetSecretAsync(key, value, etag, ct);
        public Task DeleteSecretAsync(string key, CancellationToken ct = default) => Inner.DeleteSecretAsync(key, ct);
    }
}
