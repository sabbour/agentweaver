using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentweaver.Cli;
using Agentweaver.Cli.Api;
using Agentweaver.Gateway;

namespace Agentweaver.Cli.Tests;

[Collection(CliEnvironmentCollection.Name)]
public sealed class ByokDraftCommandsTests
{
    private const string ProviderId = "byok-platform-0001";
    private const string OperationId = "f1b62270-3320-414c-9c2a-803493e50131";
    private static readonly string[] TargetOptions =
    [
        "--gateway-url", "https://gateway.example/",
        "--broker-issuer", "https://identity.example/",
        "--api-audience", "https://gateway.example/",
        "--tenant-id", "tenant-1",
    ];

    [Fact]
    public async Task ListReadsEveryCursorPageAndReturnsOnlyMetadata()
    {
        using var fixture = new Fixture();
        var previousProject = Environment.GetEnvironmentVariable("AGENTWEAVER_PROJECT_ID");
        var previousRun = Environment.GetEnvironmentVariable("AGENTWEAVER_RUN_ID");
        Environment.SetEnvironmentVariable("AGENTWEAVER_PROJECT_ID", "ambient-project");
        Environment.SetEnvironmentVariable("AGENTWEAVER_RUN_ID", "ambient-run");
        fixture.Respond = request =>
        {
            if (request.Query.Contains("afterProviderId=" + ProviderId, StringComparison.Ordinal))
                return JsonResponse(Page([Provider("byok-platform-0002", "Second")], null));
            return JsonResponse(Page([Provider(ProviderId, "First", secret: "list-secret-canary")], ProviderId));
        };

        try
        {
            Assert.Equal(0, await fixture.Run("list"));

            var requests = fixture.Requests;
            Assert.Equal(2, requests.Count);
            Assert.All(requests, request => Assert.Contains("pageSize=100", request.Query, StringComparison.Ordinal));
            Assert.DoesNotContain("afterProviderId=", requests[0].Query, StringComparison.Ordinal);
            Assert.Contains("afterProviderId=" + ProviderId, requests[1].Query, StringComparison.Ordinal);
            var result = JsonNode.Parse(fixture.Output.ToString())!.AsObject();
            var providers = result["providers"]!.AsArray();
            Assert.Equal(["byok-platform-0001", "byok-platform-0002"],
                providers.Select(provider => provider!["providerId"]!.GetValue<string>()));
            Assert.DoesNotContain("list-secret-canary", fixture.Output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("hasCredential", fixture.Output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("state", fixture.Output.ToString(), StringComparison.Ordinal);
            Assert.Null(fixture.Authentication.Identity!.ProjectId);
            Assert.Null(fixture.Authentication.Identity.RunId);
            Assert.Contains("projects.admin", fixture.Authentication.Identity.Scopes);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENTWEAVER_PROJECT_ID", previousProject);
            Environment.SetEnvironmentVariable("AGENTWEAVER_RUN_ID", previousRun);
        }
    }

    [Fact]
    public async Task ListRejectsARepeatedProviderCursor()
    {
        using var fixture = new Fixture();
        fixture.Respond = _ => JsonResponse(Page([Provider(ProviderId, "First")], ProviderId));

        Assert.Equal(1, await fixture.Run("list"));

        Assert.Equal(2, fixture.Requests.Count);
        Assert.Empty(fixture.Output.ToString());
        Assert.Contains("invalid BYOK response", fixture.Error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ShowUsesProviderIdAndKeepsTheNameAsDisplayText()
    {
        using var fixture = new Fixture
        {
            Respond = _ => JsonResponse(Provider(ProviderId, "Mutable display name", secret: "response-secret-canary")),
        };

        Assert.Equal(0, await fixture.Run("show", ProviderId));

        Assert.Equal("/api/v1/platform/byok-providers/" + ProviderId, fixture.Requests.Single().Path);
        var result = JsonNode.Parse(fixture.Output.ToString())!.AsObject();
        Assert.Equal(ProviderId, result["providerId"]!.GetValue<string>());
        Assert.Equal("Mutable display name", result["name"]!.GetValue<string>());
        Assert.False(result.ContainsKey("reference"));
        Assert.DoesNotContain("response-secret-canary", fixture.Output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("show")]
    [InlineData("add")]
    [InlineData("update")]
    public async Task PlatformCommandsProjectAnExactReservedReference(string command)
    {
        var provider = Provider(ProviderId, "display");
        provider["reference"] = "Prod.Model-1";
        using var fixture = new Fixture
        {
            Respond = _ => command == "list" ? JsonResponse(Page([provider], null)) : JsonResponse(provider),
        };
        var args = command switch
        {
            "list" => new[] { "list" },
            "show" => ["show", ProviderId],
            "add" => ["add", "--name", "display", "--type", "openai", "--base-url",
                "https://models.example/", "--model", "deployment-a"],
            _ => new[] { "update", ProviderId, "--expected-revision", "1", "--name", "display",
                "--type", "openai", "--base-url", "https://models.example/", "--model", "deployment-a" },
        };
        Assert.Equal(0, await fixture.Run(args));
        var result = JsonNode.Parse(fixture.Output.ToString())!;
        var projected = command == "list" ? result["providers"]![0]! : result;
        Assert.Equal("Prod.Model-1", projected["reference"]!.GetValue<string>());
        Assert.Equal(ProviderId, projected["providerId"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("_private")]
    [InlineData("bad__reference")]
    [InlineData("secret-canary\n")]
    public async Task PlatformShowRejectsInvalidReferenceWithoutEcho(string reference)
    {
        var provider = Provider(ProviderId, "display");
        provider["reference"] = reference;
        using var fixture = new Fixture { Respond = _ => JsonResponse(provider) };
        Assert.Equal(1, await fixture.Run("show", ProviderId));
        Assert.Empty(fixture.Output.ToString());
        Assert.DoesNotContain("secret-canary", fixture.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddUsesTheApiProviderIdAndSendsOnlyMetadata()
    {
        const string apiGeneratedId = "byok-api-generated-42";
        using var fixture = new Fixture
        {
            Respond = _ => JsonResponse(Provider(apiGeneratedId, "Staging endpoint")),
        };

        Assert.Equal(0, await fixture.Run(
            "add",
            "--name", "Staging endpoint",
            "--type", "openai",
            "--base-url", "https://models.example/",
            "--model", "deployment-a",
            "--wire-api", "responses",
            "--reasoning-effort", "low",
            "--reasoning-effort", "high",
            "--operation-id", OperationId));

        var request = fixture.Requests.Single();
        Assert.Equal(HttpMethod.Post.Method, request.Method);
        Assert.Equal("/api/v1/platform/byok-providers", request.Path);
        var body = JsonNode.Parse(request.Body)!.AsObject();
        Assert.Equal(
            new[]
            {
                "azure_api_version", "base_url", "expectedRevision", "model", "name",
                "operationId", "prompt_capacity_tokens", "rate_card_id", "rate_card_version",
                "reasoning_efforts", "type", "wire_api",
            },
            body.Select(property => property.Key).Order(StringComparer.Ordinal));
        Assert.Equal(OperationId, body["operationId"]!.GetValue<string>());
        Assert.Equal(0, body["expectedRevision"]!.GetValue<int>());
        Assert.Equal(["low", "high"], body["reasoning_efforts"]!.AsArray()
            .Select(effort => effort!.GetValue<string>()));
        Assert.DoesNotContain("key", request.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", request.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pricing", request.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("budget", request.Body, StringComparison.OrdinalIgnoreCase);
        var result = JsonNode.Parse(fixture.Output.ToString())!.AsObject();
        Assert.Equal(apiGeneratedId, result["providerId"]!.GetValue<string>());
        Assert.Equal("Staging endpoint", result["name"]!.GetValue<string>());
        Assert.DoesNotContain("has_api_key", fixture.Output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("is_active", fixture.Output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateRequiresRevisionAndKeepsAProviderIdAfterRename()
    {
        using var fixture = new Fixture
        {
            Respond = _ => JsonResponse(Provider(ProviderId, "Renamed endpoint", revision: 2)),
        };

        Assert.Equal(1, await fixture.Run(
            "update", ProviderId,
            "--name", "Renamed endpoint",
            "--type", "openai",
            "--base-url", "https://models.example/",
            "--model", "deployment-b"));
        Assert.Empty(fixture.Requests);
        Assert.Contains("--expected-revision", fixture.Error.ToString(), StringComparison.Ordinal);

        fixture.Error.GetStringBuilder().Clear();
        Assert.Equal(0, await fixture.Run(
            "update", ProviderId,
            "--expected-revision", "1",
            "--name", "Renamed endpoint",
            "--type", "openai",
            "--base-url", "https://models.example/",
            "--model", "deployment-b",
            "--operation-id", OperationId));

        var request = fixture.Requests.Single();
        Assert.Equal(HttpMethod.Put.Method, request.Method);
        Assert.Equal("/api/v1/platform/byok-providers/" + ProviderId, request.Path);
        var body = JsonNode.Parse(request.Body)!.AsObject();
        Assert.Equal(1, body["expectedRevision"]!.GetValue<long>());
        Assert.Equal("Renamed endpoint", body["name"]!.GetValue<string>());
        var result = JsonNode.Parse(fixture.Output.ToString())!.AsObject();
        Assert.Equal(ProviderId, result["providerId"]!.GetValue<string>());
        Assert.Equal("Renamed endpoint", result["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task ReusingAnExplicitOperationIdReplaysTheSameWrite()
    {
        using var fixture = new Fixture
        {
            Respond = _ => throw new HttpRequestException("The response was lost."),
        };
        var arguments = new[]
        {
            "add", "--name", "Replayable provider", "--type", "openai",
            "--base-url", "https://models.example/", "--model", "deployment-a",
            "--operation-id", OperationId,
        };

        Assert.Equal(1, await fixture.Run(arguments));
        Assert.Contains(OperationId, fixture.Error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("same command", fixture.Error.ToString(), StringComparison.OrdinalIgnoreCase);
        fixture.Error.GetStringBuilder().Clear();
        fixture.Respond = _ => JsonResponse(Provider("byok-from-api", "Replayable provider"));
        Assert.Equal(0, await fixture.Run(arguments));

        Assert.Equal(2, fixture.Requests.Count);
        Assert.Equal(fixture.Requests[0].Body, fixture.Requests[1].Body);
        Assert.Equal(OperationId, JsonNode.Parse(fixture.Requests[1].Body)!["operationId"]!.GetValue<string>());
        Assert.Equal("byok-from-api",
            JsonNode.Parse(fixture.Output.ToString())!["providerId"]!.GetValue<string>());
    }

    [Fact]
    public async Task CanceledWriteShowsItsOperationIdForReplay()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Respond = _ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        };

        Assert.Equal(130, await fixture.Run(
            cancellation.Token,
            "add",
            "--name", "Canceled provider",
            "--type", "openai",
            "--base-url", "https://models.example/",
            "--model", "deployment-a"));

        Assert.Single(fixture.Requests);
        var generatedOperationId =
            JsonNode.Parse(fixture.Requests.Single().Body)!["operationId"]!.GetValue<string>();
        Assert.True(Guid.TryParse(generatedOperationId, out var parsedOperationId));
        Assert.NotEqual(Guid.Empty, parsedOperationId);
        Assert.Contains(generatedOperationId, fixture.Error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("write result is unknown", fixture.Error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnsafeMetadataConflictShowsOnlyProviderIdAndRevision()
    {
        using var fixture = new Fixture
        {
            Respond = _ => JsonResponse(
                """{"code":"model_provider_draft_metadata_requires_update","id":"byok-legacy-0001","revision":7,"base_url":"https://bad.example/?api_key=secret-canary"}""",
                HttpStatusCode.Conflict),
        };

        Assert.Equal(1, await fixture.Run("show", "byok-legacy-0001"));

        Assert.Equal(
            "BYOK_METADATA_REPAIR_REQUIRED providerId=byok-legacy-0001 revision=7" +
            Environment.NewLine,
            fixture.Error.ToString());
        Assert.DoesNotContain("secret-canary", fixture.Error.ToString(), StringComparison.Ordinal);
        Assert.Empty(fixture.Output.ToString());
    }

    [Fact]
    public async Task StaleRevisionFailsWithoutRetry()
    {
        using var fixture = new Fixture
        {
            Respond = _ => JsonResponse(
                """{"code":"model_provider_draft_conflict"}""",
                HttpStatusCode.Conflict),
        };

        Assert.Equal(1, await fixture.Run(
            "update", ProviderId,
            "--expected-revision", "1",
            "--name", "Changed",
            "--type", "openai",
            "--base-url", "https://models.example/",
            "--model", "deployment-b",
            "--operation-id", OperationId));

        Assert.Single(fixture.Requests);
        Assert.Empty(fixture.Output.ToString());
        Assert.Contains("BYOK_CONFLICT", fixture.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlatformCommandsRejectProjectSelectors()
    {
        using var fixture = new Fixture();

        Assert.Equal(1, await fixture.Run("list", "--project-id", "project-1"));

        Assert.Empty(fixture.Requests);
        Assert.Contains("Unknown option: --project-id", fixture.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectListReadsEveryPageWithAnExactProjectOnlyIdentity()
    {
        using var fixture = new Fixture();
        fixture.Respond = request =>
        {
            if (request.Query.Contains("afterProviderId=" + ProviderId, StringComparison.Ordinal))
                return JsonResponse(Page(
                    [Provider("byok-project-0002", "Second", scope: "project", projectId: "project-1")],
                    null));
            return JsonResponse(Page(
                [Provider(ProviderId, "First", scope: "project", projectId: "project-1", secret: "project-secret-canary")],
                ProviderId));
        };

        Assert.Equal(0, await fixture.RunProject("list"));

        Assert.Equal(2, fixture.Requests.Count);
        Assert.All(fixture.Requests, request =>
        {
            Assert.Equal("/api/v1/projects/project-1/byok-providers", request.Path);
            Assert.Contains("pageSize=100", request.Query, StringComparison.Ordinal);
        });
        Assert.Contains("afterProviderId=" + ProviderId, fixture.Requests[1].Query, StringComparison.Ordinal);
        Assert.Equal("project-1", fixture.Authentication.Identity!.ProjectId);
        Assert.Null(fixture.Authentication.Identity.RunId);
        Assert.Contains("projects.admin", fixture.Authentication.Identity.Scopes);
        Assert.DoesNotContain("project-secret-canary", fixture.Output.ToString(), StringComparison.Ordinal);
        Assert.Equal(
            ["byok-platform-0001", "byok-project-0002"],
            JsonNode.Parse(fixture.Output.ToString())!["providers"]!.AsArray()
                .Select(provider => provider!["providerId"]!.GetValue<string>()));
    }

    [Fact]
    public async Task ProjectShowAndAddUseTheSelectedProjectAndApiGeneratedProviderId()
    {
        const string apiGeneratedId = "byok-project-generated-42";
        using var fixture = new Fixture();
        fixture.Respond = request => request.Method == HttpMethod.Post.Method
            ? JsonResponse(Provider(apiGeneratedId, "Project endpoint", scope: "project", projectId: "project-1"))
            : JsonResponse(Provider(
                ProviderId, "Project endpoint", scope: "project", projectId: "project-1",
                secret: "project-response-secret"));

        Assert.Equal(0, await fixture.RunProject("show", ProviderId));
        Assert.Equal("/api/v1/projects/project-1/byok-providers/" + ProviderId, fixture.Requests.Single().Path);
        Assert.DoesNotContain("project-response-secret", fixture.Output.ToString(), StringComparison.Ordinal);

        fixture.Output.GetStringBuilder().Clear();
        Assert.Equal(0, await fixture.RunProject(
            "add",
            "--name", "Project endpoint",
            "--type", "openai",
            "--base-url", "https://models.example/",
            "--model", "deployment-a",
            "--operation-id", OperationId));

        var request = fixture.Requests.Last();
        Assert.Equal(HttpMethod.Post.Method, request.Method);
        Assert.Equal("/api/v1/projects/project-1/byok-providers", request.Path);
        Assert.Equal(OperationId, JsonNode.Parse(request.Body)!["operationId"]!.GetValue<string>());
        Assert.DoesNotContain("projectId", request.Body, StringComparison.Ordinal);
        Assert.Equal(apiGeneratedId,
            JsonNode.Parse(fixture.Output.ToString())!["providerId"]!.GetValue<string>());
    }

    [Fact]
    public async Task ProjectReadRejectsAResponseForAnotherProject()
    {
        using var fixture = new Fixture
        {
            Respond = _ => JsonResponse(
                Provider(ProviderId, "Other project", scope: "project", projectId: "project-2")),
        };

        Assert.Equal(1, await fixture.RunProject("show", ProviderId));

        Assert.Empty(fixture.Output.ToString());
        Assert.Contains("invalid BYOK response", fixture.Error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProjectUpdateUsesRevisionAndDenialNamesCurrentProjectAuthority()
    {
        using var fixture = new Fixture
        {
            Respond = _ => JsonResponse("""{"code":"model_provider_draft_conflict"}""", HttpStatusCode.Conflict),
        };

        Assert.Equal(1, await fixture.RunProject(
            "update", ProviderId,
            "--expected-revision", "1",
            "--name", "Changed",
            "--type", "openai",
            "--base-url", "https://models.example/",
            "--model", "deployment-b",
            "--operation-id", OperationId));

        var request = Assert.Single(fixture.Requests);
        Assert.Equal(HttpMethod.Put.Method, request.Method);
        Assert.Equal("/api/v1/projects/project-1/byok-providers/" + ProviderId, request.Path);
        Assert.Equal(1, JsonNode.Parse(request.Body)!["expectedRevision"]!.GetValue<long>());
        Assert.Contains("BYOK_CONFLICT", fixture.Error.ToString(), StringComparison.Ordinal);

        fixture.Requests.Clear();
        fixture.Error.GetStringBuilder().Clear();
        fixture.Respond = _ => JsonResponse("{}", HttpStatusCode.Forbidden);
        Assert.Equal(1, await fixture.RunProject("show", ProviderId));
        Assert.Equal("BYOK_FORBIDDEN" + Environment.NewLine, fixture.Error.ToString());
    }

    [Fact]
    public async Task ProjectWriteReplaysWithTheSameOperationIdAndPayload()
    {
        using var fixture = new Fixture
        {
            Respond = _ => throw new HttpRequestException("The response was lost."),
        };
        var arguments = new[]
        {
            "add", "--name", "Replayable project provider", "--type", "openai",
            "--base-url", "https://models.example/", "--model", "deployment-a",
            "--operation-id", OperationId,
        };

        Assert.Equal(1, await fixture.RunProject(arguments));
        Assert.Contains(OperationId, fixture.Error.ToString(), StringComparison.OrdinalIgnoreCase);
        fixture.Error.GetStringBuilder().Clear();
        fixture.Respond = _ => JsonResponse(Provider(
            "byok-project-from-api", "Replayable project provider",
            scope: "project", projectId: "project-1"));
        Assert.Equal(0, await fixture.RunProject(arguments));

        Assert.Equal(2, fixture.Requests.Count);
        Assert.Equal(fixture.Requests[0].Body, fixture.Requests[1].Body);
        Assert.Equal(OperationId, JsonNode.Parse(fixture.Requests[1].Body)!["operationId"]!.GetValue<string>());
        Assert.Equal("byok-project-from-api",
            JsonNode.Parse(fixture.Output.ToString())!["providerId"]!.GetValue<string>());
    }

    [Fact]
    public async Task ProjectCommandsRejectRunSelectors()
    {
        using var fixture = new Fixture();

        Assert.Equal(1, await fixture.RunProject("list", "--run-id", "run-1"));

        Assert.Empty(fixture.Requests);
        Assert.Contains("Unknown option: --run-id", fixture.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddAndUpdateSendAnOptionalPairedRateCardAttachment()
    {
        using var fixture = new Fixture
        {
            Respond = _ => JsonResponse(Provider(ProviderId, "Attached")),
        };

        Assert.Equal(0, await fixture.Run(
            "add", "--name", "Attached", "--type", "openai",
            "--base-url", "https://models.example/", "--model", "deployment-a",
            "--rate-card-id", "azure-gpt", "--rate-card-version", "2026-11"));
        var addBody = JsonNode.Parse(fixture.Requests.Single().Body)!.AsObject();
        Assert.Equal("azure-gpt", addBody["rate_card_id"]!.GetValue<string>());
        Assert.Equal("2026-11", addBody["rate_card_version"]!.GetValue<string>());

        fixture.Requests.Clear();
        Assert.Equal(0, await fixture.Run(
            "update", ProviderId, "--expected-revision", "1",
            "--name", "Cleared", "--type", "openai",
            "--base-url", "https://models.example/", "--model", "deployment-a"));
        var updateBody = JsonNode.Parse(fixture.Requests.Single().Body)!.AsObject();
        Assert.Null(updateBody["rate_card_id"]);
        Assert.Null(updateBody["rate_card_version"]);
    }

    [Fact]
    public async Task RateCardAttachmentOptionsAreAlwaysPaired()
    {
        using var fixture = new Fixture();

        Assert.Equal(1, await fixture.Run(
            "add", "--name", "Attached", "--type", "openai",
            "--base-url", "https://models.example/", "--model", "deployment-a",
            "--rate-card-id", "azure-gpt"));

        Assert.Empty(fixture.Requests);
        Assert.Contains("must be specified together", fixture.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectPricesIgnoresAmbientRunIdentity()
    {
        var lookup = PriceLookup(secret: "price-secret-canary");
        lookup["sourceUrl"] =
            "https://user:password@prices.azure.com/api/retail/prices?token=price-secret-canary";
        using var fixture = new Fixture
        {
            Respond = _ => JsonResponse(lookup),
        };
        var previousRun = Environment.GetEnvironmentVariable("AGENTWEAVER_RUN_ID");
        Environment.SetEnvironmentVariable("AGENTWEAVER_RUN_ID", "ambient-run");
        try
        {
            Assert.Equal(0, await fixture.RunProject(
                "prices", "--region", "eastus", "--search", "gpt-4.1"));

            var request = Assert.Single(fixture.Requests);
            Assert.Equal(HttpMethod.Get.Method, request.Method);
            Assert.Equal("/api/v1/projects/project-1/model-pricing/azure-retail", request.Path);
            Assert.Contains("region=eastus", request.Query, StringComparison.Ordinal);
            Assert.Contains("search=gpt-4.1", request.Query, StringComparison.Ordinal);
            Assert.Null(fixture.Authentication.Identity!.RunId);
            Assert.DoesNotContain("price-secret-canary", fixture.Output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("password", fixture.Output.ToString(), StringComparison.Ordinal);
            Assert.Contains("https://prices.azure.com/api/retail/prices",
                fixture.Output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENTWEAVER_RUN_ID", previousRun);
        }
    }

    [Theory]
    [InlineData("manual", "/api/v1/platform/model-pricing/rate-cards/card-1/v1")]
    [InlineData("azure-retail", "/api/v1/platform/model-pricing/azure-retail/rate-cards/card-1/v1")]
    public async Task SavePricesUsesTheSelectedPricingMode(string source, string expectedPath)
    {
        var id = source == "azure-retail" ? "azure-retail-prices.standard.eastus" : "card-1";
        var body = source == "manual"
            ? new JsonObject
            {
                ["runtimeRateCardSchemaVersion"] = 1,
                ["rateCard"] = new JsonObject
                {
                    ["id"] = id,
                    ["version"] = "v1",
                    ["meterSource"] = "byok.tokens",
                    ["unit"] = "USD",
                    ["nanoUnitsPerUnit"] = 1_000_000,
                    ["modelMultipliers"] = new JsonObject { ["gpt-4.1"] = 1 },
                },
                ["provenance"] = new JsonObject
                {
                    ["source"] = "manual",
                    ["sourceUrl"] = "https://example.test/prices",
                    ["retrievedOn"] = "2026-11-23T00:00:00Z",
                },
            }
            : new JsonObject
            {
                ["region"] = "eastus",
                ["modelId"] = "gpt-4.1",
                ["input"] = new JsonObject { ["meterId"] = "input", ["expectedUsdPer1M"] = 1 },
                ["output"] = new JsonObject { ["meterId"] = "output", ["expectedUsdPer1M"] = 2 },
            };
        string path = WriteFixtureFile(body);
        try
        {
            using var fixture = new Fixture
            {
                Respond = _ => JsonResponse(RateCard(source, id)),
            };

            Assert.Equal(0, await fixture.Run(
                "save-prices", id, "v1", "--source", source, "--input", path));

            var request = Assert.Single(fixture.Requests);
            Assert.Equal(HttpMethod.Put.Method, request.Method);
            Assert.Equal(expectedPath.Replace("card-1", id), request.Path);
            Assert.True(JsonNode.DeepEquals(body, JsonNode.Parse(request.Body)));
            Assert.DoesNotContain("secret-canary", fixture.Output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task CredentialLifecycleCommandsSendExactRevisionFencedBodies()
    {
        var providerReceipt = CredentialReceipt("published");
        using var fixture = new Fixture
        {
            Respond = _ => JsonResponse(providerReceipt),
        };

        Assert.Equal(0, await fixture.Run(
            "recheck-key", ProviderId,
            "--expected-revision", "4",
            "--expected-credential-revision", "7",
            "--operation-id", OperationId));
        var recheck = Assert.Single(fixture.Requests);
        Assert.Equal(HttpMethod.Put.Method, recheck.Method);
        Assert.Equal("/api/v1/platform/byok-providers/" + ProviderId + "/credential", recheck.Path);
        Assert.Equal(
            ["expectedCredentialRevision", "expectedProviderRevision", "operationId", "recheckExistingVersion"],
            JsonNode.Parse(recheck.Body)!.AsObject().Select(item => item.Key).Order());
        Assert.True(JsonNode.Parse(recheck.Body)!["recheckExistingVersion"]!.GetValue<bool>());

        fixture.Requests.Clear();
        fixture.Respond = _ => JsonResponse(CredentialReceipt("disabled"));
        Assert.Equal(0, await fixture.Run(
            "disable-key", ProviderId,
            "--expected-revision", "4",
            "--expected-credential-revision", "7",
            "--operation-id", OperationId));
        var disableBody = JsonNode.Parse(fixture.Requests.Single().Body)!.AsObject();
        Assert.False(disableBody["cancel"]!.GetValue<bool>());
        Assert.DoesNotContain("credentialVersion", fixture.Output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("configurationHash", fixture.Output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("quarantined", "BYOK_QUARANTINED")]
    [InlineData("publication_pending", "BYOK_PENDING_PUBLICATION")]
    public async Task CredentialUncertainReceiptsFailAndStayRedacted(string status, string code)
    {
        using var fixture = new Fixture
        {
            Respond = _ => JsonResponse(CredentialReceipt(status, secret: "credential-secret-canary")),
        };

        Assert.Equal(1, await fixture.Run(
            "recheck-key", ProviderId,
            "--expected-revision", "4",
            "--expected-credential-revision", "7",
            "--operation-id", OperationId));

        Assert.Equal(code + Environment.NewLine, fixture.Error.ToString());
        Assert.Empty(fixture.Output.ToString());
        Assert.DoesNotContain("credential-secret-canary", fixture.Error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "credential_write_quarantined", "BYOK_QUARANTINED")]
    [InlineData(HttpStatusCode.Conflict, "credential_slot_conflict", "BYOK_CONFLICT")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "credential_publication_pending", "BYOK_PENDING_PUBLICATION")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "credential_vault_unavailable", "BYOK_VAULT_UNAVAILABLE")]
    public async Task CredentialHttpErrorsUseFixedMessages(
        HttpStatusCode status, string code, string expected)
    {
        using var fixture = new Fixture
        {
            Respond = _ => JsonResponse(
                new JsonObject { ["code"] = code, ["raw"] = "private-response-canary" }, status),
        };

        Assert.Equal(1, await fixture.Run(
            "recheck-key", ProviderId,
            "--expected-revision", "4",
            "--expected-credential-revision", "7",
            "--operation-id", OperationId));

        Assert.Equal(expected + Environment.NewLine, fixture.Error.ToString());
        Assert.DoesNotContain("private-response-canary", fixture.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActivationCommandsSwitchSettingsRevisionOnThePinnedRoutes()
    {
        using var fixture = new Fixture
        {
            Respond = _ => JsonResponse(new JsonObject
            {
                ["mode"] = "active",
                ["activeProviderId"] = ProviderId,
                ["settingsRevision"] = 12,
            }),
        };

        Assert.Equal(0, await fixture.Run(
            "activate", ProviderId, "--expected-revision", "4",
            "--expected-settings-revision", "11"));
        var activate = Assert.Single(fixture.Requests);
        Assert.Equal(HttpMethod.Post.Method, activate.Method);
        Assert.Equal("/api/v1/platform/byok-providers/" + ProviderId + "/activate", activate.Path);
        Assert.Equal(4, JsonNode.Parse(activate.Body)!["expectedProviderRevision"]!.GetValue<long>());
        Assert.Equal(11, JsonNode.Parse(activate.Body)!["expectedSettingsRevision"]!.GetValue<long>());

        fixture.Requests.Clear();
        fixture.Respond = _ => JsonResponse(new JsonObject
        {
            ["mode"] = "explicitHosted",
            ["activeProviderId"] = null,
            ["settingsRevision"] = 13,
        });
        Assert.Equal(0, await fixture.Run(
            "deactivate", "--expected-settings-revision", "12"));
        var deactivate = Assert.Single(fixture.Requests);
        Assert.Equal("/api/v1/platform/byok-providers/deactivate", deactivate.Path);
        Assert.Equal(12, JsonNode.Parse(deactivate.Body)!["expectedSettingsRevision"]!.GetValue<long>());
    }

    [Fact]
    public async Task OpenApiSchemaDriftFailsBeforeDispatch()
    {
        using var fixture = new Fixture
        {
            TransformOpenApi = document =>
                document["components"]!["schemas"]!["ModelProviderDraftPlatformCreateRequest"]!
                    ["properties"]!.AsObject().Remove("rate_card_version"),
        };

        Assert.Equal(1, await fixture.Run(
            "add", "--name", "Drifted", "--type", "openai",
            "--base-url", "https://models.example/", "--model", "deployment-a"));

        Assert.Empty(fixture.Requests);
        Assert.Equal("BYOK_CONTRACT_MISMATCH" + Environment.NewLine, fixture.Error.ToString());
        Assert.DoesNotContain("models.example", fixture.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenApiCredentialFieldTypeDriftFailsBeforeDispatch()
    {
        using var fixture = new Fixture
        {
            TransformOpenApi = document =>
            {
                var field = document["components"]!["schemas"]!["ModelProviderCredentialPutRequest"]!
                    ["properties"]!["recheckExistingVersion"]!.AsObject();
                field["anyOf"]![0]!["type"] = "string";
            },
        };

        Assert.Equal(1, await fixture.Run(
            "recheck-key", ProviderId,
            "--expected-revision", "4",
            "--expected-credential-revision", "7",
            "--operation-id", OperationId));

        Assert.Empty(fixture.Requests);
        Assert.Equal("BYOK_CONTRACT_MISMATCH" + Environment.NewLine, fixture.Error.ToString());
    }

    [Fact]
    public async Task OpenApiNestedReferenceDriftFailsBeforeDispatch()
    {
        using var fixture = new Fixture
        {
            TransformOpenApi = document =>
                document["components"]!["schemas"]!["ManualRuntimeRateCardWriteRequest"]!
                    ["properties"]!["rateCard"]!["$ref"] = "#/components/schemas/AzureRetailMeterSelection",
        };
        var body = ManualRateCard("card-1");
        var path = WriteFixtureFile(body);
        try
        {
            Assert.Equal(1, await fixture.Run(
                "save-prices", "card-1", "v1", "--source", "manual", "--input", path));
            Assert.Empty(fixture.Requests);
            Assert.Equal("BYOK_CONTRACT_MISMATCH" + Environment.NewLine, fixture.Error.ToString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ListAcceptsLegacyAndCurrentModelProviderDraftPageSchemasAndRejectsInvalidActiveProviderRevision()
    {
        // Older Gateway schema without activeProviderRevision still succeeds.
        using (var legacyFixture = new Fixture
        {
            TransformOpenApi = document =>
                document["components"]!["schemas"]!["ModelProviderDraftPage"]!
                    ["properties"]!.AsObject().Remove("activeProviderRevision"),
            Respond = _ => JsonResponse(Page([Provider(ProviderId, "First")], null)),
        })
        {
            Assert.Equal(0, await legacyFixture.Run("list"));
            Assert.Single(legacyFixture.Requests);
        }

        // Current Gateway schema with optional nullable integer activeProviderRevision succeeds.
        using (var currentFixture = new Fixture
        {
            Respond = _ =>
            {
                var page = JsonNode.Parse(Page([Provider(ProviderId, "First")], null))!.AsObject();
                page["activeProviderRevision"] = 3;
                return JsonResponse(page);
            },
        })
        {
            Assert.Equal(0, await currentFixture.Run("list"));
            Assert.Single(currentFixture.Requests);
        }

        // Invalid activeProviderRevision type fails before dispatch.
        using (var badTypeFixture = new Fixture
        {
            TransformOpenApi = document =>
            {
                var field = document["components"]!["schemas"]!["ModelProviderDraftPage"]!
                    ["properties"]!["activeProviderRevision"]!.AsObject();
                field["anyOf"]![0]!["type"] = "string";
            },
        })
        {
            Assert.Equal(1, await badTypeFixture.Run("list"));
            Assert.Empty(badTypeFixture.Requests);
            Assert.Equal("BYOK_CONTRACT_MISMATCH" + Environment.NewLine, badTypeFixture.Error.ToString());
        }

        // Required activeProviderRevision fails before dispatch.
        using (var requiredFixture = new Fixture
        {
            TransformOpenApi = document =>
                document["components"]!["schemas"]!["ModelProviderDraftPage"]!
                    ["required"]!.AsArray().Add("activeProviderRevision"),
        })
        {
            Assert.Equal(1, await requiredFixture.Run("list"));
            Assert.Empty(requiredFixture.Requests);
            Assert.Equal("BYOK_CONTRACT_MISMATCH" + Environment.NewLine, requiredFixture.Error.ToString());
        }
    }

    [Fact]
    public async Task SavePricesRejectsCredentialBearingSourceUrlBeforeDispatch()
    {
        var body = ManualRateCard("card-1");
        body["provenance"]!["sourceUrl"] =
            "https://example.test/prices?api_key=secret-canary";
        var path = WriteFixtureFile(body);
        try
        {
            using var fixture = new Fixture();
            Assert.Equal(1, await fixture.Run(
                "save-prices", "card-1", "v1", "--source", "manual", "--input", path));

            Assert.Empty(fixture.Requests);
            Assert.DoesNotContain("secret-canary", fixture.Error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SavePricesSanitizesReceiptSourceUrlOnStdout()
    {
        var body = ManualRateCard("card-1");
        var path = WriteFixtureFile(body);
        try
        {
            var receipt = RateCard("manual");
            receipt["provenance"]!["sourceUrl"] =
                "https://user:password@example.test/prices?token=secret-canary#fragment";
            using var fixture = new Fixture { Respond = _ => JsonResponse(receipt) };

            Assert.Equal(0, await fixture.Run(
                "save-prices", "card-1", "v1", "--source", "manual", "--input", path));

            Assert.Contains("https://example.test/prices", fixture.Output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("password", fixture.Output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("secret-canary", fixture.Output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AzureRetailRateCardIdMustMatchTheRequestRegionBeforeDispatch()
    {
        var body = new JsonObject
        {
            ["region"] = "westus",
            ["modelId"] = "gpt-4.1",
            ["input"] = new JsonObject { ["meterId"] = "input", ["expectedUsdPer1M"] = 1 },
            ["output"] = new JsonObject { ["meterId"] = "output", ["expectedUsdPer1M"] = 2 },
        };
        var path = WriteFixtureFile(body);
        try
        {
            using var fixture = new Fixture();
            Assert.Equal(1, await fixture.Run(
                "save-prices", "azure-retail-prices.standard.eastus", "v1",
                "--source", "azure-retail", "--input", path));

            Assert.Empty(fixture.Requests);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task HelpListsRuntimeCommandsIncludingSetKey()
    {
        using var output = new StringWriter();

        Assert.Equal(0, await CliApplication.RunAsync(
            ["--help"], new CliRuntimeOptions(Output: output)));

        Assert.Contains("byok save-prices", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("byok set-key", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("byok recheck-key", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("byok disable-key", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetKeyPreservesUtf8BytesStripsSingleTrailingCrLfAndOmitsEvidence()
    {
        const string secret = "sk-utf8-\u03C0-\U0001F511";
        using var fixture = new Fixture();
        using var stdin = new MemoryStream(Encoding.UTF8.GetBytes(secret + "\r\n"));
        fixture.StandardInput = stdin;
        fixture.StandardInputRedirected = true;
        fixture.Respond = request =>
        {
            if (request.Method == "GET" && request.Path == $"/api/v1/platform/byok-providers/{ProviderId}")
                return JsonResponse(Provider(ProviderId, "Primary", revision: 3));
            if (request.Method == "GET" && request.Path.EndsWith($"/credential/operations/{OperationId}", StringComparison.Ordinal))
                return JsonResponse(new JsonObject { ["code"] = "credential_operation_not_found" }, HttpStatusCode.NotFound);
            if (request.Method == "PUT" && request.Path == $"/api/v1/platform/byok-providers/{ProviderId}/credential")
                return JsonResponse(CredentialReceipt("published", secret));
            return JsonResponse("{}", HttpStatusCode.NotFound);
        };

        var exit = await fixture.Run(
            "set-key", ProviderId, "--from-stdin",
            "--expected-revision", "3", "--operation-id", OperationId);

        Assert.Equal(0, exit);
        var put = Assert.Single(fixture.Requests, r => r.Method == "PUT");
        Assert.Equal("application/octet-stream", put.ContentType);
        Assert.Equal("3", put.Headers["If-Match"]);
        Assert.Equal(OperationId, put.Headers["Idempotency-Key"]);
        Assert.Equal(Encoding.UTF8.GetBytes(secret), put.BodyBytes);
        Assert.DoesNotContain(secret, fixture.Output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, fixture.Error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.LastCreatedClient!.Calls, call =>
            call.ToJsonString().Contains(secret, StringComparison.Ordinal) ||
            GatewayContract.String(call, "operationId") == "setPlatformByokProviderCredential");
    }

    [Fact]
    public async Task SetKeyProjectIgnoresAmbientRunIdAndUsesRunlessProjectIdentity()
    {
        var previousRunId = Environment.GetEnvironmentVariable("AGENTWEAVER_RUN_ID");
        Environment.SetEnvironmentVariable("AGENTWEAVER_RUN_ID", "run-ambient-999");
        try
        {
            const string secret = "sk-proj-\u00E9";
            using var fixture = new Fixture();
            using var stdin = new MemoryStream(Encoding.UTF8.GetBytes(secret + "\n"));
            fixture.StandardInput = stdin;
            fixture.StandardInputRedirected = true;
            fixture.Respond = request =>
            {
                if (request.Method == "GET" && request.Path == $"/api/v1/projects/project-1/byok-providers/{ProviderId}")
                    return JsonResponse(Provider(ProviderId, "Proj", revision: 2, scope: "project", projectId: "project-1"));
                if (request.Method == "GET" && request.Path.EndsWith($"/credential/operations/{OperationId}", StringComparison.Ordinal))
                    return JsonResponse(new JsonObject { ["code"] = "credential_operation_not_found" }, HttpStatusCode.NotFound);
                if (request.Method == "PUT" && request.Path == $"/api/v1/projects/project-1/byok-providers/{ProviderId}/credential")
                {
                    return JsonResponse(new JsonObject
                    {
                        ["operationId"] = OperationId,
                        ["status"] = "published",
                        ["scope"] = "project",
                        ["projectId"] = "project-1",
                        ["providerId"] = ProviderId,
                        ["providerRevision"] = 3,
                        ["credentialRevision"] = 1,
                    });
                }
                return JsonResponse("{}", HttpStatusCode.NotFound);
            };

            var exit = await fixture.RunProject(
                "set-key", ProviderId, "--from-stdin",
                "--expected-revision", "2", "--operation-id", OperationId);

            Assert.Equal(0, exit);
            Assert.Equal("project-1", fixture.Authentication.Identity?.ProjectId);
            Assert.Null(fixture.Authentication.Identity?.RunId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENTWEAVER_RUN_ID", previousRunId);
        }
    }

    [Fact]
    public async Task SetKeyDoesNotReadStdinOnExistingOperationOrStaleRevision()
    {
        using var fixture = new Fixture();
        using var trackingStdin = new ReadTrackingStream(Encoding.UTF8.GetBytes("sk-never-read\n"));
        fixture.StandardInput = trackingStdin;
        fixture.StandardInputRedirected = true;
        fixture.Respond = request =>
        {
            if (request.Method == "GET" && request.Path == $"/api/v1/platform/byok-providers/{ProviderId}")
                return JsonResponse(Provider(ProviderId, "Primary", revision: 3));
            if (request.Method == "GET" && request.Path.EndsWith($"/credential/operations/{OperationId}", StringComparison.Ordinal))
            {
                return JsonResponse(new JsonObject
                {
                    ["operationId"] = OperationId,
                    ["scope"] = "platform",
                    ["projectId"] = null,
                    ["providerId"] = ProviderId,
                    ["status"] = "published",
                    ["providerRevision"] = 4,
                    ["hasCredential"] = true,
                });
            }
            return JsonResponse("{}", HttpStatusCode.NotFound);
        };

        var exitExisting = await fixture.Run(
            "set-key", ProviderId, "--from-stdin",
            "--expected-revision", "3", "--operation-id", OperationId);

        Assert.Equal(1, exitExisting);
        Assert.Equal(0, trackingStdin.ReadCalls);
        Assert.DoesNotContain(fixture.Requests, r => r.Method == "PUT");

        var exitStale = await fixture.Run(
            "set-key", ProviderId, "--from-stdin",
            "--expected-revision", "2", "--operation-id", OperationId);
        Assert.Equal(1, exitStale);
        Assert.Equal(0, trackingStdin.ReadCalls);
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xFE })]
    [InlineData(new byte[] { (byte)' ', (byte)'\t', (byte)'\n' })]
    [InlineData(new byte[] { (byte)'\n' })]
    public async Task SetKeyRejectsInvalidUtf8WhitespaceAndEmptyWithoutSendingPut(byte[] rawStdin)
    {
        using var fixture = new Fixture();
        using var stdin = new MemoryStream(rawStdin);
        fixture.StandardInput = stdin;
        fixture.StandardInputRedirected = true;
        fixture.Respond = request =>
        {
            if (request.Method == "GET" && request.Path == $"/api/v1/platform/byok-providers/{ProviderId}")
                return JsonResponse(Provider(ProviderId, "Primary", revision: 1));
            if (request.Method == "GET" && request.Path.EndsWith($"/credential/operations/{OperationId}", StringComparison.Ordinal))
                return JsonResponse(new JsonObject { ["code"] = "credential_operation_not_found" }, HttpStatusCode.NotFound);
            return JsonResponse("{}", HttpStatusCode.NotFound);
        };

        var exit = await fixture.Run(
            "set-key", ProviderId, "--from-stdin",
            "--expected-revision", "1", "--operation-id", OperationId);

        Assert.Equal(1, exit);
        Assert.DoesNotContain(fixture.Requests, r => r.Method == "PUT");
    }

    [Fact]
    public async Task SetKeyRecoversViaGetOnLostPutResponseWithoutResendingKey()
    {
        const string secret = "sk-recover-123";
        using var fixture = new Fixture();
        using var stdin = new MemoryStream(Encoding.UTF8.GetBytes(secret + "\n"));
        fixture.StandardInput = stdin;
        fixture.StandardInputRedirected = true;
        var getStatusCalls = 0;
        fixture.Respond = request =>
        {
            if (request.Method == "GET" && request.Path == $"/api/v1/platform/byok-providers/{ProviderId}")
                return JsonResponse(Provider(ProviderId, "Primary", revision: 1));
            if (request.Method == "GET" && request.Path.EndsWith($"/credential/operations/{OperationId}", StringComparison.Ordinal))
            {
                getStatusCalls++;
                return getStatusCalls == 1
                    ? JsonResponse(new JsonObject { ["code"] = "credential_operation_not_found" }, HttpStatusCode.NotFound)
                    : JsonResponse(new JsonObject
                    {
                        ["operationId"] = OperationId,
                        ["scope"] = "platform",
                        ["projectId"] = null,
                        ["providerId"] = ProviderId,
                        ["status"] = "published",
                        ["providerRevision"] = 2,
                        ["hasCredential"] = true,
                    });
            }
            if (request.Method == "PUT")
                return JsonResponse("{\"code\":\"unavailable\"}", HttpStatusCode.ServiceUnavailable);
            return JsonResponse("{}", HttpStatusCode.NotFound);
        };

        var exit = await fixture.Run(
            "set-key", ProviderId, "--from-stdin",
            "--expected-revision", "1", "--operation-id", OperationId);

        Assert.Equal(0, exit);
        Assert.Single(fixture.Requests, r => r.Method == "PUT");
        Assert.Equal(2, getStatusCalls);
        Assert.DoesNotContain(secret, fixture.Output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, fixture.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GatewayContractParsesOctetStreamWhileGenericInvokeRejectsNonJsonRequestBody()
    {
        using var fixture = new Fixture();
        var identity = CliConfiguration.ReadIdentity(
            ["projects.admin"],
            options: CliOptions.Parse(TargetOptions, new HashSet<string>(StringComparer.Ordinal)),
            unbound: true);
        var client = fixture.Create(identity);
        var doc = await client.DiscoverAsync(CancellationToken.None);
        var ops = GatewayContract.Parse(doc);
        var setOp = ops["setPlatformByokProviderCredential"];
        Assert.Contains("application/octet-stream", setOp.RequestBodyMediaTypes!);
        Assert.Contains("application/json", setOp.RequestBodyMediaTypes!);

        fixture.TransformOpenApi = openApi =>
        {
            var content = openApi["paths"]!["/api/v1/platform/byok-providers/{providerId}/credential"]!["put"]!["requestBody"]!["content"]!.AsObject();
            content.Remove("application/json");
        };
        var binaryOnlyClient = fixture.Create(identity);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => binaryOnlyClient.InvokeAsync(
            "setPlatformByokProviderCredential",
            new GatewayInvocation(
                new Dictionary<string, string> { ["providerId"] = ProviderId },
                Body: new JsonObject { ["operationId"] = OperationId }),
            CancellationToken.None));
        Assert.Contains("JSON request body", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not_set", 1L, 0L)]
    [InlineData("enabled", 1L, 1L)]
    [InlineData("disabled", 2L, 3L)]
    [InlineData("pending", 1L, 0L)]
    [InlineData("pending", 2L, 1L)]
    [InlineData("quarantined", 1L, 0L)]
    [InlineData("quarantined", 5_000_000_000L, 6_000_000_000L)]
    public async Task ProjectShowCredentialStatusAcceptsAllValidStateRevisionPairs(
        string state,
        long providerRevision,
        long credentialRevision)
    {
        var previousRunId = Environment.GetEnvironmentVariable("AGENTWEAVER_RUN_ID");
        Environment.SetEnvironmentVariable("AGENTWEAVER_RUN_ID", "ambient-run-ignored");
        try
        {
            using var fixture = new Fixture();
            using var stdin = new ReadTrackingStream("unused-stdin"u8.ToArray());
            fixture.StandardInput = stdin;
            fixture.StandardInputRedirected = true;
            fixture.Respond = _ => JsonResponse(new JsonObject
            {
                ["scope"] = "project",
                ["projectId"] = "project-1",
                ["providerId"] = ProviderId,
                ["providerRevision"] = providerRevision,
                ["state"] = state,
                ["credentialRevision"] = credentialRevision,
            });

            var exit = await fixture.RunProject("show", ProviderId, "--credential-status");

            Assert.Equal(0, exit);
            Assert.Equal(string.Empty, fixture.Error.ToString());
            Assert.Equal(0, stdin.ReadCalls);
            Assert.Equal("project-1", fixture.Authentication.Identity?.ProjectId);
            Assert.Null(fixture.Authentication.Identity?.RunId);

            var request = Assert.Single(fixture.Requests);
            Assert.Equal("GET", request.Method);
            Assert.Equal($"/api/v1/projects/project-1/byok-providers/{ProviderId}/credential", request.Path);
            Assert.Equal(string.Empty, request.Query);
            Assert.Empty(request.BodyBytes);
            Assert.False(request.Headers.ContainsKey("X-Agentweaver-Project-Authorization"));

            var parsed = JsonNode.Parse(fixture.Output.ToString())!.AsObject();
            Assert.Equal(6, parsed.Count);
            Assert.Equal("project", parsed["scope"]?.GetValue<string>());
            Assert.Equal("project-1", parsed["projectId"]?.GetValue<string>());
            Assert.Equal(ProviderId, parsed["providerId"]?.GetValue<string>());
            Assert.Equal(providerRevision, parsed["providerRevision"]?.GetValue<long>());
            Assert.Equal(state, parsed["state"]?.GetValue<string>());
            Assert.Equal(credentialRevision, parsed["credentialRevision"]?.GetValue<long>());

            var call = Assert.Single(fixture.LastCreatedClient!.Calls,
                c => c["operationId"]?.GetValue<string>() == "readProjectByokProviderCredential");
            Assert.Equal("[REDACTED]", call["responseBody"]?.GetValue<string>());
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENTWEAVER_RUN_ID", previousRunId);
        }
    }

    [Theory]
    [InlineData("""{"scope":"project","projectId":"project-1","providerId":"provider-1","providerRevision":1,"state":"not_set","credentialRevision":1}""")]
    [InlineData("""{"scope":"project","projectId":"project-1","providerId":"provider-1","providerRevision":1,"state":"enabled","credentialRevision":0}""")]
    [InlineData("""{"scope":"project","projectId":"project-1","providerId":"provider-1","providerRevision":1,"state":"disabled","credentialRevision":0}""")]
    [InlineData("""{"scope":"project","projectId":"project-1","providerId":"provider-1","providerRevision":0,"state":"enabled","credentialRevision":1}""")]
    [InlineData("""{"scope":"project","projectId":"project-1","providerId":"provider-1","providerRevision":1,"state":"pending","credentialRevision":-1}""")]
    [InlineData("""{"scope":"project","projectId":"project-1","providerId":"provider-1","providerRevision":9223372036854775808,"state":"enabled","credentialRevision":1}""")]
    [InlineData("""{"scope":"project","projectId":"project-1","providerId":"provider-1","providerRevision":1.5,"state":"enabled","credentialRevision":1}""")]
    [InlineData("""{"scope":"platform","projectId":"project-1","providerId":"provider-1","providerRevision":1,"state":"enabled","credentialRevision":1}""")]
    [InlineData("""{"scope":"project","projectId":null,"providerId":"provider-1","providerRevision":1,"state":"enabled","credentialRevision":1}""")]
    [InlineData("""{"scope":"project","projectId":"other-project","providerId":"provider-1","providerRevision":1,"state":"enabled","credentialRevision":1}""")]
    [InlineData("""{"scope":"project","projectId":"project-1","providerId":"other-provider","providerRevision":1,"state":"enabled","credentialRevision":1}""")]
    [InlineData("""{"scope":"project","projectId":"project-1","providerId":"provider-1","providerRevision":1,"state":"unknown","credentialRevision":1}""")]
    [InlineData("""{"scope":"project","projectId":"project-1","providerId":"provider-1","providerRevision":1,"state":"enabled"}""")]
    [InlineData("""{"scope":"project","projectId":"project-1","providerId":"provider-1","providerRevision":1,"state":"enabled","credentialRevision":1,"secret":"sk-secret-canary"}""")]
    [InlineData("""{"scope":"project","scope":"project","projectId":"project-1","providerId":"provider-1","providerRevision":1,"state":"enabled","credentialRevision":1}""")]
    [InlineData("""not-json-sk-secret-canary""")]
    public async Task ProjectShowCredentialStatusRejectsInvalidResponsesWithoutLeakingBody(string body)
    {
        using var fixture = new Fixture();
        fixture.Respond = _ => JsonResponse(body);

        var exit = await fixture.RunProject("show", ProviderId, "--credential-status");

        Assert.Equal(1, exit);
        Assert.Equal(string.Empty, fixture.Output.ToString());
        Assert.DoesNotContain("sk-secret-canary", fixture.Error.ToString(), StringComparison.Ordinal);
        var call = Assert.Single(fixture.LastCreatedClient!.Calls,
            c => c["operationId"]?.GetValue<string>() == "readProjectByokProviderCredential");
        Assert.Equal("[REDACTED]", call["responseBody"]?.GetValue<string>());
    }

    [Fact]
    public async Task ProjectShowCredentialStatusRejectsWrongMediaTypeAndOversizedResponse()
    {
        using var mediaFixture = new Fixture();
        mediaFixture.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("sk-secret-canary", Encoding.UTF8, "text/plain"),
        };

        var mediaExit = await mediaFixture.RunProject("show", ProviderId, "--credential-status");
        Assert.Equal(1, mediaExit);
        Assert.Equal(string.Empty, mediaFixture.Output.ToString());
        Assert.DoesNotContain("sk-secret-canary", mediaFixture.Error.ToString(), StringComparison.Ordinal);
        var mediaCall = Assert.Single(mediaFixture.LastCreatedClient!.Calls,
            c => c["operationId"]?.GetValue<string>() == "readProjectByokProviderCredential");
        Assert.Equal("[REDACTED]", mediaCall["responseBody"]?.GetValue<string>());

        using var sizeFixture = new Fixture();
        var oversized = new JsonObject
        {
            ["scope"] = "project",
            ["projectId"] = "project-1",
            ["providerId"] = ProviderId,
            ["providerRevision"] = 1,
            ["state"] = "enabled",
            ["credentialRevision"] = 1,
            ["padding"] = new string('x', 17 * 1024),
        };
        sizeFixture.Respond = _ => JsonResponse(oversized);

        var sizeExit = await sizeFixture.RunProject("show", ProviderId, "--credential-status");
        Assert.Equal(1, sizeExit);
        Assert.Equal(string.Empty, sizeFixture.Output.ToString());
    }

    [Theory]
    [InlineData("response-limit")]
    [InlineData("schema-field")]
    public async Task ProjectShowCredentialStatusStopsBeforeRequestOnIncompatibleDiscovery(string mutation)
    {
        using var fixture = new Fixture();
        fixture.TransformOpenApi = doc =>
        {
            if (mutation == "response-limit")
            {
                doc["paths"]!["/api/v1/projects/{projectId}/byok-providers/{providerId}/credential"]!["get"]!
                    ["x-agentweaver-max-response-bytes"] = 32 * 1024;
            }
            else
            {
                var props = doc["components"]!["schemas"]!["ModelProviderCredentialStatusResponse"]!["properties"]!.AsObject();
                props.Remove("credentialRevision");
            }
        };

        var exit = await fixture.RunProject("show", ProviderId, "--credential-status");

        Assert.Equal(1, exit);
        Assert.Equal(string.Empty, fixture.Output.ToString());
        Assert.Empty(fixture.Requests);
    }

    [Fact]
    public async Task ProjectShowCredentialStatusStopsOnTenantMismatchAndSuppressesErrorAndTransportSecrets()
    {
        using var tenantFixture = new Fixture();
        tenantFixture.TransformAuthContext = ctx => { ctx["tenantId"] = "other-tenant"; return ctx; };
        var tenantExit = await tenantFixture.RunProject("show", ProviderId, "--credential-status");
        Assert.Equal(1, tenantExit);
        Assert.Equal(string.Empty, tenantFixture.Output.ToString());
        Assert.Empty(tenantFixture.Requests);

        foreach (var (status, code, expectedError) in new[]
        {
            (HttpStatusCode.Forbidden, "forbidden", "BYOK_FORBIDDEN"),
            (HttpStatusCode.NotFound, "not_found", "BYOK_NOT_FOUND"),
            (HttpStatusCode.Conflict, "model_provider_draft_metadata_requires_update", "BYOK_CONFLICT"),
            (HttpStatusCode.Conflict, "credential_write_quarantined", "BYOK_QUARANTINED"),
            (HttpStatusCode.ServiceUnavailable, "credential_publication_pending", "BYOK_PENDING_PUBLICATION"),
            (HttpStatusCode.ServiceUnavailable, "credential_vault_unavailable", "BYOK_VAULT_UNAVAILABLE"),
        })
        {
            using var errorFixture = new Fixture();
            errorFixture.Respond = _ => JsonResponse(new JsonObject
            {
                ["code"] = code,
                ["detail"] = "sk-secret-canary",
            }, status);

            var errorExit = await errorFixture.RunProject("show", ProviderId, "--credential-status");
            Assert.Equal(1, errorExit);
            Assert.Equal(string.Empty, errorFixture.Output.ToString());
            Assert.Contains(expectedError, errorFixture.Error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("sk-secret-canary", errorFixture.Error.ToString(), StringComparison.Ordinal);
            var call = Assert.Single(errorFixture.LastCreatedClient!.Calls,
                c => c["operationId"]?.GetValue<string>() == "readProjectByokProviderCredential");
            Assert.Equal("[REDACTED]", call["responseBody"]?.GetValue<string>());
        }

        using var transportFixture = new Fixture();
        transportFixture.Respond = _ => throw new HttpRequestException("connection failed: sk-secret-canary");
        var transportExit = await transportFixture.RunProject("show", ProviderId, "--credential-status");
        Assert.Equal(1, transportExit);
        Assert.Equal(string.Empty, transportFixture.Output.ToString());
        Assert.DoesNotContain("sk-secret-canary", transportFixture.Error.ToString(), StringComparison.Ordinal);
        var transportCall = Assert.Single(transportFixture.LastCreatedClient!.Calls,
            c => c["operationId"]?.GetValue<string>() == "readProjectByokProviderCredential");
        Assert.Equal("[REDACTED]", transportCall["responseBody"]?.GetValue<string>());
        Assert.Equal("[REDACTED]", transportCall["error"]?.GetValue<string>());
    }

    [Theory]
    [InlineData("project", "show", ProviderId, "--credential-status=secret-value")]
    [InlineData("project", "show", ProviderId, "--credential-status", "secret-value")]
    [InlineData("project", "show", ProviderId, "--credential-status", "--credential-status")]
    [InlineData("project", "show", ProviderId, "--credential-status", "--run-id", "run-1")]
    [InlineData("project", "list", "--credential-status")]
    [InlineData("platform", "show", ProviderId, "--credential-status=secret-value")]
    [InlineData("platform", "show", ProviderId, "--credential-status", "secret-value")]
    [InlineData("platform", "show", ProviderId, "--credential-status", "--credential-status")]
    [InlineData("platform", "show", ProviderId, "--credential-status", "--project-id", "project-1")]
    [InlineData("platform", "show", ProviderId, "--credential-status", "--run-id", "run-1")]
    [InlineData("platform", "list", "--credential-status")]
    public async Task InvalidCredentialStatusOptionsMakeNoCallsAndDoNotEchoValues(params string[] args)
    {
        using var fixture = new Fixture();
        var scope = args[0];
        var commandArgs = args[1..];
        var exit = scope == "project"
            ? await fixture.RunProject(commandArgs)
            : await fixture.Run(commandArgs);

        Assert.Equal(1, exit);
        Assert.Equal(string.Empty, fixture.Output.ToString());
        Assert.DoesNotContain("secret-value", fixture.Error.ToString(), StringComparison.Ordinal);
        Assert.Empty(fixture.Requests);
        Assert.Null(fixture.LastCreatedClient);
    }

    [Theory]
    [InlineData("not_set", 1L, 0L)]
    [InlineData("enabled", 1L, 1L)]
    [InlineData("disabled", 2L, 3L)]
    [InlineData("pending", 1L, 0L)]
    [InlineData("pending", 2L, 1L)]
    [InlineData("quarantined", 1L, 0L)]
    [InlineData("quarantined", 5_000_000_000L, 6_000_000_000L)]
    public async Task PlatformShowCredentialStatusAcceptsAllValidStateRevisionPairs(
        string state,
        long providerRevision,
        long credentialRevision)
    {
        var previousProjectId = Environment.GetEnvironmentVariable("AGENTWEAVER_PROJECT_ID");
        var previousRunId = Environment.GetEnvironmentVariable("AGENTWEAVER_RUN_ID");
        Environment.SetEnvironmentVariable("AGENTWEAVER_PROJECT_ID", "ambient-project-ignored");
        Environment.SetEnvironmentVariable("AGENTWEAVER_RUN_ID", "ambient-run-ignored");
        try
        {
            using var fixture = new Fixture();
            using var stdin = new ReadTrackingStream("unused-stdin"u8.ToArray());
            fixture.StandardInput = stdin;
            fixture.StandardInputRedirected = true;
            fixture.Respond = _ => JsonResponse(new JsonObject
            {
                ["scope"] = "platform",
                ["projectId"] = null,
                ["providerId"] = ProviderId,
                ["providerRevision"] = providerRevision,
                ["state"] = state,
                ["credentialRevision"] = credentialRevision,
            });

            var exit = await fixture.Run("show", ProviderId, "--credential-status");

            Assert.Equal(0, exit);
            Assert.Equal(string.Empty, fixture.Error.ToString());
            Assert.Equal(0, stdin.ReadCalls);
            Assert.Null(fixture.Authentication.Identity?.ProjectId);
            Assert.Null(fixture.Authentication.Identity?.RunId);
            Assert.Contains("projects.admin", fixture.Authentication.Identity?.Scopes ?? []);

            var request = Assert.Single(fixture.Requests);
            Assert.Equal("GET", request.Method);
            Assert.Equal($"/api/v1/platform/byok-providers/{ProviderId}/credential", request.Path);
            Assert.Equal(string.Empty, request.Query);
            Assert.Empty(request.BodyBytes);
            Assert.False(request.Headers.ContainsKey("X-Agentweaver-Project-Authorization"));

            var parsed = JsonNode.Parse(fixture.Output.ToString())!.AsObject();
            Assert.Equal(6, parsed.Count);
            Assert.Equal("platform", parsed["scope"]?.GetValue<string>());
            Assert.True(parsed.TryGetPropertyValue("projectId", out var projectIdNode));
            Assert.Null(projectIdNode);
            Assert.Equal(ProviderId, parsed["providerId"]?.GetValue<string>());
            Assert.Equal(providerRevision, parsed["providerRevision"]?.GetValue<long>());
            Assert.Equal(state, parsed["state"]?.GetValue<string>());
            Assert.Equal(credentialRevision, parsed["credentialRevision"]?.GetValue<long>());

            var call = Assert.Single(fixture.LastCreatedClient!.Calls,
                c => c["operationId"]?.GetValue<string>() == "readPlatformByokProviderCredential");
            Assert.Equal("[REDACTED]", call["responseBody"]?.GetValue<string>());
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENTWEAVER_PROJECT_ID", previousProjectId);
            Environment.SetEnvironmentVariable("AGENTWEAVER_RUN_ID", previousRunId);
        }
    }

    [Theory]
    [InlineData("""{"scope":"platform","projectId":null,"providerId":"byok-platform-0001","providerRevision":1,"state":"not_set","credentialRevision":1}""")]
    [InlineData("""{"scope":"platform","projectId":null,"providerId":"byok-platform-0001","providerRevision":1,"state":"enabled","credentialRevision":0}""")]
    [InlineData("""{"scope":"platform","projectId":null,"providerId":"byok-platform-0001","providerRevision":1,"state":"disabled","credentialRevision":0}""")]
    [InlineData("""{"scope":"platform","projectId":null,"providerId":"byok-platform-0001","providerRevision":0,"state":"enabled","credentialRevision":1}""")]
    [InlineData("""{"scope":"platform","projectId":null,"providerId":"byok-platform-0001","providerRevision":1,"state":"pending","credentialRevision":-1}""")]
    [InlineData("""{"scope":"platform","projectId":null,"providerId":"byok-platform-0001","providerRevision":9223372036854775808,"state":"enabled","credentialRevision":1}""")]
    [InlineData("""{"scope":"platform","projectId":null,"providerId":"byok-platform-0001","providerRevision":1.5,"state":"enabled","credentialRevision":1}""")]
    [InlineData("""{"scope":"project","projectId":null,"providerId":"byok-platform-0001","providerRevision":1,"state":"enabled","credentialRevision":1}""")]
    [InlineData("""{"scope":"platform","projectId":"project-1","providerId":"byok-platform-0001","providerRevision":1,"state":"enabled","credentialRevision":1}""")]
    [InlineData("""{"scope":"platform","projectId":"","providerId":"byok-platform-0001","providerRevision":1,"state":"enabled","credentialRevision":1}""")]
    [InlineData("""{"scope":"platform","projectId":123,"providerId":"byok-platform-0001","providerRevision":1,"state":"enabled","credentialRevision":1}""")]
    [InlineData("""{"scope":"platform","providerId":"byok-platform-0001","providerRevision":1,"state":"enabled","credentialRevision":1,"extra":"sk-secret-canary"}""")]
    [InlineData("""{"scope":"platform","projectId":null,"providerId":"other-provider","providerRevision":1,"state":"enabled","credentialRevision":1}""")]
    [InlineData("""{"scope":"platform","projectId":null,"providerId":"byok-platform-0001","providerRevision":1,"state":"unknown","credentialRevision":1}""")]
    [InlineData("""{"scope":"platform","projectId":null,"providerId":"byok-platform-0001","providerRevision":1,"state":"enabled"}""")]
    [InlineData("""{"scope":"platform","projectId":null,"providerId":"byok-platform-0001","providerRevision":1,"state":"enabled","credentialRevision":1,"secret":"sk-secret-canary"}""")]
    [InlineData("""{"scope":"platform","scope":"platform","projectId":null,"providerId":"byok-platform-0001","providerRevision":1,"state":"enabled","credentialRevision":1}""")]
    [InlineData("""not-json-sk-secret-canary""")]
    public async Task PlatformShowCredentialStatusRejectsInvalidResponsesWithoutLeakingBody(string body)
    {
        using var fixture = new Fixture();
        fixture.Respond = _ => JsonResponse(body);

        var exit = await fixture.Run("show", ProviderId, "--credential-status");

        Assert.Equal(1, exit);
        Assert.Equal(string.Empty, fixture.Output.ToString());
        Assert.DoesNotContain("sk-secret-canary", fixture.Error.ToString(), StringComparison.Ordinal);
        var call = Assert.Single(fixture.LastCreatedClient!.Calls,
            c => c["operationId"]?.GetValue<string>() == "readPlatformByokProviderCredential");
        Assert.Equal("[REDACTED]", call["responseBody"]?.GetValue<string>());
    }

    [Fact]
    public async Task PlatformShowCredentialStatusRejectsWrongMediaTypeAndOversizedResponse()
    {
        using var mediaFixture = new Fixture();
        mediaFixture.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("sk-secret-canary", Encoding.UTF8, "text/plain"),
        };

        var mediaExit = await mediaFixture.Run("show", ProviderId, "--credential-status");
        Assert.Equal(1, mediaExit);
        Assert.Equal(string.Empty, mediaFixture.Output.ToString());
        Assert.DoesNotContain("sk-secret-canary", mediaFixture.Error.ToString(), StringComparison.Ordinal);
        var mediaCall = Assert.Single(mediaFixture.LastCreatedClient!.Calls,
            c => c["operationId"]?.GetValue<string>() == "readPlatformByokProviderCredential");
        Assert.Equal("[REDACTED]", mediaCall["responseBody"]?.GetValue<string>());

        using var sizeFixture = new Fixture();
        var oversized =
            """{"scope":"platform","projectId":null,"providerId":"byok-platform-0001","providerRevision":1,"state":"enabled","credentialRevision":1}""" +
            new string(' ', 17 * 1024);
        sizeFixture.Respond = _ => JsonResponse(oversized);

        var sizeExit = await sizeFixture.Run("show", ProviderId, "--credential-status");
        Assert.Equal(1, sizeExit);
        Assert.Equal(string.Empty, sizeFixture.Output.ToString());
    }

    [Theory]
    [InlineData("response-limit")]
    [InlineData("schema-field")]
    [InlineData("extra-tenant-query")]
    public async Task PlatformShowCredentialStatusStopsBeforeRequestOnIncompatibleDiscovery(string mutation)
    {
        using var fixture = new Fixture();
        fixture.TransformOpenApi = doc =>
        {
            if (mutation == "response-limit")
            {
                doc["paths"]!["/api/v1/platform/byok-providers/{providerId}/credential"]!["get"]!
                    ["x-agentweaver-max-response-bytes"] = 32 * 1024;
            }
            else if (mutation == "extra-tenant-query")
            {
                var parameters = doc["paths"]!["/api/v1/platform/byok-providers/{providerId}/credential"]!["get"]!
                    ["parameters"]!.AsArray();
                parameters.Add(new JsonObject
                {
                    ["name"] = "X-Agentweaver-Tenant",
                    ["in"] = "query",
                    ["required"] = false,
                    ["schema"] = new JsonObject { ["type"] = "string" },
                });
            }
            else
            {
                var props = doc["components"]!["schemas"]!["ModelProviderCredentialStatusResponse"]!["properties"]!.AsObject();
                props.Remove("credentialRevision");
            }
        };

        var exit = await fixture.Run("show", ProviderId, "--credential-status");

        Assert.Equal(1, exit);
        Assert.Equal(string.Empty, fixture.Output.ToString());
        Assert.Empty(fixture.Requests);
    }

    [Fact]
    public async Task PlatformShowCredentialStatusStopsOnTenantMismatchAndSuppressesErrorAndTransportSecrets()
    {
        using var tenantFixture = new Fixture();
        tenantFixture.TransformAuthContext = ctx => { ctx["tenantId"] = "other-tenant"; return ctx; };
        var tenantExit = await tenantFixture.Run("show", ProviderId, "--credential-status");
        Assert.Equal(1, tenantExit);
        Assert.Equal(string.Empty, tenantFixture.Output.ToString());
        Assert.Empty(tenantFixture.Requests);

        using var boundFixture = new Fixture();
        boundFixture.TransformAuthContext = ctx => { ctx["boundProjectId"] = "project-1"; return ctx; };
        var boundExit = await boundFixture.Run("show", ProviderId, "--credential-status");
        Assert.Equal(1, boundExit);
        Assert.Equal(string.Empty, boundFixture.Output.ToString());
        Assert.Empty(boundFixture.Requests);

        foreach (var (status, code, expectedError) in new[]
        {
            (HttpStatusCode.Forbidden, "forbidden", "BYOK_FORBIDDEN"),
            (HttpStatusCode.NotFound, "not_found", "BYOK_NOT_FOUND"),
            (HttpStatusCode.Conflict, "model_provider_draft_metadata_requires_update", "BYOK_CONFLICT"),
            (HttpStatusCode.Conflict, "credential_write_quarantined", "BYOK_QUARANTINED"),
            (HttpStatusCode.ServiceUnavailable, "credential_publication_pending", "BYOK_PENDING_PUBLICATION"),
            (HttpStatusCode.ServiceUnavailable, "credential_vault_unavailable", "BYOK_VAULT_UNAVAILABLE"),
        })
        {
            using var errorFixture = new Fixture();
            errorFixture.Respond = _ => JsonResponse(new JsonObject
            {
                ["code"] = code,
                ["detail"] = "sk-secret-canary",
            }, status);

            var errorExit = await errorFixture.Run("show", ProviderId, "--credential-status");
            Assert.Equal(1, errorExit);
            Assert.Equal(string.Empty, errorFixture.Output.ToString());
            Assert.Contains(expectedError, errorFixture.Error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("sk-secret-canary", errorFixture.Error.ToString(), StringComparison.Ordinal);
            var call = Assert.Single(errorFixture.LastCreatedClient!.Calls,
                c => c["operationId"]?.GetValue<string>() == "readPlatformByokProviderCredential");
            Assert.Equal("[REDACTED]", call["responseBody"]?.GetValue<string>());
        }

        using var transportFixture = new Fixture();
        transportFixture.Respond = _ => throw new HttpRequestException("connection failed: sk-secret-canary");
        var transportExit = await transportFixture.Run("show", ProviderId, "--credential-status");
        Assert.Equal(1, transportExit);
        Assert.Equal(string.Empty, transportFixture.Output.ToString());
        Assert.DoesNotContain("sk-secret-canary", transportFixture.Error.ToString(), StringComparison.Ordinal);
        var transportCall = Assert.Single(transportFixture.LastCreatedClient!.Calls,
            c => c["operationId"]?.GetValue<string>() == "readPlatformByokProviderCredential");
        Assert.Equal("[REDACTED]", transportCall["responseBody"]?.GetValue<string>());
        Assert.Equal("[REDACTED]", transportCall["error"]?.GetValue<string>());
    }

    [Fact]
    public async Task PlainProjectShowStillReturnsMetadataWithoutStatusRequest()
    {
        using var fixture = new Fixture();
        fixture.Respond = _ => JsonResponse(Provider(ProviderId, "Project OpenAI", revision: 3, scope: "project", projectId: "project-1"));

        var exit = await fixture.RunProject("show", ProviderId);

        Assert.Equal(0, exit);
        var request = Assert.Single(fixture.Requests);
        Assert.Equal($"/api/v1/projects/project-1/byok-providers/{ProviderId}", request.Path);
        var parsed = JsonNode.Parse(fixture.Output.ToString())!.AsObject();
        Assert.Equal(ProviderId, parsed["providerId"]?.GetValue<string>());
        Assert.Equal(3L, parsed["revision"]?.GetValue<long>());
        Assert.False(parsed.ContainsKey("credentialRevision"));
    }

    [Theory]
    [InlineData("pending", null, null)]
    [InlineData("publication_pending", null, null)]
    [InlineData("published", 5_000_000_000L, true)]
    [InlineData("quarantined", null, null)]
    [InlineData("cancelled", null, null)]
    [InlineData("disable_pending", null, null)]
    [InlineData("disabled", 4L, null)]
    public async Task ShowOperationStatusAcceptsAllValidStatesInBothScopes(
        string status,
        long? providerRevision,
        bool? hasCredential)
    {
        var previousProject = Environment.GetEnvironmentVariable("AGENTWEAVER_PROJECT_ID");
        var previousRun = Environment.GetEnvironmentVariable("AGENTWEAVER_RUN_ID");
        Environment.SetEnvironmentVariable("AGENTWEAVER_PROJECT_ID", "ambient-project-ignored");
        Environment.SetEnvironmentVariable("AGENTWEAVER_RUN_ID", "ambient-run-ignored");
        try
        {
            foreach (var projectScope in new[] { false, true })
            {
                using var fixture = new Fixture();
                using var stdin = new ReadTrackingStream("unused-stdin"u8.ToArray());
                fixture.StandardInput = stdin;
                fixture.StandardInputRedirected = true;
                var expectedScope = projectScope ? "project" : "platform";
                var expectedProjectId = projectScope ? "project-1" : null;
                fixture.Respond = _ =>
                {
                    var body = new JsonObject
                    {
                        ["operationId"] = OperationId,
                        ["scope"] = expectedScope,
                        ["projectId"] = expectedProjectId,
                        ["providerId"] = ProviderId,
                        ["status"] = status,
                    };
                    if (providerRevision is not null)
                        body["providerRevision"] = providerRevision;
                    if (hasCredential is not null)
                        body["hasCredential"] = hasCredential;
                    return JsonResponse(body);
                };

                var exit = projectScope
                    ? await fixture.RunProject("show", ProviderId, "--operation-id", OperationId)
                    : await fixture.Run("show", ProviderId, "--operation-id", OperationId);

                Assert.Equal(0, exit);
                Assert.Equal(string.Empty, fixture.Error.ToString());
                Assert.Equal(0, stdin.ReadCalls);
                Assert.Equal(expectedProjectId, fixture.Authentication.Identity?.ProjectId);
                Assert.Null(fixture.Authentication.Identity?.RunId);

                var request = Assert.Single(fixture.Requests);
                Assert.Equal("GET", request.Method);
                var expectedPath = projectScope
                    ? $"/api/v1/projects/project-1/byok-providers/{ProviderId}/credential/operations/{OperationId}"
                    : $"/api/v1/platform/byok-providers/{ProviderId}/credential/operations/{OperationId}";
                Assert.Equal(expectedPath, request.Path);
                Assert.Equal(string.Empty, request.Query);

                var parsed = JsonNode.Parse(fixture.Output.ToString())!.AsObject();
                Assert.Equal(OperationId, parsed["operationId"]?.GetValue<string>());
                Assert.Equal(expectedScope, parsed["scope"]?.GetValue<string>());
                Assert.Equal(expectedProjectId, parsed["projectId"]?.GetValue<string>());
                Assert.Equal(ProviderId, parsed["providerId"]?.GetValue<string>());
                Assert.Equal(status, parsed["status"]?.GetValue<string>());
                Assert.Equal(providerRevision, parsed["providerRevision"]?.GetValue<long?>());
                Assert.Equal(hasCredential, parsed["hasCredential"]?.GetValue<bool?>());

                var call = Assert.Single(fixture.LastCreatedClient!.Calls,
                    c => c["operationId"]?.GetValue<string>() ==
                        (projectScope ? "readProjectByokProviderCredentialOperation" : "readPlatformByokProviderCredentialOperation"));
                Assert.Equal("[REDACTED]", call["responseBody"]?.GetValue<string>());
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENTWEAVER_PROJECT_ID", previousProject);
            Environment.SetEnvironmentVariable("AGENTWEAVER_RUN_ID", previousRun);
        }
    }

    [Theory]
    [InlineData("""{"operationId":"f1b62270-3320-414c-9c2a-803493e50131","scope":"platform","projectId":null,"providerId":"byok-platform-0001","status":"published","hasCredential":true}""")]
    [InlineData("""{"operationId":"f1b62270-3320-414c-9c2a-803493e50131","scope":"platform","projectId":null,"providerId":"byok-platform-0001","status":"published","providerRevision":0,"hasCredential":true}""")]
    [InlineData("""{"operationId":"f1b62270-3320-414c-9c2a-803493e50131","scope":"platform","projectId":null,"providerId":"byok-platform-0001","status":"published","providerRevision":1}""")]
    [InlineData("""{"operationId":"f1b62270-3320-414c-9c2a-803493e50131","scope":"platform","projectId":null,"providerId":"byok-platform-0001","status":"published","providerRevision":1,"hasCredential":false}""")]
    [InlineData("""{"operationId":"f1b62270-3320-414c-9c2a-803493e50131","scope":"platform","projectId":null,"providerId":"byok-platform-0001","status":"disabled","providerRevision":null,"hasCredential":null}""")]
    [InlineData("""{"operationId":"f1b62270-3320-414c-9c2a-803493e50131","scope":"platform","projectId":null,"providerId":"byok-platform-0001","status":"disabled","providerRevision":1,"hasCredential":true}""")]
    [InlineData("""{"operationId":"f1b62270-3320-414c-9c2a-803493e50131","scope":"platform","projectId":null,"providerId":"byok-platform-0001","status":"disabled","providerRevision":1,"hasCredential":false}""")]
    [InlineData("""{"operationId":"f1b62270-3320-414c-9c2a-803493e50131","scope":"platform","projectId":null,"providerId":"byok-platform-0001","status":"pending","providerRevision":1,"hasCredential":null}""")]
    [InlineData("""{"operationId":"f1b62270-3320-414c-9c2a-803493e50131","scope":"platform","projectId":null,"providerId":"byok-platform-0001","status":"publication_pending","providerRevision":null,"hasCredential":true}""")]
    [InlineData("""{"operationId":"f1b62270-3320-414c-9c2a-803493e50131","scope":"platform","projectId":null,"providerId":"byok-platform-0001","status":"quarantined","providerRevision":null,"hasCredential":false}""")]
    [InlineData("""{"operationId":"f1b62270-3320-414c-9c2a-803493e50131","scope":"platform","projectId":null,"providerId":"byok-platform-0001","status":"cancelled","providerRevision":2}""")]
    [InlineData("""{"operationId":"f1b62270-3320-414c-9c2a-803493e50131","scope":"platform","projectId":null,"providerId":"byok-platform-0001","status":"disable_pending","hasCredential":true}""")]
    [InlineData("""{"operationId":"f1b62270-3320-414c-9c2a-803493e50131","scope":"platform","projectId":null,"providerId":"byok-platform-0001","status":"unknown_status"}""")]
    [InlineData("""{"operationId":"00000000-0000-0000-0000-000000000001","scope":"platform","projectId":null,"providerId":"byok-platform-0001","status":"pending"}""")]
    [InlineData("""{"operationId":"f1b62270-3320-414c-9c2a-803493e50131","scope":"project","projectId":null,"providerId":"byok-platform-0001","status":"pending"}""")]
    [InlineData("""{"operationId":"f1b62270-3320-414c-9c2a-803493e50131","scope":"platform","projectId":"project-1","providerId":"byok-platform-0001","status":"pending"}""")]
    [InlineData("""{"operationId":"f1b62270-3320-414c-9c2a-803493e50131","scope":"platform","providerId":"byok-platform-0001","status":"pending"}""")]
    [InlineData("""{"operationId":"f1b62270-3320-414c-9c2a-803493e50131","scope":"platform","projectId":null,"providerId":"other-provider","status":"pending"}""")]
    [InlineData("""{"operationId":"f1b62270-3320-414c-9c2a-803493e50131","scope":"platform","projectId":null,"providerId":"byok-platform-0001","status":"pending","secret":"sk-secret-canary"}""")]
    [InlineData("""{"operationId":"f1b62270-3320-414c-9c2a-803493e50131","operationId":"f1b62270-3320-414c-9c2a-803493e50131","scope":"platform","projectId":null,"providerId":"byok-platform-0001","status":"pending"}""")]
    [InlineData("""not-json-sk-secret-canary""")]
    public async Task ShowOperationStatusRejectsInvalidMismatchedExtraAndDuplicateResponsesWithoutLeak(string body)
    {
        using var fixture = new Fixture();
        fixture.Respond = _ => JsonResponse(body);

        var exit = await fixture.Run("show", ProviderId, "--operation-id", OperationId);

        Assert.Equal(1, exit);
        Assert.Equal(string.Empty, fixture.Output.ToString());
        Assert.DoesNotContain("sk-secret-canary", fixture.Error.ToString(), StringComparison.Ordinal);
        var call = Assert.Single(fixture.LastCreatedClient!.Calls,
            c => c["operationId"]?.GetValue<string>() == "readPlatformByokProviderCredentialOperation");
        Assert.Equal("[REDACTED]", call["responseBody"]?.GetValue<string>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("other-project")]
    public async Task ProjectShowOperationStatusRejectsMismatchedProjectId(string? responseProjectId)
    {
        using var fixture = new Fixture();
        fixture.Respond = _ => JsonResponse(new JsonObject
        {
            ["operationId"] = OperationId,
            ["scope"] = "project",
            ["projectId"] = responseProjectId,
            ["providerId"] = ProviderId,
            ["status"] = "pending",
        });

        var exit = await fixture.RunProject("show", ProviderId, "--operation-id", OperationId);

        Assert.Equal(1, exit);
        Assert.Equal(string.Empty, fixture.Output.ToString());
    }

    [Theory]
    [InlineData("show|byok-platform-0001|--credential-status|--operation-id|f1b62270-3320-414c-9c2a-803493e50131")]
    [InlineData("show|byok-platform-0001|--operation-id|")]
    [InlineData("show|byok-platform-0001|--operation-id|not-a-uuid")]
    [InlineData("show|byok-platform-0001|--operation-id|00000000-0000-0000-0000-000000000000")]
    [InlineData("show|byok-platform-0001|--operation-id")]
    [InlineData("show|byok-platform-0001|--operation-id|f1b62270-3320-414c-9c2a-803493e50131|--project-id|project-1")]
    public async Task PlatformShowOperationStatusRejectsInvalidOptionsBeforeDiscovery(string joinedArgs)
    {
        using var fixture = new Fixture();
        var discoveryCalled = false;
        fixture.TransformOpenApi = _ => discoveryCalled = true;

        var exit = await fixture.Run(joinedArgs.Split('|'));

        Assert.Equal(1, exit);
        Assert.False(discoveryCalled);
        Assert.Empty(fixture.Requests);
    }

    [Fact]
    public async Task ProjectShowOperationStatusRejectsRunIdAndMutuallyExclusiveFlagsBeforeDiscovery()
    {
        using var fixture = new Fixture();
        var discoveryCalled = false;
        fixture.TransformOpenApi = _ => discoveryCalled = true;

        Assert.Equal(1, await fixture.RunProject("show", ProviderId, "--operation-id", OperationId, "--run-id", "run-1"));
        Assert.Equal(1, await fixture.RunProject("show", ProviderId, "--credential-status", "--operation-id", OperationId));
        Assert.False(discoveryCalled);
        Assert.Empty(fixture.Requests);
    }

    [Fact]
    public async Task ShowOperationStatusRejectsWrongMediaTypeAndOversizedValidPaddedJson()
    {
        using var mediaFixture = new Fixture();
        mediaFixture.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("sk-secret-canary", Encoding.UTF8, "text/plain"),
        };

        var mediaExit = await mediaFixture.Run("show", ProviderId, "--operation-id", OperationId);
        Assert.Equal(1, mediaExit);
        Assert.Equal(string.Empty, mediaFixture.Output.ToString());
        Assert.DoesNotContain("sk-secret-canary", mediaFixture.Error.ToString(), StringComparison.Ordinal);
        var mediaCall = Assert.Single(mediaFixture.LastCreatedClient!.Calls,
            c => c["operationId"]?.GetValue<string>() == "readPlatformByokProviderCredentialOperation");
        Assert.Equal("[REDACTED]", mediaCall["responseBody"]?.GetValue<string>());

        using var sizeFixture = new Fixture();
        var oversized =
            $$"""{"operationId":"{{OperationId}}","scope":"platform","projectId":null,"providerId":"{{ProviderId}}","status":"pending"}""" +
            new string(' ', 17 * 1024);
        sizeFixture.Respond = _ => JsonResponse(oversized);

        var sizeExit = await sizeFixture.Run("show", ProviderId, "--operation-id", OperationId);
        Assert.Equal(1, sizeExit);
        Assert.Equal(string.Empty, sizeFixture.Output.ToString());
    }

    [Theory]
    [InlineData("response-limit")]
    [InlineData("schema-field")]
    [InlineData("extra-tenant-query")]
    [InlineData("extra-response-media-type")]
    public async Task ShowOperationStatusStopsBeforeRequestOnIncompatibleDiscovery(string mutation)
    {
        foreach (var projectScope in new[] { false, true })
        {
            using var fixture = new Fixture();
            fixture.Respond = _ => JsonResponse(new JsonObject
            {
                ["operationId"] = OperationId,
                ["scope"] = projectScope ? "project" : "platform",
                ["projectId"] = projectScope ? "project-1" : null,
                ["providerId"] = ProviderId,
                ["status"] = "pending",
            });
            var openApiPath = projectScope
                ? "/api/v1/projects/{projectId}/byok-providers/{providerId}/credential/operations/{operationId}"
                : "/api/v1/platform/byok-providers/{providerId}/credential/operations/{operationId}";
            fixture.TransformOpenApi = doc =>
            {
                if (mutation == "response-limit")
                {
                    doc["paths"]![openApiPath]!["get"]!["x-agentweaver-max-response-bytes"] = 32 * 1024;
                }
                else if (mutation == "extra-tenant-query")
                {
                    var parameters = doc["paths"]![openApiPath]!["get"]!["parameters"]!.AsArray();
                    parameters.Add(new JsonObject
                    {
                        ["name"] = "X-Agentweaver-Tenant",
                        ["in"] = "query",
                        ["required"] = false,
                        ["schema"] = new JsonObject { ["type"] = "string" },
                    });
                }
                else if (mutation == "extra-response-media-type")
                {
                    var content = doc["paths"]![openApiPath]!["get"]!["responses"]!["200"]!["content"]!.AsObject();
                    content["application/problem+json"] = new JsonObject
                    {
                        ["schema"] = new JsonObject { ["$ref"] = "#/components/schemas/ModelProviderCredentialOperationStatusResponse" },
                    };
                }
                else
                {
                    var props = doc["components"]!["schemas"]!["ModelProviderCredentialOperationStatusResponse"]!["properties"]!.AsObject();
                    props.Remove("hasCredential");
                }
            };

            var exit = projectScope
                ? await fixture.RunProject("show", ProviderId, "--operation-id", OperationId)
                : await fixture.Run("show", ProviderId, "--operation-id", OperationId);

            Assert.Equal(1, exit);
            Assert.Equal(string.Empty, fixture.Output.ToString());
            Assert.Empty(fixture.Requests);
        }
    }

    [Fact]
    public async Task ShowOperationStatusEnforcesAuthAndHandlesErrorsAndCancellationWithoutWriteGuidanceOrSecrets()
    {
        using var tenantFixture = new Fixture();
        tenantFixture.TransformAuthContext = ctx => { ctx["tenantId"] = "other-tenant"; return ctx; };
        Assert.Equal(1, await tenantFixture.Run("show", ProviderId, "--operation-id", OperationId));
        Assert.Empty(tenantFixture.Requests);

        using var boundProjectFixture = new Fixture();
        boundProjectFixture.TransformAuthContext = ctx => { ctx["boundProjectId"] = "project-1"; return ctx; };
        Assert.Equal(1, await boundProjectFixture.Run("show", ProviderId, "--operation-id", OperationId));
        Assert.Empty(boundProjectFixture.Requests);

        using var boundRunFixture = new Fixture();
        boundRunFixture.TransformAuthContext = ctx => { ctx["boundRunId"] = "run-1"; return ctx; };
        Assert.Equal(1, await boundRunFixture.RunProject("show", ProviderId, "--operation-id", OperationId));
        Assert.Empty(boundRunFixture.Requests);

        foreach (var (status, code, expectedError) in new[]
        {
            (HttpStatusCode.NotFound, "credential_operation_not_found", "BYOK_NOT_FOUND"),
            (HttpStatusCode.Forbidden, "forbidden", "BYOK_FORBIDDEN"),
            (HttpStatusCode.Conflict, "model_provider_draft_metadata_requires_update", "BYOK_CONFLICT"),
            (HttpStatusCode.Conflict, "credential_write_quarantined", "BYOK_QUARANTINED"),
            (HttpStatusCode.ServiceUnavailable, "credential_publication_pending", "BYOK_PENDING_PUBLICATION"),
            (HttpStatusCode.InternalServerError, "internal_error", "BYOK_REQUEST_FAILED_500"),
        })
        {
            using var errorFixture = new Fixture();
            errorFixture.Respond = _ => JsonResponse(new JsonObject
            {
                ["code"] = code,
                ["detail"] = "sk-secret-canary",
            }, status);

            var errorExit = await errorFixture.Run("show", ProviderId, "--operation-id", OperationId);
            Assert.Equal(1, errorExit);
            Assert.Equal(string.Empty, errorFixture.Output.ToString());
            Assert.Single(errorFixture.Requests);
            Assert.Contains(expectedError, errorFixture.Error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("Repeat the same command", errorFixture.Error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("sk-secret-canary", errorFixture.Error.ToString(), StringComparison.Ordinal);
            var call = Assert.Single(errorFixture.LastCreatedClient!.Calls,
                c => c["operationId"]?.GetValue<string>() == "readPlatformByokProviderCredentialOperation");
            Assert.Equal("[REDACTED]", call["responseBody"]?.GetValue<string>());
        }

        using var transportFixture = new Fixture();
        transportFixture.Respond = _ => throw new HttpRequestException("connection failed: sk-secret-canary");
        var transportExit = await transportFixture.Run("show", ProviderId, "--operation-id", OperationId);
        Assert.Equal(1, transportExit);
        Assert.DoesNotContain("sk-secret-canary", transportFixture.Error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Repeat the same command", transportFixture.Error.ToString(), StringComparison.Ordinal);
        var transportCall = Assert.Single(transportFixture.LastCreatedClient!.Calls,
            c => c["operationId"]?.GetValue<string>() == "readPlatformByokProviderCredentialOperation");
        Assert.Equal("[REDACTED]", transportCall["responseBody"]?.GetValue<string>());
        Assert.Equal("[REDACTED]", transportCall["error"]?.GetValue<string>());

        using var cancelFixture = new Fixture();
        using var cts = new CancellationTokenSource();
        cancelFixture.Respond = _ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        };
        var cancelExit = await cancelFixture.Run(cts.Token, "show", ProviderId, "--operation-id", OperationId);
        Assert.Equal(130, cancelExit);
        Assert.DoesNotContain("Repeat the same command", cancelFixture.Error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"operationId":"f1b62270-3320-414c-9c2a-803493e50131","scope":"platform","projectId":null,"providerId":"other-provider","status":"published","providerRevision":2,"hasCredential":true,"secret":"sk-secret-canary"}""")]
    [InlineData("""{"operationId":"f1b62270-3320-414c-9c2a-803493e50131","scope":"platform","projectId":null,"providerId":"byok-platform-0001","status":"published","providerRevision":2,"hasCredential":false,"secret":"sk-secret-canary"}""")]
    [InlineData("""{"operationId":"f1b62270-3320-414c-9c2a-803493e50131","operationId":"f1b62270-3320-414c-9c2a-803493e50131","scope":"platform","projectId":null,"providerId":"byok-platform-0001","status":"published","providerRevision":2,"hasCredential":true}""")]
    public async Task SetKeyRecoveryReportsFixedUnknownMessageOnInvalidOrMismatchedOperationStatusWithoutLeak(string recoveryBody)
    {
        const string secret = "sk-recover-invalid-123";
        using var fixture = new Fixture();
        using var stdin = new MemoryStream(Encoding.UTF8.GetBytes(secret + "\n"));
        fixture.StandardInput = stdin;
        fixture.StandardInputRedirected = true;
        var getStatusCalls = 0;
        fixture.Respond = request =>
        {
            if (request.Method == "GET" && request.Path == $"/api/v1/platform/byok-providers/{ProviderId}")
                return JsonResponse(Provider(ProviderId, "Primary", revision: 1));
            if (request.Method == "GET" && request.Path.EndsWith($"/credential/operations/{OperationId}", StringComparison.Ordinal))
            {
                getStatusCalls++;
                return getStatusCalls == 1
                    ? JsonResponse(new JsonObject { ["code"] = "credential_operation_not_found" }, HttpStatusCode.NotFound)
                    : JsonResponse(recoveryBody);
            }
            if (request.Method == "PUT")
                return JsonResponse("{\"code\":\"unavailable\"}", HttpStatusCode.ServiceUnavailable);
            return JsonResponse("{}", HttpStatusCode.NotFound);
        };

        var exit = await fixture.Run(
            "set-key", ProviderId, "--from-stdin",
            "--expected-revision", "1", "--operation-id", OperationId);

        Assert.Equal(1, exit);
        Assert.Equal(string.Empty, fixture.Output.ToString());
        Assert.Contains($"Operation {OperationId} result is unknown. Check operation status before retrying; do not resend the key.",
            fixture.Error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("sk-secret-canary", fixture.Error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, fixture.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetKeyRecoveryPreservesNonPublishedValidStatusMessage()
    {
        const string secret = "sk-recover-pending-123";
        using var fixture = new Fixture();
        using var stdin = new MemoryStream(Encoding.UTF8.GetBytes(secret + "\n"));
        fixture.StandardInput = stdin;
        fixture.StandardInputRedirected = true;
        var getStatusCalls = 0;
        fixture.Respond = request =>
        {
            if (request.Method == "GET" && request.Path == $"/api/v1/platform/byok-providers/{ProviderId}")
                return JsonResponse(Provider(ProviderId, "Primary", revision: 1));
            if (request.Method == "GET" && request.Path.EndsWith($"/credential/operations/{OperationId}", StringComparison.Ordinal))
            {
                getStatusCalls++;
                return getStatusCalls == 1
                    ? JsonResponse(new JsonObject { ["code"] = "credential_operation_not_found" }, HttpStatusCode.NotFound)
                    : JsonResponse(new JsonObject
                    {
                        ["operationId"] = OperationId,
                        ["scope"] = "platform",
                        ["projectId"] = null,
                        ["providerId"] = ProviderId,
                        ["status"] = "quarantined",
                    });
            }
            if (request.Method == "PUT")
                return JsonResponse("{\"code\":\"unavailable\"}", HttpStatusCode.ServiceUnavailable);
            return JsonResponse("{}", HttpStatusCode.NotFound);
        };

        var exit = await fixture.Run(
            "set-key", ProviderId, "--from-stdin",
            "--expected-revision", "1", "--operation-id", OperationId);

        Assert.Equal(1, exit);
        Assert.Equal(string.Empty, fixture.Output.ToString());
        Assert.Contains($"Operation {OperationId} status is 'quarantined'. Do not resend the key.",
            fixture.Error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, fixture.Error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"operationId":"f1b62270-3320-414c-9c2a-803493e50131","scope":"platform","projectId":null,"providerId":"byok-platform-0001","status":"bogus-sk-secret-canary"}""")]
    [InlineData("""{"operationId":"f1b62270-3320-414c-9c2a-803493e50131","scope":"platform","projectId":null,"providerId":"other-provider","status":"pending","secret":"sk-secret-canary"}""")]
    [InlineData("""{"operationId":"f1b62270-3320-414c-9c2a-803493e50131","scope":"platform","projectId":null,"providerId":"byok-platform-0001","status":"published","providerRevision":1,"hasCredential":false}""")]
    public async Task SetKeyPreflightRejectsInvalidOperationStatusWithoutReadingStdinOrSendingPut(string preflightBody)
    {
        using var fixture = new Fixture();
        using var trackingStdin = new ReadTrackingStream("sk-stdin-canary\n"u8.ToArray());
        fixture.StandardInput = trackingStdin;
        fixture.StandardInputRedirected = true;
        fixture.Respond = request =>
        {
            if (request.Method == "GET" && request.Path == $"/api/v1/platform/byok-providers/{ProviderId}")
                return JsonResponse(Provider(ProviderId, "Primary", revision: 1));
            if (request.Method == "GET" && request.Path.EndsWith($"/credential/operations/{OperationId}", StringComparison.Ordinal))
                return JsonResponse(preflightBody);
            return JsonResponse("{}", HttpStatusCode.NotFound);
        };

        var exit = await fixture.Run(
            "set-key", ProviderId, "--from-stdin",
            "--expected-revision", "1", "--operation-id", OperationId);

        Assert.Equal(1, exit);
        Assert.Equal(string.Empty, fixture.Output.ToString());
        Assert.Contains("The Gateway returned an invalid BYOK response.", fixture.Error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("sk-secret-canary", fixture.Error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("bogus-sk-secret-canary", fixture.Error.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, trackingStdin.ReadCalls);
        Assert.DoesNotContain(fixture.Requests, r => r.Method == "PUT");
    }

    private static string Page(IReadOnlyList<JsonObject> providers, string? nextAfterProviderId) =>
        new JsonObject
        {
            ["providers"] = new JsonArray(providers.Select(provider => (JsonNode?)provider.DeepClone()).ToArray()),
            ["nextAfterProviderId"] = nextAfterProviderId,
        }.ToJsonString();

    private static JsonObject Provider(
        string id,
        string name,
        long revision = 1,
        string? secret = null,
        string scope = "platform",
        string? projectId = null) =>
        new()
        {
            ["id"] = id,
            ["name"] = name,
            ["type"] = "openai",
            ["base_url"] = "https://models.example/",
            ["model"] = "deployment-a",
            ["wire_api"] = "responses",
            ["azure_api_version"] = null,
            ["reasoning_efforts"] = new JsonArray("low"),
            ["prompt_capacity_tokens"] = null,
            ["scope"] = scope,
            ["projectId"] = projectId,
            ["state"] = "Draft",
            ["revision"] = revision,
            ["configurationHash"] = "abc123",
            ["has_api_key"] = false,
            ["is_active"] = false,
            ["hasCredential"] = false,
            ["api_key"] = secret,
            ["credentialReference"] = secret,
        };

    private static JsonObject PriceLookup(string? secret = null) =>
        new()
        {
            ["region"] = "eastus",
            ["sourceUrl"] = "https://prices.azure.com/api/retail/prices",
            ["retrievedOn"] = "2026-11-23T00:00:00Z",
            ["items"] = new JsonArray(new JsonObject
            {
                ["meterId"] = "meter-1",
                ["meterName"] = "GPT",
                ["productName"] = "Azure OpenAI",
                ["skuName"] = "Global",
                ["unitOfMeasure"] = "1K Tokens",
                ["currencyCode"] = "USD",
                ["retailPrice"] = 1,
                ["tierMinimumUnits"] = 0,
                ["usdPer1M"] = 1000,
                ["secret"] = secret,
            }),
            ["secret"] = secret,
        };

    private static JsonObject RateCard(string source, string id = "card-1") =>
        new()
        {
            ["runtimeRateCardSchemaVersion"] = 1,
            ["rateCard"] = new JsonObject
            {
                ["id"] = id,
                ["version"] = "v1",
                ["meterSource"] = "byok.tokens",
                ["unit"] = "USD",
                ["nanoUnitsPerUnit"] = 1000000,
                ["modelMultipliers"] = new JsonObject { ["gpt-4.1"] = 1 },
            },
            ["provenance"] = source == "manual"
                ? new JsonObject
                {
                    ["source"] = "manual",
                    ["sourceUrl"] = "https://example.test/prices",
                    ["retrievedOn"] = "2026-11-23T00:00:00Z",
                }
                : new JsonObject
                {
                    ["source"] = "azure-retail-prices",
                    ["region"] = "eastus",
                    ["meterIds"] = new JsonArray("input", "output"),
                },
            ["secret-canary"] = "secret-canary",
        };

    private static JsonObject ManualRateCard(string id) =>
        new()
        {
            ["runtimeRateCardSchemaVersion"] = 1,
            ["rateCard"] = new JsonObject
            {
                ["id"] = id,
                ["version"] = "v1",
                ["meterSource"] = "byok.tokens",
                ["unit"] = "USD",
                ["nanoUnitsPerUnit"] = 1_000_000,
                ["modelMultipliers"] = new JsonObject { ["gpt-4.1"] = 1 },
            },
            ["provenance"] = new JsonObject
            {
                ["source"] = "manual",
                ["sourceUrl"] = "https://example.test/prices",
                ["retrievedOn"] = "2026-11-23T00:00:00Z",
            },
        };

    private static JsonObject CredentialReceipt(string status, string? secret = null) =>
        new()
        {
            ["operationId"] = OperationId,
            ["status"] = status,
            ["scope"] = "platform",
            ["projectId"] = null,
            ["providerId"] = ProviderId,
            ["providerRevision"] = 4,
            ["credentialRevision"] = 7,
            ["credentialVersion"] = secret ?? "private-version",
            ["configurationHash"] = "private-hash",
            ["providerState"] = "Available",
            ["secret"] = secret,
        };

    private static string WriteFixtureFile(JsonObject body)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "byok-input-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, body.ToJsonString());
        return path;
    }

    private static HttpResponseMessage JsonResponse(
        JsonObject body,
        HttpStatusCode status = HttpStatusCode.OK) =>
        JsonResponse(body.ToJsonString(), status);

    private static HttpResponseMessage JsonResponse(
        string body,
        HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Fixture : HttpMessageHandler, IGatewayClientFactory, IDisposable
    {
        private readonly HttpClient _http;

        public Fixture()
        {
            _http = new HttpClient(this, disposeHandler: false);
        }

        public List<Request> Requests { get; } = [];
        public StringWriter Output { get; } = new();
        public StringWriter Error { get; } = new();
        public Authentication Authentication { get; } = new();
        public Func<Request, HttpResponseMessage>? Respond { get; set; }
        public Action<JsonObject>? TransformOpenApi { get; set; }
        public Func<JsonObject, JsonObject>? TransformAuthContext { get; set; }
        public Stream? StandardInput { get; set; }
        public bool? StandardInputRedirected { get; set; }
        public IGatewayClient? LastCreatedClient { get; private set; }

        public IGatewayClient Create(CliIdentity identity) =>
            LastCreatedClient = new GatewayClient(_http, identity, Authentication);

        public Task<int> Run(params string[] args) =>
            Run(CancellationToken.None, args);

        public Task<int> Run(CancellationToken cancellationToken, params string[] args) =>
            CliApplication.RunAsync(
                ["platform", "byok", .. args, .. TargetOptions],
                new CliRuntimeOptions(
                    GatewayClientFactory: this,
                    Authentication: Authentication,
                    Output: Output,
                    Error: Error)
                {
                    StandardInput = StandardInput,
                    StandardInputRedirected = StandardInputRedirected,
                },
                cancellationToken);

        public Task<int> RunProject(params string[] args) =>
            CliApplication.RunAsync(
                ["project", "byok", .. args, "--project-id", "project-1", .. TargetOptions],
                new CliRuntimeOptions(
                    GatewayClientFactory: this,
                    Authentication: Authentication,
                    Output: Output,
                    Error: Error)
                {
                    StandardInput = StandardInput,
                    StandardInputRedirected = StandardInputRedirected,
                });

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/openapi/v1.json")
            {
                var openApi = typeof(GatewayEndpoints).Assembly.GetType("Agentweaver.Gateway.GatewayOpenApi")!;
                var emitted = openApi.GetMethod("CreateDocument", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, null);
                var document = JsonNode.Parse(JsonSerializer.Serialize(
                    emitted, new JsonSerializerOptions(JsonSerializerDefaults.Web)))!.AsObject();
                TransformOpenApi?.Invoke(document);
                return JsonResponse(document.ToJsonString());
            }
            if (path == "/api/v1/authorization/context")
            {
                var context = new JsonObject
                {
                    ["contractVersion"] = 1,
                    ["issuer"] = "https://identity.example/",
                    ["actorId"] = "actor-1",
                    ["tenantId"] = "tenant-1",
                    ["membershipRevision"] = 1,
                    ["boundProjectId"] = Authentication.Identity?.ProjectId,
                    ["boundRunId"] = Authentication.Identity?.RunId,
                    ["effectiveAuthority"] = new JsonArray(),
                };
                if (TransformAuthContext is not null)
                    context = TransformAuthContext(context);
                return JsonResponse(context);
            }

            var bodyBytes = request.Content is null
                ? []
                : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var body = Encoding.UTF8.GetString(bodyBytes);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in request.Headers)
                headers[h.Key] = string.Join(",", h.Value);
            var captured = new Request(
                request.Method.Method,
                request.RequestUri.PathAndQuery,
                path,
                body,
                bodyBytes,
                request.Content?.Headers.ContentType?.MediaType,
                headers);
            Requests.Add(captured);
            return Respond?.Invoke(captured) ??
                   JsonResponse("{}", HttpStatusCode.NotFound);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _http.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class ReadTrackingStream(byte[] data) : MemoryStream(data)
    {
        public int ReadCalls { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed record Request(
        string Method,
        string PathAndQuery,
        string Path,
        string Body,
        byte[] BodyBytes,
        string? ContentType,
        IReadOnlyDictionary<string, string> Headers)
    {
        public string Query => PathAndQuery.Contains('?')
            ? PathAndQuery[(PathAndQuery.IndexOf('?') + 1)..]
            : string.Empty;
    }

    private sealed class Authentication : ICliAuthentication
    {
        public CliIdentity? Identity { get; private set; }

        public Task<string> GetBearerAsync(
            CliIdentity identity,
            InteractionPolicy interaction,
            CancellationToken cancellationToken)
        {
            Identity = identity;
            return Task.FromResult("safe-bearer");
        }

        public Task<CliAuthStatus> LoginAsync(CliIdentity identity, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CliAuthStatus> StatusAsync(CliIdentity identity, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task LogoutAsync(CliIdentity identity, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
