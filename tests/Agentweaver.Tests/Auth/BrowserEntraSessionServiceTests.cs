using Agentweaver.Api.Auth;
using Agentweaver.Api.Auth.OAuth;
using Agentweaver.Api.Memory;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Tests.Auth;

public sealed class BrowserEntraSessionServiceTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Issue_UsesEightHourAbsoluteExpiry_AndSecureHostCookie()
    {
        await using var database = await OpenDatabaseAsync();
        var clock = new ManualTimeProvider(Start);
        var service = new BrowserEntraSessionService(database, clock);
        var context = new DefaultHttpContext();

        var session = await service.IssueAsync(context, Claims(Start.AddMinutes(30)));

        session.ExpiresAt.Should().Be(Start.AddHours(8));
        session.ExpiresAt.Should().NotBe(Claims(Start.AddMinutes(30)).ExpiresAt);
        var cookie = context.Response.Headers.SetCookie.Single();
        cookie.Should().StartWith($"{BrowserEntraSessionService.CookieName}={session.Id}");
        cookie.Should().Contain("path=/");
        cookie.Should().Contain("secure");
        cookie.Should().Contain("httponly");
        cookie.Should().Contain("samesite=lax");
        cookie.Should().Contain($"expires={session.ExpiresAt:R}");
        cookie.Should().NotContain("domain=");
    }

    [Fact]
    public async Task GetCurrent_UsesAbsoluteExpiry_WithoutSlidingRenewal()
    {
        await using var database = await OpenDatabaseAsync();
        var clock = new ManualTimeProvider(Start);
        var service = new BrowserEntraSessionService(database, clock);
        var issueContext = new DefaultHttpContext();
        var issued = await service.IssueAsync(issueContext, Claims(Start.AddMinutes(30)));
        var originalExpiry = issued.ExpiresAt;

        clock.Advance(TimeSpan.FromHours(7));
        var activeContext = ContextWithCookie(issued.Id);
        (await service.GetCurrentAsync(activeContext)).Should().NotBeNull();
        activeContext.Response.Headers.SetCookie.Should().BeEmpty(
            "reading an active session must not slide or renew its absolute deadline");
        (await database.BrowserEntraSessions.FindAsync([issued.Id]))!.ExpiresAt.Should().Be(originalExpiry);

        clock.Advance(TimeSpan.FromHours(1));
        (await service.GetCurrentAsync(ContextWithCookie(issued.Id))).Should().BeNull();
    }

    [Fact]
    public async Task Reauthentication_ReplacesCurrentSession_WithFreshAbsoluteDeadlineAndIdentity()
    {
        await using var database = await OpenDatabaseAsync();
        var clock = new ManualTimeProvider(Start);
        var service = new BrowserEntraSessionService(database, clock);
        var first = await service.IssueAsync(new DefaultHttpContext(), Claims(Start.AddMinutes(30), "first-user"));

        clock.Advance(TimeSpan.FromHours(2));
        var renewalContext = ContextWithCookie(first.Id);
        var renewed = await service.IssueAsync(
            renewalContext,
            Claims(Start.AddMinutes(150), "renewed-user"));

        renewed.Id.Should().NotBe(first.Id);
        renewed.EntraObjectId.Should().Be("renewed-user");
        renewed.ExpiresAt.Should().Be(Start.AddHours(10));
        (await database.BrowserEntraSessions.FindAsync([first.Id])).Should().BeNull(
            "re-authentication revokes the browser session replaced by the new cookie");
        (await database.BrowserEntraSessions.FindAsync([renewed.Id])).Should().NotBeNull();
    }

    [Fact]
    public async Task Revoke_RemovesServerSession_AndExpiresCookieWithSameSecurityPolicy()
    {
        await using var database = await OpenDatabaseAsync();
        var service = new BrowserEntraSessionService(database, new ManualTimeProvider(Start));
        var issued = await service.IssueAsync(new DefaultHttpContext(), Claims(Start.AddMinutes(30)));
        var context = ContextWithCookie(issued.Id);

        await service.RevokeCurrentAsync(context);

        (await database.BrowserEntraSessions.FindAsync([issued.Id])).Should().BeNull();
        var cookie = context.Response.Headers.SetCookie.Single();
        cookie.Should().StartWith($"{BrowserEntraSessionService.CookieName}=");
        cookie.Should().Contain("expires=Thu, 01 Jan 1970 00:00:00 GMT");
        cookie.Should().Contain("path=/");
        cookie.Should().Contain("secure");
        cookie.Should().Contain("httponly");
        cookie.Should().Contain("samesite=lax");
        cookie.Should().NotContain("domain=");
    }

    private static DefaultHttpContext ContextWithCookie(string sessionId)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = $"{BrowserEntraSessionService.CookieName}={sessionId}";
        return context;
    }

    private static EntraAccessTokenClaims Claims(
        DateTimeOffset tokenExpiresAt,
        string objectId = "entra-user") =>
        new(
            objectId,
            "entra-tenant",
            "Agentweaver User",
            "user@example.com",
            [PlatformRoles.Contributor],
            [PlatformRoles.Contributor],
            PlatformRoles.Contributor,
            tokenExpiresAt);

    private static async Task<MemoryDbContext> OpenDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var database = new MemoryDbContext(
            new DbContextOptionsBuilder<MemoryDbContext>().UseSqlite(connection).Options);
        await database.Database.EnsureCreatedAsync();
        return database;
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }
}
