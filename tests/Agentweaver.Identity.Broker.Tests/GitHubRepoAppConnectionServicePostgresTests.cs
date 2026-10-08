using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.SourceControl;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

[Collection("IdentityBrokerPostgres")]
public sealed class GitHubRepoAppConnectionServicePostgresTests(PostgresContainerFixture postgres)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-08T10:00:00Z");

    [Fact]
    public async Task ConnectsBrowsesAndMintsRepositoryScopedTokensBoundToOneProject()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var dbOptions = new DbContextOptionsBuilder<IdentityBrokerDbContext>()
            .UseNpgsql(dataSource, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history", IdentityBrokerDbContext.Schema))
            .Options;
        await using var db = new IdentityBrokerDbContext(dbOptions);
        var time = new FrozenTimeProvider(Now);
        var ownerId = Guid.NewGuid();
        var otherOwnerId = Guid.NewGuid();
        db.Users.AddRange(
            NewUser(ownerId, "owner"),
            NewUser(otherOwnerId, "other-owner"));
        AddActiveRunBinding(db, ownerId, "project-one", "run-one");
        AddActiveRunBinding(db, ownerId, "project-two", "run-two");
        await db.SaveChangesAsync();

        using var rsa = RSA.Create(2048);
        var privateKey = rsa.ExportPkcs8PrivateKeyPem();
        var secretStore = new InMemorySecretStore();
        secretStore.Seed("github-app-private-key", "key-version-1", privateKey);
        var secretWriter = new TestSecretVersionWriter(secretStore);
        var secretRedemption = new TestSecretRedemption(secretStore, time);
        string? expectedChallenge = null;
        var oauthHandler = new StubHandler(async (request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/login/oauth/access_token", request.RequestUri!.AbsolutePath);
            var fields = ParseForm(await request.Content!.ReadAsStringAsync());
            Assert.Equal("authorization-code", fields["code"]);
            Assert.Equal("https://app.example/auth/github/repo-app/callback", fields["redirect_uri"]);
            var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(fields["code_verifier"])));
            Assert.Equal(expectedChallenge, challenge);
            return JsonResponse(
                "{\"access_token\":\"user-access-secret\",\"expires_in\":28800," +
                "\"refresh_token\":\"user-refresh-secret\",\"refresh_token_expires_in\":15897600}");
        });
        var repositoryApiPaths = new List<string>();
        var repositoryApiHandler = new StubHandler((request, _) =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("user-access-secret", request.Headers.Authorization.Parameter);
            repositoryApiPaths.Add(request.RequestUri!.PathAndQuery);
            var body = request.RequestUri.PathAndQuery switch
            {
                "/user" => "{\"login\":\"connected-user\"}",
                "/user/installations?per_page=100&page=1" =>
                    "{\"installations\":[{\"id\":456,\"account\":{\"login\":\"octo\"," +
                    "\"type\":\"Organization\"},\"repository_selection\":\"selected\"}]}",
                "/user/installations/456/repositories?per_page=100&page=1" =>
                    "{\"repositories\":[{\"id\":789,\"full_name\":\"octo/widget\"," +
                    "\"owner\":{\"login\":\"octo\"},\"private\":true,\"default_branch\":\"main\"}]}",
                _ => throw new Xunit.Sdk.XunitException(
                    $"Unexpected GitHub repository discovery path: {request.RequestUri.PathAndQuery}")
            };
            return Task.FromResult(JsonResponse(body));
        });
        var appMintRequests = 0;
        var returnedContentsPermission = "write";
        var appTokenHandler = new StubHandler(async (request, _) =>
        {
            appMintRequests++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/app/installations/456/access_tokens", request.RequestUri!.AbsolutePath);
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var body = document.RootElement;
            Assert.Equal(new long[] { 789 }, body.GetProperty("repository_ids")
                .EnumerateArray().Select(value => value.GetInt64()).ToArray());
            var permissions = body.GetProperty("permissions");
            Assert.Equal(2, permissions.EnumerateObject().Count());
            Assert.Equal("write", permissions.GetProperty("contents").GetString());
            Assert.Equal("write", permissions.GetProperty("pull_requests").GetString());
            return JsonResponse(
                "{\"token\":\"installation-token\",\"expires_at\":\"2026-10-08T10:47:00Z\"," +
                $"\"permissions\":{{\"contents\":\"{returnedContentsPermission}\",\"metadata\":\"read\"," +
                "\"pull_requests\":\"write\"}}");
        });

        using var oauthClient = new HttpClient(oauthHandler)
        {
            BaseAddress = new Uri("https://github.com/")
        };
        using var repositoryApiClient = new HttpClient(repositoryApiHandler)
        {
            BaseAddress = new Uri("https://api.github.com/")
        };
        using var appApiClient = new HttpClient(appTokenHandler)
        {
            BaseAddress = new Uri("https://api.github.com/")
        };
        var options = new GitHubRepoAppOptions
        {
            OAuthClientId = "repo-app-client",
            OAuthClientSecret = "repo-app-client-secret",
            CallbackUri = "https://app.example/auth/github/repo-app/callback",
            AppId = 123,
            AppSlug = "agentweaver",
            PrivateKeySecretId = "github-app-private-key",
            PrivateKeySecretVersion = "key-version-1"
        };
        var provider = new GitHubRepoAppProviderClient(
            oauthClient,
            repositoryApiClient,
            new GitHubRepoAppProviderOptions(
                options.OAuthClientId,
                options.OAuthClientSecret,
                new Uri(options.CallbackUri)),
            time);
        var service = new GitHubRepoAppConnectionService(
            db,
            options,
            provider,
            secretWriter,
            secretRedemption,
            new EphemeralDataProtectionProvider(),
            time);
        var grantAuthority = new IdentityGrantAuthority(db, time);
        var tokenIssuer = new GitHubAppInstallationTokenIssuer(appApiClient, options.AppId, time);

        var authorization = await service.BeginUserAuthorizationAsync(ownerId, CancellationToken.None);
        var query = QueryHelpers.ParseQuery(authorization.RedirectUri.Query);
        Assert.Equal("repo-app-client", query["client_id"].Single());
        Assert.Equal("S256", query["code_challenge_method"].Single());
        expectedChallenge = query["code_challenge"].Single();
        var state = query["state"].Single();

        var wrongOwner = await Assert.ThrowsAsync<GitHubRepoAppConnectionException>(() =>
            service.CompleteCallbackAsync(
                otherOwnerId, state, authorization.CallbackCookie, "authorization-code", null, null,
                CancellationToken.None));
        Assert.Equal(GitHubRepoAppConnectionFailure.AuthorizationInvalid, wrongOwner.Failure);
        var wrongCookie = await Assert.ThrowsAsync<GitHubRepoAppConnectionException>(() =>
            service.CompleteCallbackAsync(
                ownerId, state, "wrong-callback-cookie", "authorization-code", null, null,
                CancellationToken.None));
        Assert.Equal(GitHubRepoAppConnectionFailure.AuthorizationInvalid, wrongCookie.Failure);

        await service.CompleteCallbackAsync(
            ownerId, state, authorization.CallbackCookie, "authorization-code", null, null,
            CancellationToken.None);
        var replay = await Assert.ThrowsAsync<GitHubRepoAppConnectionException>(() =>
            service.CompleteCallbackAsync(
                ownerId, state, authorization.CallbackCookie, "authorization-code", null, null,
                CancellationToken.None));
        Assert.Equal(GitHubRepoAppConnectionFailure.AuthorizationInvalid, replay.Failure);

        var transaction = await db.RepoAppAuthorizationTransactions.AsNoTracking().SingleAsync();
        Assert.Equal(RepoAppAuthorizationState.Completed, transaction.State);
        Assert.NotEqual(state, transaction.StateHash);
        Assert.DoesNotContain(authorization.CallbackCookie, transaction.CallbackCookieHash);

        var installationSetup = await service.BeginInstallationSetupAsync(ownerId, CancellationToken.None);
        Assert.Equal("/apps/agentweaver/installations/new", installationSetup.RedirectUri.AbsolutePath);
        var setupQuery = QueryHelpers.ParseQuery(installationSetup.RedirectUri.Query);
        await service.CompleteCallbackAsync(
            ownerId,
            setupQuery["state"].Single(),
            installationSetup.CallbackCookie,
            null,
            456,
            "install",
            CancellationToken.None);
        var setupTransaction = await db.RepoAppAuthorizationTransactions.AsNoTracking()
            .SingleAsync(item => item.Purpose == RepoAppAuthorizationPurpose.InstallationSetup);
        Assert.Equal(RepoAppAuthorizationState.Completed, setupTransaction.State);
        Assert.Equal(456, setupTransaction.InstallationId);
        Assert.True(await db.RepoAppInstallations.AsNoTracking()
            .AnyAsync(item => item.InstallationId == 456 && item.RevokedAt == null));

        var connection = await db.RepoAppConnections.AsNoTracking().SingleAsync();
        Assert.Equal("connected-user", connection.GitHubLogin);
        Assert.NotEqual("user-access-secret", connection.AccessTokenSecretId);
        Assert.NotEqual("user-refresh-secret", connection.RefreshTokenSecretId);
        Assert.Equal("agentweaver-gh-app-" + connection.ConnectionId + "-access", connection.AccessTokenSecretId);
        Assert.Equal("agentweaver-gh-app-" + connection.ConnectionId + "-refresh", connection.RefreshTokenSecretId);

        var browser = await service.ListRepositoriesAsync(ownerId, CancellationToken.None);
        Assert.Equal(connection.ConnectionId, browser.ConnectionId);
        Assert.Equal(connection.ConnectionRevision, browser.ConnectionRevision);
        Assert.Equal("connected-user", browser.GitHubLogin);
        var repository = Assert.Single(browser.Repositories);
        Assert.Equal(456, repository.InstallationId);
        Assert.Equal(789, repository.RepositoryId);
        Assert.Equal("octo/widget", repository.FullName);
        Assert.True(repository.IsPrivate);
        Assert.Equal("main", repository.DefaultBranch);

        var selection = await service.CreateRepositorySelectionAsync(ownerId, 456, 789, CancellationToken.None);
        Assert.Matches("^[0-9a-f]{64}$", selection.Code);
        var selectionHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(selection.Code)));
        var storedSelection = await db.RepoAppRepositorySelections.AsNoTracking().SingleAsync();
        Assert.Equal(selectionHash, storedSelection.CodeHash);
        Assert.Null(storedSelection.ProjectId);
        Assert.Null(storedSelection.PermissionDigest);

        var firstMint = await service.MintInstallationTokenAsync(
            ownerId,
            "project-one",
            "run-one",
            new GitHubRepoAppInstallationTokenRequest(
                selection.Code, null, browser.ConnectionId, null, null, null, null, "octo/widget"),
            grantAuthority,
            tokenIssuer,
            CancellationToken.None);
        Assert.Equal("installation-token", firstMint.Credential.Credential.GetValue());
        Assert.Equal(Now.AddMinutes(47), firstMint.Credential.Credential.ExpiresAt);
        Assert.Equal(connection.ConnectionId, firstMint.ConnectionId);
        Assert.Equal("octo/widget", firstMint.RepositoryFullName);
        Assert.Equal(selectionHash, firstMint.SelectionHash);
        firstMint.Credential.Credential.Invalidate();
        var permissionDigest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            "github-app-permissions-v1\ncontents:write\nmetadata:read\npull_requests:write")));
        Assert.Equal(permissionDigest, (await db.RepoAppRepositorySelections.AsNoTracking().SingleAsync())
            .PermissionDigest);

        var crossProject = await Assert.ThrowsAsync<GitHubRepoAppConnectionException>(() =>
            service.MintInstallationTokenAsync(
                ownerId,
                "project-two",
                "run-two",
                new GitHubRepoAppInstallationTokenRequest(
                    null,
                    selectionHash,
                    browser.ConnectionId,
                    browser.ConnectionRevision,
                    456,
                    789,
                    permissionDigest,
                    "octo/widget"),
                grantAuthority,
                tokenIssuer,
                CancellationToken.None));
        Assert.Equal(GitHubRepoAppConnectionFailure.RepositoryUnavailable, crossProject.Failure);
        Assert.Equal(1, appMintRequests);

        var sameProjectMint = await service.MintInstallationTokenAsync(
            ownerId,
            "project-one",
            "run-one",
            new GitHubRepoAppInstallationTokenRequest(
                null,
                selectionHash,
                browser.ConnectionId,
                browser.ConnectionRevision,
                456,
                789,
                permissionDigest,
                "octo/widget"),
            grantAuthority,
            tokenIssuer,
            CancellationToken.None);
        Assert.Equal("installation-token", sameProjectMint.Credential.Credential.GetValue());
        Assert.Equal(2, appMintRequests);
        sameProjectMint.Credential.Credential.Invalidate();

        returnedContentsPermission = "read";
        var permissionChange = await Assert.ThrowsAsync<GitHubRepoAppConnectionException>(() =>
            service.MintInstallationTokenAsync(
                ownerId,
                "project-one",
                "run-one",
                new GitHubRepoAppInstallationTokenRequest(
                    null,
                    selectionHash,
                    browser.ConnectionId,
                    browser.ConnectionRevision,
                    456,
                    789,
                    permissionDigest,
                    "octo/widget"),
                grantAuthority,
                tokenIssuer,
                CancellationToken.None));
        Assert.Equal(GitHubRepoAppConnectionFailure.PermissionsChanged, permissionChange.Failure);
        Assert.Equal(3, appMintRequests);

        Assert.Equal(
            ["/user",
             "/user/installations?per_page=100&page=1",
             "/user/installations/456/repositories?per_page=100&page=1",
            "/user/installations?per_page=100&page=1",
             "/user/installations/456/repositories?per_page=100&page=1",
             "/user/installations?per_page=100&page=1",
             "/user/installations/456/repositories?per_page=100&page=1",
             "/user/installations?per_page=100&page=1",
             "/user/installations/456/repositories?per_page=100&page=1",
             "/user/installations?per_page=100&page=1",
             "/user/installations/456/repositories?per_page=100&page=1",
             "/user/installations?per_page=100&page=1",
             "/user/installations/456/repositories?per_page=100&page=1"],
            repositoryApiPaths);

        await using var rawConnection = await dataSource.OpenConnectionAsync();
        var persistedRows = await ReadRepoAppRowsAsync(rawConnection);
        Assert.DoesNotContain("user-access-secret", persistedRows, StringComparison.Ordinal);
        Assert.DoesNotContain("user-refresh-secret", persistedRows, StringComparison.Ordinal);
        Assert.DoesNotContain("installation-token", persistedRows, StringComparison.Ordinal);
        Assert.DoesNotContain(privateKey, persistedRows, StringComparison.Ordinal);
        Assert.Contains(("github-app-private-key", "key-version-1",
            SourceControlSecretPurposes.GitHubAppPrivateKey, "run-one"),
            secretRedemption.Requests);
    }

    [Fact]
    public async Task RefreshesRotatedOAuthTokensAndPersistsRevocationWhenRefreshIsRejected()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var dbOptions = new DbContextOptionsBuilder<IdentityBrokerDbContext>()
            .UseNpgsql(dataSource, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history", IdentityBrokerDbContext.Schema))
            .Options;
        await using var db = new IdentityBrokerDbContext(dbOptions);
        var time = new FrozenTimeProvider(Now);
        var ownerId = Guid.NewGuid();
        db.Users.Add(NewUser(ownerId, "refresh-owner"));
        db.RepoAppConnections.Add(new RepoAppConnectionRecord
        {
            ConnectionId = "connection-refresh-test",
            OwnerId = ownerId,
            GitHubLogin = "connected-user",
            AccessTokenSecretId = "repo-app-access",
            AccessTokenSecretVersion = "access-version-1",
            AccessTokenExpiresAt = Now.AddMinutes(1),
            RefreshTokenSecretId = "repo-app-refresh",
            RefreshTokenSecretVersion = "refresh-version-1",
            RefreshTokenExpiresAt = Now.AddDays(1),
            ConnectionRevision = 1,
            CredentialRevision = 1,
            State = RepoAppConnectionState.Connected,
            CreatedAt = Now,
            UpdatedAt = Now
        });
        await db.SaveChangesAsync();

        var secretStore = new InMemorySecretStore();
        secretStore.Seed("repo-app-access", "access-version-1", "expired-access-secret");
        secretStore.Seed("repo-app-refresh", "refresh-version-1", "old-refresh-secret");
        var secretWriter = new TestSecretVersionWriter(secretStore);
        var secretRedemption = new TestSecretRedemption(secretStore, time);
        var refreshCalls = 0;
        var refreshTokens = new List<string>();
        var oauthHandler = new StubHandler(async (request, _) =>
        {
            refreshCalls++;
            var fields = ParseForm(await request.Content!.ReadAsStringAsync());
            Assert.Equal("refresh_token", fields["grant_type"]);
            refreshTokens.Add(fields["refresh_token"]);
            if (refreshCalls == 1)
                return JsonResponse(
                    "{\"access_token\":\"fresh-access-secret\",\"expires_in\":3600," +
                    "\"refresh_token\":\"fresh-refresh-secret\",\"refresh_token_expires_in\":86400}");
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"error\":\"invalid_grant\"}", Encoding.UTF8, "application/json")
            };
        });
        var apiHandler = new StubHandler((request, _) =>
        {
            Assert.Equal("fresh-access-secret", request.Headers.Authorization!.Parameter);
            var body = request.RequestUri!.PathAndQuery switch
            {
                "/user/installations?per_page=100&page=1" =>
                    "{\"installations\":[{\"id\":456,\"account\":{\"login\":\"octo\"," +
                    "\"type\":\"Organization\"},\"repository_selection\":\"selected\"}]}",
                "/user/installations/456/repositories?per_page=100&page=1" =>
                    "{\"repositories\":[{\"id\":789,\"full_name\":\"octo/widget\"," +
                    "\"owner\":{\"login\":\"octo\"},\"private\":true,\"default_branch\":\"main\"}]}",
                _ => throw new Xunit.Sdk.XunitException(
                    $"Unexpected GitHub repository discovery path: {request.RequestUri.PathAndQuery}")
            };
            return Task.FromResult(JsonResponse(body));
        });
        using var oauthClient = new HttpClient(oauthHandler)
        {
            BaseAddress = new Uri("https://github.com/")
        };
        using var apiClient = new HttpClient(apiHandler)
        {
            BaseAddress = new Uri("https://api.github.com/")
        };
        var options = new GitHubRepoAppOptions
        {
            OAuthClientId = "repo-app-client",
            OAuthClientSecret = "repo-app-client-secret",
            CallbackUri = "https://app.example/auth/github/repo-app/callback",
            AppId = 123,
            AppSlug = "agentweaver",
            PrivateKeySecretId = "github-app-private-key",
            PrivateKeySecretVersion = "key-version-1"
        };
        var provider = new GitHubRepoAppProviderClient(
            oauthClient,
            apiClient,
            new GitHubRepoAppProviderOptions(
                options.OAuthClientId,
                options.OAuthClientSecret,
                new Uri(options.CallbackUri)),
            time);
        var service = new GitHubRepoAppConnectionService(
            db,
            options,
            provider,
            secretWriter,
            secretRedemption,
            new EphemeralDataProtectionProvider(),
            time);

        var browser = await service.ListRepositoriesAsync(ownerId, CancellationToken.None);
        Assert.Equal("connection-refresh-test", browser.ConnectionId);
        Assert.Equal("octo/widget", Assert.Single(browser.Repositories).FullName);
        var refreshed = await db.RepoAppConnections.AsNoTracking().SingleAsync();
        Assert.Equal(2, refreshed.CredentialRevision);
        Assert.Equal("version-3", refreshed.AccessTokenSecretVersion);
        Assert.Equal("version-4", refreshed.RefreshTokenSecretVersion);
        Assert.Equal(new[] { "old-refresh-secret" }, refreshTokens);

        await db.RepoAppConnections
            .Where(item => item.ConnectionId == refreshed.ConnectionId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.AccessTokenExpiresAt, Now.AddSeconds(30)));
        var revoked = await Assert.ThrowsAsync<GitHubRepoAppConnectionException>(() =>
            service.ListRepositoriesAsync(ownerId, CancellationToken.None));
        Assert.Equal(GitHubRepoAppConnectionFailure.Revoked, revoked.Failure);
        Assert.Equal(2, refreshCalls);
        Assert.Equal(
            new[] { "old-refresh-secret", "fresh-refresh-secret" },
            refreshTokens);
        var finalConnection = await db.RepoAppConnections.AsNoTracking().SingleAsync();
        Assert.Equal(RepoAppConnectionState.Revoked, finalConnection.State);
        Assert.Null(finalConnection.RefreshLeaseId);
        Assert.Equal(2, finalConnection.CredentialRevision);
    }

    [Fact]
    public async Task UncertainRefreshOutcomeLocksConnectionWithoutRetryingRotation()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var dbOptions = new DbContextOptionsBuilder<IdentityBrokerDbContext>()
            .UseNpgsql(dataSource, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history", IdentityBrokerDbContext.Schema))
            .Options;
        await using var db = new IdentityBrokerDbContext(dbOptions);
        var time = new FrozenTimeProvider(Now);
        var ownerId = Guid.NewGuid();
        db.Users.Add(NewUser(ownerId, "uncertain-owner"));
        db.RepoAppConnections.Add(new RepoAppConnectionRecord
        {
            ConnectionId = "connection-uncertain-test",
            OwnerId = ownerId,
            GitHubLogin = "connected-user",
            AccessTokenSecretId = "repo-app-access",
            AccessTokenSecretVersion = "access-version-1",
            AccessTokenExpiresAt = Now.AddMinutes(1),
            RefreshTokenSecretId = "repo-app-refresh",
            RefreshTokenSecretVersion = "refresh-version-1",
            RefreshTokenExpiresAt = Now.AddDays(1),
            ConnectionRevision = 1,
            CredentialRevision = 1,
            State = RepoAppConnectionState.Connected,
            CreatedAt = Now,
            UpdatedAt = Now
        });
        await db.SaveChangesAsync();

        var secretStore = new InMemorySecretStore();
        secretStore.Seed("repo-app-access", "access-version-1", "expired-access-secret");
        secretStore.Seed("repo-app-refresh", "refresh-version-1", "old-refresh-secret");
        var secretWriter = new TestSecretVersionWriter(secretStore);
        var secretRedemption = new TestSecretRedemption(secretStore, time);
        var refreshCalls = 0;
        var oauthHandler = new StubHandler((_, _) =>
        {
            refreshCalls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent(
                    "{\"error\":\"private-provider-detail\"}", Encoding.UTF8, "application/json")
            });
        });
        var apiHandler = new StubHandler((_, _) =>
            throw new Xunit.Sdk.XunitException("Discovery must not run after uncertain rotation."));
        using var oauthClient = new HttpClient(oauthHandler)
        {
            BaseAddress = new Uri("https://github.com/")
        };
        using var apiClient = new HttpClient(apiHandler)
        {
            BaseAddress = new Uri("https://api.github.com/")
        };
        var options = new GitHubRepoAppOptions
        {
            OAuthClientId = "repo-app-client",
            OAuthClientSecret = "repo-app-client-secret",
            CallbackUri = "https://app.example/auth/github/repo-app/callback",
            AppId = 123,
            AppSlug = "agentweaver",
            PrivateKeySecretId = "github-app-private-key",
            PrivateKeySecretVersion = "key-version-1"
        };
        var provider = new GitHubRepoAppProviderClient(
            oauthClient,
            apiClient,
            new GitHubRepoAppProviderOptions(
                options.OAuthClientId,
                options.OAuthClientSecret,
                new Uri(options.CallbackUri)),
            time);
        var service = new GitHubRepoAppConnectionService(
            db,
            options,
            provider,
            secretWriter,
            secretRedemption,
            new EphemeralDataProtectionProvider(),
            time);

        var firstFailure = await Assert.ThrowsAsync<GitHubRepoAppConnectionException>(() =>
            service.ListRepositoriesAsync(ownerId, CancellationToken.None));
        Assert.Equal(GitHubRepoAppConnectionFailure.RotationUncertain, firstFailure.Failure);
        Assert.DoesNotContain("private-provider-detail", firstFailure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("old-refresh-secret", firstFailure.ToString(), StringComparison.Ordinal);
        var repeatedFailure = await Assert.ThrowsAsync<GitHubRepoAppConnectionException>(() =>
            service.ListRepositoriesAsync(ownerId, CancellationToken.None));
        Assert.Equal(GitHubRepoAppConnectionFailure.RotationUncertain, repeatedFailure.Failure);
        Assert.Equal(1, refreshCalls);
        var connection = await db.RepoAppConnections.AsNoTracking().SingleAsync();
        Assert.Equal(RepoAppConnectionState.RotationUncertain, connection.State);
        Assert.Null(connection.RefreshLeaseId);
    }

    private static BrokerUser NewUser(Guid id, string subject) => new()
    {
        Id = id,
        Issuer = "https://identity.example/",
        Subject = subject,
        CreatedAt = Now
    };

    private static void AddActiveRunBinding(
        IdentityBrokerDbContext db,
        Guid ownerId,
        string projectId,
        string runId)
    {
        var grantId = "grant:" + projectId;
        db.SecretGrantHeads.Add(new SecretGrantHead { GrantId = grantId, CurrentRevision = 1 });
        db.SecretGrantRevisions.Add(new SecretGrantRevision
        {
            GrantId = grantId,
            Revision = 1,
            ActorId = ownerId.ToString(),
            ProjectId = projectId,
            RunId = runId,
            Purpose = SourceControlSecretPurposes.GitHubAppPrivateKey,
            SecretId = "github-app-private-key",
            SecretVersion = "key-version-1",
            State = GrantState.Active,
            ExpiresAt = Now.AddHours(1)
        });
    }

    private static async Task<string> ReadRepoAppRowsAsync(NpgsqlConnection connection)
    {
        var result = new StringBuilder();
        foreach (var table in new[]
                 {
                     "repo_app_authorization_transactions",
                     "repo_app_connections",
                     "repo_app_installations",
                     "repo_app_repository_selections"
                 })
        {
            await using var command = new NpgsqlCommand(
                $"SELECT COALESCE(json_agg(row_data)::text, '[]') FROM identity_broker.{table} AS row_data",
                connection);
            result.Append(await command.ExecuteScalarAsync());
        }

        return result.ToString();
    }

    private static Dictionary<string, string> ParseForm(string body) =>
        body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(
                pair => Uri.UnescapeDataString(pair[0].Replace('+', ' ')),
                pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')),
                StringComparer.Ordinal);

    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static HttpResponseMessage JsonResponse(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class FrozenTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class InMemorySecretStore
    {
        private readonly Dictionary<(string Id, string Version), string> _values = [];

        public void Seed(string id, string version, string value) => _values.Add((id, version), value);

        public string Read(SecretRef secret) =>
            _values.TryGetValue((secret.Id, secret.Version), out var value)
                ? value
                : throw new Xunit.Sdk.XunitException("The requested test secret was not available.");

        public SecretRef Write(string id, SecretCredential credential)
        {
            var version = "version-" + (_values.Count + 1);
            _values.Add((id, version), credential.GetValue());
            return new SecretRef(id, version);
        }
    }

    private sealed class TestSecretVersionWriter(InMemorySecretStore store) : ISecretVersionWriter
    {
        public Task<SecretRef> WriteVersionAsync(
            string secretId,
            SecretCredential credential,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(store.Write(secretId, credential));
        }
    }

    private sealed class TestSecretRedemption(
        InMemorySecretStore store,
        TimeProvider timeProvider) : ISecretRedemption
    {
        public List<(string Id, string Version, string Purpose, string RunId)> Requests { get; } = [];

        public Task<SecretCredential> RedeemAsync(
            SecretRedemptionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add((request.Secret.Id, request.Secret.Version, request.Purpose, request.RunId));
            var expiry = request.Purpose switch
            {
                "github-repo-app-user-access" => Now.AddHours(8),
                "github-repo-app-user-refresh" => Now.AddDays(180),
                SourceControlSecretPurposes.GitHubAppPrivateKey => Now.AddDays(1),
                _ => throw new Xunit.Sdk.XunitException("Unexpected test secret purpose.")
            };
            return Task.FromResult(new SecretCredential(store.Read(request.Secret), expiry, timeProvider));
        }
    }
}
