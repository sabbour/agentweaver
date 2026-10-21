using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class GatewayKnowledgeOpenApiRegressionTests
{
    private static readonly string[] KnowledgeWriteOperationIds =
    [
        "createKnowledgeRecord",
        "updateKnowledgeRecord",
        "restoreKnowledgeRecord",
        "approveKnowledgeDecision",
        "importKnowledgeRecords",
        "promoteKnowledgeProposal",
        "rejectKnowledgeProposal",
    ];

    [Fact]
    public async Task ServedOpenApiRequiresIdempotencyKeyOnlyForKnowledgeWrites()
    {
        using var rsa = RSA.Create(2048);
        await using var gateway = await GatewayResourceServer.StartAsync(
            new RsaSecurityKey(rsa),
            () => new HttpClientHandler(),
            (_, _, _) => throw new InvalidOperationException(
                "The OpenAPI endpoint must not call an owner."));

        using var response = await gateway.Client.GetAsync("/openapi/v1.json");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");

        foreach (var operationId in KnowledgeWriteOperationIds)
        {
            var operation = FindOperation(paths, operationId);
            var header = Assert.Single(
                operation.GetProperty("parameters").EnumerateArray(),
                parameter => parameter.GetProperty("name").GetString() == "Idempotency-Key");
            Assert.Equal("header", header.GetProperty("in").GetString());
            Assert.True(header.GetProperty("required").GetBoolean());

            var schema = header.GetProperty("schema");
            Assert.Equal("string", schema.GetProperty("type").GetString());
            Assert.Equal(1, schema.GetProperty("minLength").GetInt32());
            Assert.Equal(128, schema.GetProperty("maxLength").GetInt32());
            Assert.Equal("^[A-Za-z0-9._:-]+$", schema.GetProperty("pattern").GetString());
        }

        var operationsWithIdempotencyKey = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in paths.EnumerateObject())
        foreach (var method in path.Value.EnumerateObject())
        {
            var operation = method.Value;
            if (HasIdempotencyKeyHeader(operation))
                operationsWithIdempotencyKey.Add(operation.GetProperty("operationId").GetString()!);
        }

        Assert.Equal(
            KnowledgeWriteOperationIds.OrderBy(id => id, StringComparer.Ordinal),
            operationsWithIdempotencyKey);
    }

    [Fact]
    public async Task ServedOpenApiDocumentsOptionalTaskModelSelectionOnWorkflowSteps()
    {
        using var rsa = RSA.Create(2048);
        await using var gateway = await GatewayResourceServer.StartAsync(
            new RsaSecurityKey(rsa),
            () => new HttpClientHandler(),
            (_, _, _) => throw new InvalidOperationException(
                "The OpenAPI endpoint must not call an owner."));

        using var response = await gateway.Client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var step = document.RootElement.GetProperty("components").GetProperty("schemas")
            .GetProperty("WorkflowStepDefinition");
        var required = step.GetProperty("required").EnumerateArray()
            .Select(item => item.GetString()!).ToArray();
        var modelTaskKind = step.GetProperty("properties").GetProperty("modelTaskKind");
        var choices = modelTaskKind.GetProperty("anyOf")[0].GetProperty("enum")
            .EnumerateArray().Select(item => item.GetString()!).ToArray();

        Assert.DoesNotContain("modelTaskKind", required);
        Assert.Equal(
            ["planning", "review", "design", "security", "creativeWriting", "complex", "codeWriting", "straightforward"],
            choices);
    }

    [Fact]
    public async Task KnowledgeLifecycleRoutesExposeStrictTransferContractsAndRequestBounds()
    {
        using var rsa = RSA.Create(2048);
        await using var gateway = await GatewayResourceServer.StartAsync(
            new RsaSecurityKey(rsa),
            () => new HttpClientHandler(),
            (_, _, _) => throw new InvalidOperationException(
                "The OpenAPI endpoint must not call an owner."));

        using var response = await gateway.Client.GetAsync("/openapi/v1.json");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        var paths = root.GetProperty("paths");
        var import = paths.GetProperty(
            "/api/v1/projects/{projectId}/runs/{runId}/agents/{agentId}/records/import")
            .GetProperty("post");
        Assert.Equal("importKnowledgeRecords", import.GetProperty("operationId").GetString());
        Assert.Equal("Knowledge", import.GetProperty("x-agentweaver-owner").GetString());
        Assert.Equal(1_048_576, import.GetProperty("x-agentweaver-max-request-body-bytes").GetInt32());
        Assert.Equal(1_048_576L, gateway.GetRequestSizeLimit(
            "/api/v1/projects/{projectId}/runs/{runId}/agents/{agentId}/records/import")?.MaxRequestBodySize);
        var importBody = import.GetProperty("requestBody");
        Assert.True(importBody.GetProperty("required").GetBoolean());
        Assert.Equal(
            "#/components/schemas/KnowledgeRecordTransferBundle",
            importBody.GetProperty("content").GetProperty("application/json")
                .GetProperty("schema").GetProperty("$ref").GetString());

        var export = paths.GetProperty(
            "/api/v1/projects/{projectId}/runs/{runId}/agents/{agentId}/records/export")
            .GetProperty("get");
        Assert.Equal("exportKnowledgeRecords", export.GetProperty("operationId").GetString());
        Assert.Equal(
            "#/components/schemas/KnowledgeRecordTransferBundle",
            export.GetProperty("responses").GetProperty("200").GetProperty("content")
                .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        Assert.False(HasIdempotencyKeyHeader(export));

        var schemas = root.GetProperty("components").GetProperty("schemas");
        var decisionRecord = schemas.GetProperty("KnowledgeRecord");
        Assert.Contains("superseded",
            decisionRecord.GetProperty("properties").GetProperty("state").GetProperty("enum")
                .EnumerateArray().Select(value => value.GetString()));
        Assert.True(decisionRecord.GetProperty("additionalProperties").ValueKind == JsonValueKind.False);
        Assert.True(decisionRecord.GetProperty("properties").TryGetProperty("supersededByRecordId", out _));

        var revision = schemas.GetProperty("KnowledgeRecordRevision");
        var revisionProperties = revision.GetProperty("properties");
        Assert.Equal(JsonValueKind.Array, revisionProperties.GetProperty("reason").GetProperty("anyOf").ValueKind);
        Assert.True(revisionProperties.TryGetProperty("supersededByRecordId", out _));
        Assert.True(revisionProperties.TryGetProperty("sourceRunId", out _));
        Assert.True(revisionProperties.TryGetProperty("sourceSessionId", out _));
        Assert.True(revisionProperties.TryGetProperty("actorFingerprint", out _));
        Assert.True(revisionProperties.TryGetProperty("changeKind", out _));

        var bundle = schemas.GetProperty("KnowledgeRecordTransferBundle");
        Assert.Equal("agentweaver.knowledge-transfer.v1",
            bundle.GetProperty("properties").GetProperty("format").GetProperty("const").GetString());
        Assert.Equal(1, bundle.GetProperty("properties").GetProperty("schemaVersion")
            .GetProperty("const").GetInt32());
        Assert.Equal(25, bundle.GetProperty("properties").GetProperty("records")
            .GetProperty("maxItems").GetInt32());
        Assert.False(bundle.GetProperty("additionalProperties").GetBoolean());
        Assert.True(schemas.GetProperty("KnowledgeRecordImportResult").GetProperty("properties")
            .TryGetProperty("isDuplicate", out _));
    }

    [Fact]
    public async Task SkillsMarketplaceOpenApiRequiresOnlyTheOwnerRevisionQueryParameters()
    {
        using var rsa = RSA.Create(2048);
        await using var gateway = await GatewayResourceServer.StartAsync(
            new RsaSecurityKey(rsa),
            () => new HttpClientHandler(),
            (_, _, _) => throw new InvalidOperationException(
                "The OpenAPI endpoint must not call an owner."));

        using var response = await gateway.Client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");

        AssertRevisionQuery(
            FindOperation(paths, "removeMarketplaceSource"),
            "expectedRevision",
            required: true);
        var browse = FindOperation(paths, "browseMarketplaceSource");
        AssertRevisionQuery(browse, "expectedSourceRevision", required: true);
        foreach (var optionalName in new[] { "query", "page", "pageSize" })
        {
            var parameter = Assert.Single(
                browse.GetProperty("parameters").EnumerateArray(),
                item => item.GetProperty("name").GetString() == optionalName);
            Assert.False(parameter.GetProperty("required").GetBoolean());
        }

        var preview = FindOperation(paths, "previewMarketplaceSkill");
        var import = FindOperation(paths, "importMarketplaceSkill");
        foreach (var operation in new[] { preview, import })
        {
            Assert.DoesNotContain(
                operation.GetProperty("parameters").EnumerateArray(),
                parameter => parameter.GetProperty("in").GetString() == "query");
            Assert.True(operation.GetProperty("requestBody").GetProperty("required").GetBoolean());
            Assert.True(operation.GetProperty("responses").TryGetProperty("200", out _));
        }
        Assert.Equal(
            "#/components/schemas/MarketplaceSkillPreviewRequest",
            preview.GetProperty("requestBody").GetProperty("content")
                .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        Assert.Equal(
            "#/components/schemas/MarketplaceSkillImportRequest",
            import.GetProperty("requestBody").GetProperty("content")
                .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        Assert.Equal(
            "#/components/schemas/SkillContentPreview",
            preview.GetProperty("responses").GetProperty("200").GetProperty("content")
                .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        Assert.Equal(
            "#/components/schemas/SkillContentImportReceipt",
            import.GetProperty("responses").GetProperty("200").GetProperty("content")
                .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        foreach (var status in new[] { "400", "404", "409", "422", "503" })
        {
            Assert.True(preview.GetProperty("responses").TryGetProperty(status, out _));
            Assert.True(import.GetProperty("responses").TryGetProperty(status, out _));
        }
        Assert.True(import.GetProperty("responses").TryGetProperty("500", out _));
        Assert.Contains("not the source bytes", preview.GetProperty("description").GetString(),
            StringComparison.Ordinal);
        var schemasForMarketplaceContent = document.RootElement.GetProperty("components").GetProperty("schemas");
        var previewRequest = schemasForMarketplaceContent.GetProperty("MarketplaceSkillPreviewRequest");
        Assert.False(previewRequest.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            new[] { "expectedSourceRevision", "resolvedCommitSha", "selectedPath" },
            previewRequest.GetProperty("required").EnumerateArray()
                .Select(property => property.GetString()).ToArray());
        var previewProperties = previewRequest.GetProperty("properties");
        AssertPropertyType(previewProperties, "expectedSourceRevision", "integer");
        AssertPropertyType(previewProperties, "resolvedCommitSha", "string");
        AssertPropertyType(previewProperties, "selectedPath", "string");

        var importRequest = schemasForMarketplaceContent.GetProperty("MarketplaceSkillImportRequest");
        Assert.False(importRequest.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            new[]
            {
                "expectedSourceRevision", "resolvedCommitSha", "selectedPath", "idempotencyKey",
                "expectedContentDigest",
            },
            importRequest.GetProperty("required").EnumerateArray()
                .Select(property => property.GetString()).ToArray());
        var importProperties = importRequest.GetProperty("properties");
        AssertPropertyType(importProperties, "expectedSourceRevision", "integer");
        AssertPropertyType(importProperties, "resolvedCommitSha", "string");
        AssertPropertyType(importProperties, "selectedPath", "string");
        AssertPropertyType(importProperties, "idempotencyKey", "string");
        AssertPropertyType(importProperties, "expectedContentDigest", "string");
        AssertNullablePropertyType(importProperties, "skillId", "string");
        AssertNullablePropertyType(importProperties, "expectedRevision", "integer");

        var inventory = FindOperation(paths, "listProjectSkillInventory");
        var inventoryQuery = inventory.GetProperty("parameters").EnumerateArray()
            .Where(parameter => parameter.GetProperty("in").GetString() == "query")
            .ToArray();
        Assert.Equal(
            new[] { "cursor", "pageSize" },
            inventoryQuery.Select(parameter => parameter.GetProperty("name").GetString()).ToArray());
        Assert.All(inventoryQuery, parameter => Assert.False(parameter.GetProperty("required").GetBoolean()));
        var pageSizeSchema = Assert.Single(
            inventoryQuery,
            parameter => parameter.GetProperty("name").GetString() == "pageSize")
            .GetProperty("schema");
        Assert.Equal("integer", pageSizeSchema.GetProperty("type").GetString());
        Assert.Equal(1, pageSizeSchema.GetProperty("minimum").GetInt32());
        Assert.Equal(50, pageSizeSchema.GetProperty("maximum").GetInt32());
        Assert.Equal(25, pageSizeSchema.GetProperty("default").GetInt32());
        Assert.True(inventory.GetProperty("responses").TryGetProperty("400", out _));
        Assert.True(inventory.GetProperty("responses").TryGetProperty("404", out _));
        Assert.Contains("does not make multiple pages a snapshot",
            inventory.GetProperty("description").GetString(), StringComparison.Ordinal);

        var existingProjectQuery = Assert.Single(
            FindOperation(paths, "getProject").GetProperty("parameters").EnumerateArray(),
            item => item.GetProperty("name").GetString() == "runId");
        Assert.False(existingProjectQuery.GetProperty("required").GetBoolean());

        var runList = FindOperation(paths, "listProjectRuns");
        var runListQuery = runList.GetProperty("parameters").EnumerateArray()
            .Where(parameter => parameter.GetProperty("in").GetString() == "query")
            .ToArray();
        Assert.Equal(
            new[] { "state", "cursor", "pageSize" },
            runListQuery.Select(parameter => parameter.GetProperty("name").GetString()).ToArray());
        Assert.All(runListQuery, parameter => Assert.False(parameter.GetProperty("required").GetBoolean()));
        var runPageSize = runListQuery.Single(parameter => parameter.GetProperty("name").GetString() == "pageSize")
            .GetProperty("schema");
        Assert.Equal(100, runPageSize.GetProperty("maximum").GetInt32());
        Assert.Equal(25, runPageSize.GetProperty("default").GetInt32());
        Assert.True(runList.GetProperty("responses").TryGetProperty("400", out _));
        Assert.Contains("run-bound token sees only its own run",
            runList.GetProperty("description").GetString(), StringComparison.Ordinal);
        var runSummary = document.RootElement.GetProperty("components").GetProperty("schemas")
            .GetProperty("ProjectRunSummary");
        Assert.False(runSummary.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            new[] { "runId", "rootSessionId", "title", "goal", "state", "createdAt", "updatedAt", "completedAt", "startedBy" },
            runSummary.GetProperty("required").EnumerateArray().Select(item => item.GetString()).ToArray());

        foreach (var operationId in new[] { "listProjectIssues", "listProjectPullRequests" })
        {
            var sourceList = FindOperation(paths, operationId);
            var sourceQuery = sourceList.GetProperty("parameters").EnumerateArray()
                .Where(parameter => parameter.GetProperty("in").GetString() == "query")
                .ToArray();
            Assert.Equal(
                new[] { "state", "cursor", "pageSize" },
                sourceQuery.Select(parameter => parameter.GetProperty("name").GetString()).ToArray());
            Assert.All(sourceQuery, parameter => Assert.False(parameter.GetProperty("required").GetBoolean()));
            var sourcePageSize = sourceQuery
                .Single(parameter => parameter.GetProperty("name").GetString() == "pageSize")
                .GetProperty("schema");
            Assert.Equal(1, sourcePageSize.GetProperty("minimum").GetInt32());
            Assert.Equal(100, sourcePageSize.GetProperty("maximum").GetInt32());
            Assert.Equal(25, sourcePageSize.GetProperty("default").GetInt32());
            foreach (var status in new[] { "200", "400", "403", "502" })
                Assert.True(sourceList.GetProperty("responses").TryGetProperty(status, out _), status);
            Assert.Contains("makes no source control provider call",
                sourceList.GetProperty("description").GetString(), StringComparison.Ordinal);
        }
        var issueState = FindOperation(paths, "listProjectIssues").GetProperty("parameters").EnumerateArray()
            .Single(parameter => parameter.GetProperty("name").GetString() == "state").GetProperty("schema");
        Assert.Equal(new[] { "open", "closed", "all" },
            issueState.GetProperty("enum").EnumerateArray().Select(item => item.GetString()).ToArray());
        Assert.Equal("open", issueState.GetProperty("default").GetString());
        Assert.Contains("rejects run-bound tokens",
            FindOperation(paths, "listProjectIssues").GetProperty("description").GetString(),
            StringComparison.Ordinal);
        Assert.Contains("run-bound token sees only its run's pull requests",
            FindOperation(paths, "listProjectPullRequests").GetProperty("description").GetString(),
            StringComparison.Ordinal);
        var componentSchemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var issueSummary = componentSchemas.GetProperty("ProjectIssueSummary");
        Assert.False(issueSummary.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            new[] { "taskId", "taskState", "providerId", "repository", "number", "title", "state", "createdAt", "updatedAt" },
            issueSummary.GetProperty("required").EnumerateArray().Select(item => item.GetString()).ToArray());
        var pullRequestSummary = componentSchemas.GetProperty("ProjectPullRequestSummary");
        Assert.False(pullRequestSummary.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            new[]
            {
                "providerId", "repository", "number", "title", "htmlUrl", "state", "headBranch", "headSha",
                "baseBranch", "baseSha", "runId", "disposition", "recordedAt", "updatedAt"
            },
            pullRequestSummary.GetProperty("required").EnumerateArray().Select(item => item.GetString()).ToArray());
        var issuePage = componentSchemas.GetProperty("ProjectIssuePage");
        Assert.False(issuePage.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(new[] { "projectId", "items", "nextCursor" },
            issuePage.GetProperty("required").EnumerateArray().Select(item => item.GetString()).ToArray());
        var pullRequestPage = componentSchemas.GetProperty("ProjectPullRequestPage");
        Assert.False(pullRequestPage.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(new[] { "projectId", "coverage", "items", "nextCursor" },
            pullRequestPage.GetProperty("required").EnumerateArray().Select(item => item.GetString()).ToArray());
        Assert.Equal(new[] { "recordedSince", "includesEarlierPullRequests" },
            componentSchemas.GetProperty("ProjectPullRequestCoverage").GetProperty("required").EnumerateArray()
                .Select(item => item.GetString()).ToArray());
        var pullRequestState = FindOperation(paths, "listProjectPullRequests").GetProperty("parameters")
            .EnumerateArray().Single(parameter => parameter.GetProperty("name").GetString() == "state")
            .GetProperty("schema");
        Assert.Equal(new[] { "open", "closed", "merged", "all" },
            pullRequestState.GetProperty("enum").EnumerateArray().Select(item => item.GetString()).ToArray());
        Assert.True(FindOperation(paths, "listProjectPullRequests").GetProperty("responses")
            .TryGetProperty("503", out var unavailable));
        Assert.Contains("pull_request_records_unavailable",
            unavailable.GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Contains("isn't a complete pull request history",
            FindOperation(paths, "listProjectPullRequests").GetProperty("description").GetString(),
            StringComparison.Ordinal);

        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var inventoryPage = schemas.GetProperty("ProjectSkillInventoryPage");
        Assert.False(inventoryPage.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            new[] { "configurationRevision", "assignments", "revisions", "pageSize", "nextCursor" },
            inventoryPage.GetProperty("required").EnumerateArray()
                .Select(property => property.GetString()).ToArray());
        var pageProperties = inventoryPage.GetProperty("properties");
        AssertPropertyType(pageProperties, "configurationRevision", "integer");
        AssertPropertyType(pageProperties, "assignments", "array");
        AssertPropertyType(pageProperties, "revisions", "array");
        AssertPropertyType(pageProperties, "pageSize", "integer");
        AssertNullablePropertyType(pageProperties, "nextCursor", "string");
        Assert.Equal(
            "#/components/schemas/ProjectSkillInventoryAssignment",
            pageProperties.GetProperty("assignments").GetProperty("items").GetProperty("$ref").GetString());
        Assert.Equal(
            "#/components/schemas/ProjectSkillInventoryRevision",
            pageProperties.GetProperty("revisions").GetProperty("items").GetProperty("$ref").GetString());

        var assignmentSchema = schemas.GetProperty("ProjectSkillInventoryAssignment");
        Assert.False(assignmentSchema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            new[] { "skillId", "enabled", "order", "revision", "contentDigest", "agentIds" },
            assignmentSchema.GetProperty("required").EnumerateArray()
                .Select(property => property.GetString()).ToArray());
        var assignmentProperties = assignmentSchema.GetProperty("properties");
        foreach (var (name, type) in new[]
        {
            ("skillId", "string"), ("enabled", "boolean"), ("order", "integer"),
        })
            AssertPropertyType(assignmentProperties, name, type);
        AssertNullablePropertyType(assignmentProperties, "revision", "integer");
        AssertNullablePropertyType(assignmentProperties, "contentDigest", "string");
        AssertNullablePropertyType(assignmentProperties, "agentIds", "array");
        Assert.Equal(
            "string",
            assignmentProperties.GetProperty("agentIds").GetProperty("anyOf")[0]
                .GetProperty("items").GetProperty("type").GetString());

        var revisionSchema = schemas.GetProperty("ProjectSkillInventoryRevision");
        Assert.False(revisionSchema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            new[]
            {
                "skillId", "revision", "name", "description", "contentDigest", "resourceCount",
                "totalBytes", "createdByActorId", "createdAt", "sourceId", "sourceRevision",
                "requestedRef", "resolvedCommitSha", "selectedPath", "isRevoked", "revokedByActorId",
                "revokedAt",
            },
            revisionSchema.GetProperty("required").EnumerateArray()
                .Select(property => property.GetString()).ToArray());
        var revisionProperties = revisionSchema.GetProperty("properties");
        foreach (var (name, type) in new[]
        {
            ("skillId", "string"), ("revision", "integer"), ("name", "string"),
            ("description", "string"), ("contentDigest", "string"), ("resourceCount", "integer"),
            ("totalBytes", "integer"), ("createdByActorId", "string"), ("createdAt", "string"),
            ("isRevoked", "boolean"),
        })
            AssertPropertyType(revisionProperties, name, type);
        Assert.Equal(
            "date-time",
            revisionProperties.GetProperty("createdAt").GetProperty("format").GetString());
        foreach (var (name, type) in new[]
        {
            ("sourceId", "string"), ("sourceRevision", "string"), ("requestedRef", "string"),
            ("resolvedCommitSha", "string"), ("selectedPath", "string"),
            ("revokedByActorId", "string"), ("revokedAt", "string"),
        })
            AssertNullablePropertyType(revisionProperties, name, type);
        Assert.Equal(
            "date-time",
            revisionProperties.GetProperty("revokedAt").GetProperty("anyOf")[0]
                .GetProperty("format").GetString());

        foreach (var schemaName in new[]
        {
            "CreateMarketplaceSourceRequest",
            "UpdateMarketplaceSourceRequest",
            "SkillContentCandidateRequest",
            "ImportSkillContentRequest",
            "UpdateSkillAssignmentRequest",
        })
        {
            Assert.False(schemas.GetProperty(schemaName).GetProperty("additionalProperties").GetBoolean());
        }

        Assert.Equal(3L * 1024 * 1024, gateway.GetRequestSizeLimit("/api/v1/skills/preview")?.MaxRequestBodySize);
        Assert.Equal(
            3L * 1024 * 1024,
            gateway.GetRequestSizeLimit("/api/v1/projects/{projectId}/skills/import")?.MaxRequestBodySize);
        Assert.Equal(
            3L * 1024 * 1024,
            gateway.GetRequestSizeLimit(
                "/api/v1/projects/{projectId}/skill-marketplaces/sources/{sourceId}/preview")?.MaxRequestBodySize);
        Assert.Equal(
            3L * 1024 * 1024,
            gateway.GetRequestSizeLimit(
                "/api/v1/projects/{projectId}/skill-marketplaces/sources/{sourceId}/import")?.MaxRequestBodySize);
        foreach (var operationId in new[]
        {
            "previewSkillContent", "importProjectSkill", "previewMarketplaceSkill", "importMarketplaceSkill",
        })
        {
            Assert.Equal(
                3 * 1024 * 1024,
                FindOperation(paths, operationId)
                    .GetProperty("x-agentweaver-max-request-body-bytes").GetInt32());
        }
    }

    [Fact]
    public async Task RuntimeObservationV1IsExposedWithStrictSanitizedSchemas()
    {
        using var rsa = RSA.Create(2048);
        await using var gateway = await GatewayResourceServer.StartAsync(
            new RsaSecurityKey(rsa),
            () => new HttpClientHandler(),
            (_, _, _) => throw new InvalidOperationException(
                "The OpenAPI endpoint must not call an owner."));

        using var response = await gateway.Client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        var operation = root.GetProperty("paths").GetProperty(
                "/api/v1/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/runtime-observation/v1")
            .GetProperty("get");

        Assert.Equal("readRuntimeObservationV1", operation.GetProperty("operationId").GetString());
        Assert.Equal("Orchestrator", operation.GetProperty("x-agentweaver-owner").GetString());
        Assert.Equal(
            "#/components/schemas/RuntimeObservationV1",
            operation.GetProperty("responses").GetProperty("200").GetProperty("content")
                .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        var cacheControl = operation.GetProperty("responses").GetProperty("200")
            .GetProperty("headers").GetProperty("Cache-Control");
        Assert.Equal("string", cacheControl.GetProperty("schema").GetProperty("type").GetString());
        Assert.Equal("no-store", cacheControl.GetProperty("schema").GetProperty("example").GetString());
        Assert.True(operation.GetProperty("responses").TryGetProperty("409", out _));
        Assert.True(operation.GetProperty("responses").TryGetProperty("503", out _));

        var schemas = root.GetProperty("components").GetProperty("schemas");
        var observation = schemas.GetProperty("RuntimeObservationV1");
        Assert.False(observation.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            new[] { "contractVersion", "registration", "execution", "creditGuard", "dispatches", "toolReservations" },
            observation.GetProperty("required").EnumerateArray()
                .Select(property => property.GetString()).ToArray());
        var section = schemas.GetProperty("RuntimeObservationCreditGuardSection");
        Assert.Equal(
            new[] { "Available", "Unknown", "Unavailable" },
            section.GetProperty("properties").GetProperty("state").GetProperty("enum")
                .EnumerateArray().Select(value => value.GetString()).ToArray());
        var dispatch = schemas.GetProperty("RuntimeObservationDispatch");
        foreach (var forbiddenField in new[]
        {
            "promptHash", "requestHash", "sourceReportJson", "acceptedByokDescriptor",
            "costBindingIdentity", "selectedModelId",
        })
            Assert.False(dispatch.GetProperty("properties").TryGetProperty(forbiddenField, out _));
        var pendingDispatch = schemas.GetProperty("RuntimeObservationPendingDispatch");
        Assert.Equal(new[] { "messageId" },
            pendingDispatch.GetProperty("required").EnumerateArray()
                .Select(property => property.GetString()).ToArray());
        Assert.False(pendingDispatch.GetProperty("properties").TryGetProperty("promptSha256", out _));
        Assert.Equal(100, schemas.GetProperty("RuntimeObservationDispatchCollection")
            .GetProperty("properties").GetProperty("items").GetProperty("maxItems").GetInt32());
    }

    [Fact]
    public async Task RuntimeObservationV1ForwardsTheAuthenticatedRunOwnerRead()
    {
        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa);
        var now = DateTime.UtcNow;
        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri,
            "https://api.test",
            [new Claim("sub", "runtime-observation-test-actor"), new Claim("scope", "api.read")],
            now.AddMinutes(-1),
            now.AddMinutes(5),
            new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256));
        token.Header["typ"] = "at+jwt";
        var bearer = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token);
        var forwarded = new List<(string Owner, string Method, string PathAndQuery, string? Authorization, string? Tenant)>();
        await using var gateway = await GatewayResourceServer.StartAsync(
            signingKey,
            () => new HttpClientHandler(),
            (owner, request, _) =>
            {
                request.Headers.TryGetValues("X-Agentweaver-Tenant", out var tenants);
                forwarded.Add((owner.ToString(), request.Method.Method, request.RequestUri!.PathAndQuery,
                    request.Headers.Authorization?.ToString(), tenants?.SingleOrDefault()));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"contractVersion\":1}", Encoding.UTF8, "application/json"),
                });
            });

        using var ownerRequest = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/v1/projects/project-1/runs/run-1/coordination/sessions/session-1/runtime-observation/v1");
        ownerRequest.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        ownerRequest.Headers.TryAddWithoutValidation("X-Agentweaver-Tenant", "tenant-1");
        using var response = await gateway.Client.SendAsync(ownerRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("{\"contractVersion\":1}", await response.Content.ReadAsStringAsync());
        var call = Assert.Single(forwarded);
        Assert.Equal("Orchestrator", call.Owner);
        Assert.Equal("GET", call.Method);
        Assert.Equal(
            "/api/projects/project-1/runs/run-1/coordination/sessions/session-1/runtime-observation/v1",
            call.PathAndQuery);
        Assert.Equal("Bearer " + bearer, call.Authorization);
        Assert.Equal("tenant-1", call.Tenant);
    }

    [Fact]
    public async Task SkillsMarketplaceRoutesForwardExactOwnerPathsBodiesBearerAndTenant()
    {
        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa);
        var now = DateTime.UtcNow;
        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri,
            "https://api.test",
            [new Claim("sub", "skills-marketplace-test-actor"), new Claim("scope", "api.read")],
            now.AddMinutes(-1),
            now.AddMinutes(5),
            new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256));
        token.Header["typ"] = "at+jwt";
        var bearer = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token);
        var forwarded = new List<(string Method, string PathAndQuery, string? Authorization, string? Tenant, string Body)>();
        const string ownerResponse = "{\"owner\":\"response\"}";
        await using var gateway = await GatewayResourceServer.StartAsync(
            signingKey,
            () => new RecordingOwnerHandler(async (request, cancellationToken) =>
            {
                var body = request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken);
                request.Headers.TryGetValues("X-Agentweaver-Tenant", out var tenantValues);
                forwarded.Add((
                    request.Method.Method,
                    request.RequestUri!.PathAndQuery,
                    request.Headers.Authorization?.ToString(),
                    tenantValues?.SingleOrDefault(),
                    body));
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent(ownerResponse, Encoding.UTF8, "application/json"),
                };
            }),
            (_, _, _) => throw new InvalidOperationException(
                "Skills and marketplace requests must use the Projects owner."));

        var ownerRequests = new (HttpMethod Method, string PublicPath, string OwnerPath, string Body)[]
        {
            (HttpMethod.Get,
                "/api/v1/projects/project-1/skill-marketplaces/sources",
                "/api/projects/project-1/skill-marketplaces/sources",
                string.Empty),
            (HttpMethod.Post,
                "/api/v1/projects/project-1/skill-marketplaces/sources",
                "/api/projects/project-1/skill-marketplaces/sources",
                "{\"repository\":\"example/skills\"}"),
            (HttpMethod.Put,
                "/api/v1/projects/project-1/skill-marketplaces/sources/source-1",
                "/api/projects/project-1/skill-marketplaces/sources/source-1",
                "{\"expectedRevision\":4,\"repository\":\"example/skills\"}"),
            (HttpMethod.Delete,
                "/api/v1/projects/project-1/skill-marketplaces/sources/source-1?expectedRevision=4",
                "/api/projects/project-1/skill-marketplaces/sources/source-1?expectedRevision=4",
                string.Empty),
            (HttpMethod.Get,
                "/api/v1/projects/project-1/skill-marketplaces/sources/source-1/browse?expectedSourceRevision=4&query=alpha&page=2&pageSize=5",
                "/api/projects/project-1/skill-marketplaces/sources/source-1/browse?expectedSourceRevision=4&query=alpha&page=2&pageSize=5",
                string.Empty),
            (HttpMethod.Post,
                "/api/v1/projects/project-1/skill-marketplaces/sources/source-1/preview",
                "/api/projects/project-1/skill-marketplaces/sources/source-1/preview",
                "{\"expectedSourceRevision\":4,\"resolvedCommitSha\":\"0123456789abcdef0123456789abcdef01234567\",\"selectedPath\":\"skills/example\"}"),
            (HttpMethod.Post,
                "/api/v1/projects/project-1/skill-marketplaces/sources/source-1/import",
                "/api/projects/project-1/skill-marketplaces/sources/source-1/import",
                "{\"expectedSourceRevision\":4,\"resolvedCommitSha\":\"0123456789abcdef0123456789abcdef01234567\",\"selectedPath\":\"skills/example\",\"idempotencyKey\":\"intent-2\",\"expectedContentDigest\":\"sha256:expected\",\"skillId\":\"skill-1\",\"expectedRevision\":3}"),
            (HttpMethod.Post,
                "/api/v1/skills/preview",
                "/api/skills/preview",
                "{\"candidate\":{\"skillMarkdown\":\"IyBUZXN0\",\"resources\":[]}}"),
            (HttpMethod.Post,
                "/api/v1/projects/project-1/skills/import",
                "/api/projects/project-1/skills/import",
                "{\"idempotencyKey\":\"intent-1\",\"expectedContentDigest\":\"sha256:test\",\"candidate\":{\"skillMarkdown\":\"IyBUZXN0\",\"resources\":[]}}"),
            (HttpMethod.Put,
                "/api/v1/projects/project-1/skills/skill-1/assignment",
                "/api/projects/project-1/skills/skill-1/assignment",
                "{\"expectedProjectConfigurationRevision\":4,\"revision\":1,\"contentDigest\":\"sha256:test\",\"enabled\":true,\"order\":0,\"agentIds\":[\"agent-1\"]}"),
            (HttpMethod.Get,
                "/api/v1/projects/project-1/skills?cursor=opaque_cursor&pageSize=50",
                "/api/projects/project-1/skills?cursor=opaque_cursor&pageSize=50",
                string.Empty),
        };

        foreach (var ownerRequest in ownerRequests)
        {
            using var request = new HttpRequestMessage(ownerRequest.Method, ownerRequest.PublicPath);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
            request.Headers.TryAddWithoutValidation("X-Agentweaver-Tenant", "tenant-1");
            if (ownerRequest.Body.Length > 0)
                request.Content = new StringContent(ownerRequest.Body, Encoding.UTF8, "application/json");

            using var response = await gateway.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.Equal(ownerResponse, await response.Content.ReadAsStringAsync());
            Assert.True(response.Headers.CacheControl?.NoStore);
        }

        Assert.Equal(ownerRequests.Length, forwarded.Count);
        for (var index = 0; index < ownerRequests.Length; index++)
        {
            Assert.Equal(ownerRequests[index].Method.Method, forwarded[index].Method);
            Assert.Equal(ownerRequests[index].OwnerPath, forwarded[index].PathAndQuery);
            Assert.Equal("Bearer " + bearer, forwarded[index].Authorization);
            Assert.Equal("tenant-1", forwarded[index].Tenant);
            Assert.Equal(ownerRequests[index].Body, forwarded[index].Body);
        }
    }

    [Fact]
    public async Task MarketplaceSelectedSourceOwnerProblemsPassThrough()
    {
        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa);
        var now = DateTime.UtcNow;
        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri,
            "https://api.test",
            [new Claim("sub", "marketplace-skill-test-actor"), new Claim("scope", "api.read")],
            now.AddMinutes(-1),
            now.AddMinutes(5),
            new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256));
        token.Header["typ"] = "at+jwt";
        var bearer = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token);
        const string ownerBody =
            "{\"title\":\"skill conflict\",\"status\":409,\"code\":\"skill_content_conflict\"}";
        string? ownerPath = null;
        string? ownerAuthorization = null;
        string? ownerTenant = null;
        await using var gateway = await GatewayResourceServer.StartAsync(
            signingKey,
            () => new RecordingOwnerHandler((request, _) =>
            {
                ownerPath = request.RequestUri!.PathAndQuery;
                ownerAuthorization = request.Headers.Authorization?.ToString();
                request.Headers.TryGetValues("X-Agentweaver-Tenant", out var tenantValues);
                ownerTenant = tenantValues?.SingleOrDefault();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
                {
                    Content = new StringContent(ownerBody, Encoding.UTF8, "application/problem+json"),
                });
            }),
            (_, _, _) => throw new InvalidOperationException(
                "Selected marketplace skill requests must use the Projects owner."));

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/projects/project-1/skill-marketplaces/sources/source-1/import")
        {
            Content = new StringContent(
                "{\"expectedSourceRevision\":4,\"resolvedCommitSha\":\"0123456789abcdef0123456789abcdef01234567\",\"selectedPath\":\"skills/example\",\"idempotencyKey\":\"intent-2\",\"expectedContentDigest\":\"sha256:expected\"}",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.TryAddWithoutValidation("X-Agentweaver-Tenant", "tenant-1");
        using var response = await gateway.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ownerBody, await response.Content.ReadAsStringAsync());
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(
            "/api/projects/project-1/skill-marketplaces/sources/source-1/import",
            ownerPath);
        Assert.Equal("Bearer " + bearer, ownerAuthorization);
        Assert.Equal("tenant-1", ownerTenant);
    }

    [Fact]
    public async Task ProjectSkillInventoryPassesMalformedQueryToOwnerAndPreservesItsError()
    {
        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa);
        var now = DateTime.UtcNow;
        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri,
            "https://api.test",
            [new Claim("sub", "skills-inventory-test-actor"), new Claim("scope", "api.read")],
            now.AddMinutes(-1),
            now.AddMinutes(5),
            new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256));
        token.Header["typ"] = "at+jwt";
        var bearer = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token);
        const string query = "?cursor=first&cursor=second&pageSize=0&unexpected=value";
        const string ownerBody =
            "{\"title\":\"invalid inventory query\",\"status\":400,\"code\":\"skill_inventory_query_invalid\"}";
        string? ownerPathAndQuery = null;
        string? ownerAuthorization = null;
        string? ownerTenant = null;
        await using var gateway = await GatewayResourceServer.StartAsync(
            signingKey,
            () => new RecordingOwnerHandler((request, _) =>
            {
                ownerPathAndQuery = request.RequestUri!.PathAndQuery;
                ownerAuthorization = request.Headers.Authorization?.ToString();
                request.Headers.TryGetValues("X-Agentweaver-Tenant", out var tenantValues);
                ownerTenant = tenantValues?.SingleOrDefault();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(ownerBody, Encoding.UTF8, "application/problem+json"),
                });
            }),
            (_, _, _) => throw new InvalidOperationException(
                "Skill inventory requests must use the Projects owner."));

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/v1/projects/project-1/skills" + query);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.TryAddWithoutValidation("X-Agentweaver-Tenant", "tenant-1");
        using var response = await gateway.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ownerBody, await response.Content.ReadAsStringAsync());
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("/api/projects/project-1/skills" + query, ownerPathAndQuery);
        Assert.Equal("Bearer " + bearer, ownerAuthorization);
        Assert.Equal("tenant-1", ownerTenant);
    }

    private static void AssertPropertyType(JsonElement properties, string name, string expectedType)
    {
        Assert.Equal(expectedType, properties.GetProperty(name).GetProperty("type").GetString());
    }

    private static void AssertNullablePropertyType(
        JsonElement properties,
        string name,
        string expectedType)
    {
        var alternatives = properties.GetProperty(name).GetProperty("anyOf").EnumerateArray().ToArray();
        Assert.Contains(
            alternatives,
            alternative => alternative.TryGetProperty("type", out var type) &&
                type.GetString() == expectedType);
        Assert.Contains(
            alternatives,
            alternative => alternative.TryGetProperty("type", out var type) &&
                type.GetString() == "null");
    }

    private static void AssertRevisionQuery(JsonElement operation, string name, bool required)
    {
        var parameter = Assert.Single(
            operation.GetProperty("parameters").EnumerateArray(),
            item => item.GetProperty("name").GetString() == name);
        Assert.Equal("query", parameter.GetProperty("in").GetString());
        Assert.Equal(required, parameter.GetProperty("required").GetBoolean());
        var schema = parameter.GetProperty("schema");
        Assert.Equal("integer", schema.GetProperty("type").GetString());
        Assert.Equal("int64", schema.GetProperty("format").GetString());
    }

    private sealed class RecordingOwnerHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private static JsonElement FindOperation(JsonElement paths, string operationId)
    {
        var matches = new List<JsonElement>();
        foreach (var path in paths.EnumerateObject())
        foreach (var method in path.Value.EnumerateObject())
        {
            if (method.Value.TryGetProperty("operationId", out var id) &&
                id.GetString() == operationId)
            {
                matches.Add(method.Value);
            }
        }

        return Assert.Single(matches);
    }

    private static bool HasIdempotencyKeyHeader(JsonElement operation) =>
        operation.TryGetProperty("parameters", out var parameters) &&
        parameters.EnumerateArray().Any(parameter =>
            parameter.GetProperty("name").GetString() == "Idempotency-Key");
}
