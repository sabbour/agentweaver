// Ported from 0.x tests/Agentweaver.Tests/Blueprints/BlueprintPackageContractTests.cs (origin/dev). Assertions are kept exact to prove 0.x package parity.
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agentweaver.Projects.Config.BlueprintPackages;
using Xunit;

namespace Agentweaver.Projects.Config.Tests.BlueprintPackages;

public sealed class BlueprintPackageContractTests
{
    [Fact]
    public void Validate_GoldenDefinitionsOnlyPackage_ProducesAllContractDigests()
    {
        var source = CreateSource(
            ("definitions/blueprints/engineering.json", Encoding.UTF8.GetBytes("""{"name":"Engineering","weight":1.0}""")),
            ("definitions/workflows/delivery.yaml", Encoding.UTF8.GetBytes("name: delivery\r\nsteps:\r\n  - build\r\n")));

        var result = BlueprintPackageValidator.Validate(source);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Equal(2, result.Package!.Manifest.Definitions.Length);
        Assert.Equal(BlueprintPackageHash.Sha256(source.RawManifest.AsSpan()), result.Package.Digests.RawManifestSha256);
        Assert.Matches("^[0-9a-f]{64}$", result.Package.Digests.SemanticSha256);
        Assert.Equal("bbeb4578c069da4b053e50fe4bfa79f4fb52d7c935521201e0c4a314167dfdd3", result.Package.Digests.PayloadSetSha256);
        Assert.Equal(result.Package.Digests.PayloadSetSha256, BlueprintPackageValidator.CalculatePayloadSetDigest(source.Payloads));
    }

    [Fact]
    public void CalculatePayloadSetDigest_UsesTheOwnerLibraryCanonicalFraming()
    {
        var source = CreateSource(
            ("definitions/workflows/delivery.yaml", Encoding.UTF8.GetBytes("name: delivery\r\n")),
            ("definitions/blueprints/engineering.json", Encoding.UTF8.GetBytes("""{"weight":1}""")));
        var ownerPayloads = source.Payloads.Select(x => new BlueprintPackagePayload(x.Key, x.Value.ToArray()));

        Assert.Equal(
            BlueprintPackagePayloadSetDigest.Calculate(ownerPayloads),
            BlueprintPackageValidator.CalculatePayloadSetDigest(source.Payloads));
    }

    [Fact]
    public void Validate_SemanticDigest_IsStableAcrossJsonFormattingAndExactNumberSpellings()
    {
        var first = CreateSource(("definitions/blueprints/engineering.json", Encoding.UTF8.GetBytes("""{"weight":1.0,"name":"Engineering"}""")));
        var second = CreateSource(("definitions/blueprints/engineering.json", Encoding.UTF8.GetBytes("""{ "name": "Engineering", "weight": 1e0 }""")));

        var firstResult = BlueprintPackageValidator.Validate(first);
        var secondResult = BlueprintPackageValidator.Validate(second);

        Assert.True(firstResult.IsValid);
        Assert.True(secondResult.IsValid);
        Assert.Equal(secondResult.Package!.Digests.SemanticSha256, firstResult.Package!.Digests.SemanticSha256);
        Assert.NotEqual(secondResult.Package.Digests.RawManifestSha256, firstResult.Package.Digests.RawManifestSha256);
    }

    [Fact]
    public void Validate_RejectsManifestInInventoryAndDefinitionsOnlyEscapes()
    {
        var source = CreateSource(
            ("definitions/blueprints/engineering.json", Encoding.UTF8.GetBytes("{}")),
            ("manifest.json", Encoding.UTF8.GetBytes("{}")));

        var result = BlueprintPackageValidator.Validate(source);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("manifest.json is not a payload", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("not listed in the inventory", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsNonCanonicalPathAndInventoryConflict()
    {
        var bytes = Encoding.UTF8.GetBytes("{}");
        var manifest = $$"""
            {"schema_version":"1","package":{"id":"engineering","version":"1.0.0"},"definitions":[
              {"kind":"blueprint","id":"engineering","path":"../runtime.json","size":{{bytes.Length}},"sha256":"{{BlueprintPackageHash.Sha256(bytes)}}"}
            ]}
            """;
        var source = new BlueprintPackageSource(
            Encoding.UTF8.GetBytes(manifest),
            [new KeyValuePair<string, byte[]>("../runtime.json", bytes)]);

        var result = BlueprintPackageValidator.Validate(source);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("canonical definitions-only path", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsMalformedUnicodeAndNeverRoundsJsonNumbers()
    {
        var malformed = Encoding.UTF8.GetBytes("""{"schema_version":"1","package":{"id":"bad\uD800","version":"1.0.0"},"definitions":[]}""");

        var result = BlueprintPackageValidator.Validate(new BlueprintPackageSource(malformed, []));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("Unicode", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsNonAdjacentLowSurrogateWithStableError()
    {
        var malformed = Encoding.UTF8.GetBytes("""{"schema_version":"1","package":{"id":"bad\uD800\n\uDC00","version":"1.0.0"},"definitions":[]}""");

        var result = BlueprintPackageValidator.Validate(new BlueprintPackageSource(malformed, []));

        Assert.False(result.IsValid);
        Assert.Equal("manifest is not strict JSON: unpaired Unicode high surrogate.", Assert.Single(result.Errors));
    }

    [Fact]
    public void Validate_RejectsDuplicateJsonPropertyNames()
    {
        var source = CreateSource(("definitions/blueprints/engineering.json", Encoding.UTF8.GetBytes("""{"name":"first","name":"second"}""")));

        var result = BlueprintPackageValidator.Validate(source);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("duplicate JSON property", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_PayloadSetDigestChangesWhenRawPayloadBytesChange()
    {
        var first = CreateSource(("definitions/blueprints/engineering.json", Encoding.UTF8.GetBytes("""{"weight":1}""")));
        var second = CreateSource(("definitions/blueprints/engineering.json", Encoding.UTF8.GetBytes("""{"weight":2}""")));

        var firstResult = BlueprintPackageValidator.Validate(first);
        var secondResult = BlueprintPackageValidator.Validate(second);

        Assert.True(firstResult.IsValid);
        Assert.True(secondResult.IsValid);
        Assert.NotEqual(secondResult.Package!.Digests.PayloadSetSha256, firstResult.Package!.Digests.PayloadSetSha256);
    }

    [Fact]
    public void Validate_RejectsOversizeManifestBeforeParsing()
    {
        var source = new BlueprintPackageSource(new byte[BlueprintPackageLimits.MaximumManifestBytes + 1], []);

        var result = BlueprintPackageValidator.Validate(source);

        Assert.False(result.IsValid);
        Assert.Equal(["manifest exceeds the maximum byte length."], result.Errors.ToArray());
    }

    [Fact]
    public void Validate_RejectsOversizePayloadBeforeHashOrJsonWork()
    {
        var payload = new byte[BlueprintPackageLimits.MaximumPayloadBytes + 1];
        var source = CreateDefinitionSource(
            "blueprint",
            "engineering",
            "definitions/blueprints/engineering.json",
            payload,
            declaredSize: payload.Length,
            declaredHash: new string('0', 64));

        var result = BlueprintPackageValidator.Validate(source);

        Assert.False(result.IsValid);
        Assert.Contains("payload exceeds the byte limit: definitions/blueprints/engineering.json", result.Errors.ToArray());
        Assert.DoesNotContain(result.Errors, error => error.Contains("inventory SHA-256", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Errors, error => error.Contains("JSON payload is invalid", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsAggregatePayloadLimitBeforeHashOrPayloadParsing()
    {
        var payload = new byte[BlueprintPackageLimits.MaximumPayloadBytes];
        var manifest = $$"""
            {"schema_version":"1","package":{"id":"engineering","version":"1.0.0"},"definitions":[{"kind":"blueprint","id":"engineering","path":"definitions/blueprints/engineering.json","size":{{payload.Length}},"sha256":"{{new string('0', 64)}}" }]}
            """;
        var payloads = new List<KeyValuePair<string, byte[]>>
        {
            new("definitions/blueprints/engineering.json", payload),
        };
        payloads.AddRange(Enumerable.Range(0, 16).Select(index =>
            new KeyValuePair<string, byte[]>($"definitions/unlisted-{index}.bin", new byte[BlueprintPackageLimits.MaximumPayloadBytes])));

        var result = BlueprintPackageValidator.Validate(new BlueprintPackageSource(Encoding.UTF8.GetBytes(manifest), payloads));

        Assert.False(result.IsValid);
        Assert.Equal(["payload set exceeds the total byte limit."], result.Errors.ToArray());
    }

    [Theory]
    [InlineData("1")]
    [InlineData("1e")]
    public void Validate_RejectsOversizedJsonNumberTokensPromptly(string prefix)
    {
        var literal = prefix + new string('9', 1_000_000);
        var source = CreateSource(("definitions/blueprints/engineering.json", Encoding.UTF8.GetBytes($$"""{"weight":{{literal}}}""")));
        var stopwatch = Stopwatch.StartNew();

        var result = BlueprintPackageValidator.Validate(source);

        stopwatch.Stop();
        Assert.False(result.IsValid);
        Assert.Contains(
            $"JSON payload is invalid (definitions/blueprints/engineering.json): JSON number token exceeds the maximum length of {BlueprintPackageLimits.MaximumCanonicalNumberTokenLength} characters.",
            result.Errors.ToArray());
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"Validation took {stopwatch.Elapsed}, expected less than 5 seconds.");
    }

    [Fact]
    public void Validate_AcceptsCanonicalNumberAtTokenLimit()
    {
        var literal = "1e" + new string('7', BlueprintPackageLimits.MaximumCanonicalNumberTokenLength - 2);
        var source = CreateSource(("definitions/blueprints/engineering.json", Encoding.UTF8.GetBytes($$"""{"weight":{{literal}}}""")));

        var result = BlueprintPackageValidator.Validate(source);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void SemanticVersion_UsesUnboundedNumericComparison()
    {
        var lower = SemanticVersion.Parse("999999999999999999999999999999999999.1.0-alpha.9");
        var higher = SemanticVersion.Parse("1000000000000000000000000000000000000.0.0-alpha.10");

        var lowerComparison = lower.CompareTo(higher);
        Assert.True(lowerComparison < 0, $"Expected lower.CompareTo(higher) to be less than 0, but was {lowerComparison}.");
        var preReleaseComparison = SemanticVersion.Parse("1.0.0-alpha.10").CompareTo(SemanticVersion.Parse("1.0.0-alpha.2"));
        Assert.True(preReleaseComparison > 0, $"Expected alpha.10 to compare greater than alpha.2, but CompareTo was {preReleaseComparison}.");
        Assert.False(SemanticVersion.TryParse("1.0.0-01", out _));
    }

    [Fact]
    public void SemanticVersion_AcceptsLongVersionsAndHashesEqualBuildMetadataVariantsEqually()
    {
        var longVersion = new string('9', 600) + ".0.0";
        var first = SemanticVersion.Parse("1.2.3-alpha.1+build-a");
        var second = SemanticVersion.Parse("1.2.3-alpha.1+build-b");

        Assert.True(SemanticVersion.TryParse(longVersion, out var parsed));
        Assert.Equal(longVersion, parsed!.ToString());
        Assert.Equal(second, first);
        Assert.Equal(second.GetHashCode(), first.GetHashCode());
        Assert.True(BlueprintPackageValidator.Validate(CreateDefinitionSource(
            "blueprint",
            "engineering",
            "definitions/blueprints/engineering.json",
            packageVersion: longVersion)).IsValid);
    }

    [Theory]
    [InlineData("1.0.0", true)]
    [InlineData("1.0.0-alpha.1+build.2", true)]
    [InlineData("01.0.0", false)]
    [InlineData("1.0.0-01", false)]
    [InlineData("1.0", false)]
    [InlineData("1.0.0\n", false)]
    [InlineData("1.0.0\r", false)]
    [InlineData("1.0.0\u0000", false)]
    public void SchemaAndRuntime_SemVerGrammarHaveParity(string version, bool expected)
    {
        var schemaPattern = GetSchemaPattern("package", "version");

        Assert.Equal(expected, new Regex(schemaPattern).IsMatch(version));
        Assert.Equal(expected, SemanticVersion.TryParse(version, out _));
        Assert.Equal(expected, BlueprintPackageValidator.Validate(CreateDefinitionSource(
            "blueprint",
            "engineering",
            "definitions/blueprints/engineering.json",
            packageVersion: version)).IsValid);
    }

    [Theory]
    [InlineData("blueprint", "definitions/blueprints/engineering.json", true)]
    [InlineData("role", "definitions/roles/engineering.json", true)]
    [InlineData("workflow", "definitions/workflows/engineering.yaml", true)]
    [InlineData("skill", "definitions/skills/engineering.md", true)]
    [InlineData("blueprint", "definitions/blueprints/engineering.yaml", false)]
    [InlineData("workflow", "definitions/roles/engineering.json", false)]
    [InlineData("blueprint", "definitions/blueprints/engineering.json\n", false)]
    [InlineData("blueprint", "definitions/blueprints/engineering.json\r", false)]
    [InlineData("blueprint", "definitions/blueprints/engineering.json\u0000", false)]
    public void SchemaAndRuntime_KindDependentPathGrammarHaveParity(string kind, string path, bool expected)
    {
        var schemaPattern = GetDefinitionPathPattern(kind);

        Assert.Equal(expected, new Regex(schemaPattern).IsMatch(path));
        Assert.Equal(expected, BlueprintPackageValidator.Validate(CreateDefinitionSource(kind, "engineering", path)).IsValid);
    }

    [Theory]
    [InlineData("https://github.com/example/blueprints", "2026-07-16T10:54:27Z", true)]
    [InlineData("https://github.com/example/%2Fblueprints", "9999-12-31T23:59:59.99999999Z", true)]
    [InlineData("https://[2001:db8::1]:65535/blueprints", "2024-02-29T10:54:27-14:00", true)]
    [InlineData("https://[2001:db8::1]:65535/a%2Fb?ref=release%2Fv1#readme%20copy", "2026-07-16T10:54:27Z", true)]
    [InlineData("http://github.com/example/blueprints", "2026-07-16T10:54:27Z", false)]
    [InlineData("https://github.com/example/blueprints", "2026-07-16", false)]
    [InlineData("https://github.com/example/%ZZ", "2026-07-16T10:54:27Z", false)]
    [InlineData("https://github.com/example?ref=%ZZ", "2026-07-16T10:54:27Z", false)]
    [InlineData("https://github.com/example#readme%ZZ", "2026-07-16T10:54:27Z", false)]
    [InlineData("https://user@github.com/example", "2026-07-16T10:54:27Z", false)]
    [InlineData("https://github..com/example", "2026-07-16T10:54:27Z", false)]
    [InlineData("https://github.com:65536/example", "2026-07-16T10:54:27Z", false)]
    [InlineData("https://example.com/a\\b", "2026-07-16T10:54:27Z", false)]
    [InlineData("https://example.com/a[b]", "2026-07-16T10:54:27Z", false)]
    [InlineData("https://example.com/a#b#c", "2026-07-16T10:54:27Z", false)]
    [InlineData("https://example.com/a?one?two", "2026-07-16T10:54:27Z", false)]
    [InlineData("https://example.com/a#fragment?query", "2026-07-16T10:54:27Z", false)]
    [InlineData("https://[2001:db8::1/example", "2026-07-16T10:54:27Z", false)]
    [InlineData("https://[2001:db8::1]]/example", "2026-07-16T10:54:27Z", false)]
    [InlineData("https://[not-an-ipv6]/example", "2026-07-16T10:54:27Z", false)]
    [InlineData("******github.com/example", "2026-07-16T10:54:27Z", false)]
    [InlineData("https://github.com/example\n", "2026-07-16T10:54:27Z", false)]
    [InlineData("https://github.com/example\r", "2026-07-16T10:54:27Z", false)]
    [InlineData("https://github.com/example\u0000", "2026-07-16T10:54:27Z", false)]
    [InlineData("https://github.com/example", "2026-07-16T10:54:27Z\n", false)]
    [InlineData("https://github.com/example", "2026-07-16T10:54:27Z\r", false)]
    [InlineData("https://github.com/example", "2026-07-16T10:54:27Z\u0000", false)]
    [InlineData("https://github.com/example", "2026-07-16T10:54:27+14:00", true)]
    [InlineData("https://github.com/example", "2026-07-16T10:54:27+14:01", false)]
    [InlineData("https://github.com/example", "2026-07-16T10:54:60Z", false)]
    [InlineData("https://github.com/example", "2026-02-29T10:54:27Z", false)]
    [InlineData("https://github.com/example", "0000-01-01T00:00:00Z", false)]
    public void SchemaRuntimeAndCustomKeywords_ProvenanceGrammarHaveParity(string repository, string createdAt, bool expected)
    {
        using var schema = JsonDocument.Parse(BlueprintPackageSchema.Json);
        using var metaSchema = JsonDocument.Parse(BlueprintPackageSchema.MetaSchemaJson);
        var provenance = schema.RootElement.GetProperty("properties").GetProperty("provenance").GetProperty("properties");
        Assert.True(provenance.GetProperty("repository").GetProperty(BlueprintPackageSchema.HttpsRepositoryUriKeyword).GetBoolean());
        Assert.Equal(
            "Absolute HTTPS repository URI using the package lexical RFC 3986 profile.",
            provenance.GetProperty("repository").GetProperty("description").GetString());
        Assert.Equal(
            "Requires the Blueprint Package v1 lexical HTTPS repository URI profile.",
            metaSchema.RootElement.GetProperty("allOf")[1].GetProperty("properties")
                .GetProperty(BlueprintPackageSchema.HttpsRepositoryUriKeyword).GetProperty("description").GetString());
        Assert.True(provenance.GetProperty("created_at").GetProperty(BlueprintPackageSchema.Rfc3339TimestampKeyword).GetBoolean());

        var source = CreateDefinitionSource(
            "blueprint",
            "engineering",
            "definitions/blueprints/engineering.json",
            repository: repository,
            createdAt: createdAt);
        using var manifest = JsonDocument.Parse(source.RawManifest.ToArray());
        var customKeywordErrors = new List<string>();
        BlueprintPackageSchema.ValidateCustomKeywords(manifest.RootElement, customKeywordErrors);
        var result = BlueprintPackageValidator.Validate(source);

        Assert.Equal(
            expected,
            BlueprintPackageSchema.IsHttpsRepositoryUri(repository) && BlueprintPackageSchema.IsRfc3339Timestamp(createdAt));
        Assert.Equal(!expected, customKeywordErrors.Any());
        Assert.Equal(expected, result.IsValid);
        if (expected)
            Assert.Equal(createdAt, result.Package!.Manifest.Provenance!.CreatedAt);
    }

    [Fact]
    public void SchemaCustomPathAssertion_EnforcesKindAndIdEqualityThroughProjectValidator()
    {
        const string path = "definitions/blueprints/other.json";
        using var manifest = JsonDocument.Parse("""
            {"schema_version":"1","package":{"id":"engineering","version":"1.0.0"},"definitions":[{"kind":"blueprint","id":"engineering","path":"definitions/blueprints/other.json","size":2,"sha256":"44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a"}]}
            """);
        var schemaErrors = new List<string>();

        Assert.True(
            new Regex(GetDefinitionPathPattern("blueprint")).IsMatch(path),
            "standard schema patterns cannot compare sibling values");
        BlueprintPackageSchema.ValidateCustomKeywords(manifest.RootElement, schemaErrors);
        Assert.Equal(["definition.path is not a canonical definitions-only path."], schemaErrors.ToArray());
        Assert.Contains(
            "definition.path is not a canonical definitions-only path.",
            BlueprintPackageValidator.Validate(CreateDefinitionSource("blueprint", "engineering", path)).Errors.ToArray());
    }

    [Theory]
    [InlineData("engineering\n")]
    [InlineData("engineering\r")]
    [InlineData("engineering\u0000")]
    public void SchemaAndRuntime_RejectIdentifiersWithTrailingInput(string id)
    {
        var schemaPattern = GetSchemaPattern("package", "id");
        var payload = Encoding.UTF8.GetBytes("{}");
        var manifest = $$"""
            {"schema_version":"1","package":{"id":{{JsonSerializer.Serialize(id)}},"version":"1.0.0"},"definitions":[{"kind":"blueprint","id":"engineering","path":"definitions/blueprints/engineering.json","size":2,"sha256":"{{BlueprintPackageHash.Sha256(payload)}}" }]}
            """;

        Assert.DoesNotMatch(new Regex(schemaPattern), id);
        Assert.False(BlueprintPackageValidator.Validate(new BlueprintPackageSource(
            Encoding.UTF8.GetBytes(manifest),
            [new KeyValuePair<string, byte[]>("definitions/blueprints/engineering.json", payload)])).IsValid);
    }

    [Theory]
    [InlineData(64, true)]
    [InlineData(65, false)]
    public void Validate_EnforcesDocumentedIdentifierLength(int length, bool expected)
    {
        var id = "a" + new string('b', length - 1);
        var path = $"definitions/blueprints/{id}.json";

        Assert.Equal(expected, BlueprintPackageValidator.Validate(CreateDefinitionSource("blueprint", id, path)).IsValid);
    }

    [Fact]
    public void PackageSnapshots_DoNotExposeMutableBytesOrCollections()
    {
        var manifest = Encoding.UTF8.GetBytes("""{"schema_version":"1","package":{"id":"engineering","version":"1.0.0-alpha.1+build"},"definitions":[{"kind":"blueprint","id":"engineering","path":"definitions/blueprints/engineering.json","size":2,"sha256":"44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a"}]}""");
        var payload = Encoding.UTF8.GetBytes("{}");
        var source = new BlueprintPackageSource(manifest, [new KeyValuePair<string, byte[]>("definitions/blueprints/engineering.json", payload)]);
        manifest[0] = (byte)'[';
        payload[0] = (byte)'[';

        var result = BlueprintPackageValidator.Validate(source);
        var payloadDigest = BlueprintPackageValidator.CalculatePayloadSetDigest(source.Payloads);
        var manifestCopy = source.RawManifest.ToArray();
        var payloadCopy = source.Payloads["definitions/blueprints/engineering.json"].ToArray();
        manifestCopy[0] = (byte)'[';
        payloadCopy[0] = (byte)'[';

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Equal(payloadDigest, result.Package!.Digests.PayloadSetSha256);
        Assert.Equal((byte)'{', source.RawManifest[0]);
        Assert.Equal((byte)'{', source.Payloads["definitions/blueprints/engineering.json"][0]);
        Assert.Equal(typeof(ImmutableArray<BlueprintPackageDefinition>), result.Package.Manifest.Definitions.GetType());
        Assert.Equal(typeof(ImmutableArray<string>), result.Package.Manifest.Version.PreRelease.GetType());
        Assert.Equal(typeof(ImmutableDictionary<string, ImmutableArray<byte>>), source.Payloads.GetType());
        Assert.Equal(typeof(ImmutableArray<string>), result.Errors.GetType());
    }

    [Fact]
    public void Schema_IsStrictAndMirrorsManifestGrammar()
    {
        using var schema = JsonDocument.Parse(BlueprintPackageSchema.Json);
        using var metaSchema = JsonDocument.Parse(BlueprintPackageSchema.MetaSchemaJson);
        Assert.Equal(BlueprintPackageSchema.Id, schema.RootElement.GetProperty("$id").GetString());
        Assert.Equal(BlueprintPackageSchema.MetaSchemaId, schema.RootElement.GetProperty("$schema").GetString());
        Assert.Equal(BlueprintPackageSchema.MetaSchemaId, metaSchema.RootElement.GetProperty("$id").GetString());
        Assert.True(metaSchema.RootElement.GetProperty("$vocabulary").GetProperty(BlueprintPackageSchema.VocabularyId).GetBoolean());
        Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            BlueprintPackageLimits.MaximumDefinitions,
            schema.RootElement.GetProperty("properties").GetProperty("definitions").GetProperty("maxItems").GetInt32());
        Assert.False(
            schema.RootElement.GetProperty("properties").GetProperty("provenance").GetProperty("additionalProperties").GetBoolean());
        Assert.True(
            schema.RootElement.GetProperty("properties").GetProperty("definitions").GetProperty("items")
                .GetProperty(BlueprintPackageSchema.CanonicalDefinitionPathKeyword).GetBoolean());
        var provenance = schema.RootElement.GetProperty("properties").GetProperty("provenance").GetProperty("properties");
        Assert.True(provenance.GetProperty("repository").GetProperty(BlueprintPackageSchema.HttpsRepositoryUriKeyword).GetBoolean());
        Assert.True(provenance.GetProperty("created_at").GetProperty(BlueprintPackageSchema.Rfc3339TimestampKeyword).GetBoolean());
    }

    [Theory]
    [InlineData(0xD800)]
    [InlineData(0xDC00)]
    public void CalculatePayloadSetDigest_RejectsUnpairedSurrogatePaths(int invalidCodeUnit)
    {
        var invalidPath = new string((char)invalidCodeUnit, 1);
        var payloads = new Dictionary<string, ImmutableArray<byte>>
        {
            [invalidPath] = ImmutableArray.Create((byte)1),
        };

        var exception = Assert.ThrowsAny<ArgumentException>(() => BlueprintPackageValidator.CalculatePayloadSetDigest(payloads));

        Assert.StartsWith("Payload paths must be valid Unicode.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsCompatibilityConflictAndContainerDigestMismatch()
    {
        var payload = Encoding.UTF8.GetBytes("{}");
        var hash = BlueprintPackageHash.Sha256(payload);
        var manifest = $$"""
            {"schema_version":"1","package":{"id":"engineering","version":"1.0.0"},"compatibility":{"minimum_agentweaver_version":"999999999999999999999999.0.0","maximum_agentweaver_version":"1.0.0"},"container_sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","definitions":[{"kind":"blueprint","id":"engineering","path":"definitions/blueprints/engineering.json","size":{{payload.Length}},"sha256":"{{hash}}"}]}
            """;
        var source = new BlueprintPackageSource(
            Encoding.UTF8.GetBytes(manifest),
            [new KeyValuePair<string, byte[]>("definitions/blueprints/engineering.json", payload)],
            new string('b', 64));

        var result = BlueprintPackageValidator.Validate(source);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("minimum cannot exceed", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("container SHA-256 conflicts", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsVersionBeyondPersistenceLimit()
    {
        var version = new string('9', BlueprintPackageLimits.MaximumVersionLength - 3) + ".0.0";
        var source = CreateDefinitionSource(
            "blueprint",
            "engineering",
            "definitions/blueprints/engineering.json",
            packageVersion: version);

        var result = BlueprintPackageValidator.Validate(source);

        Assert.False(result.IsValid);
        Assert.Contains(
            $"package.version exceeds the maximum length of {BlueprintPackageLimits.MaximumVersionLength} characters.",
            result.Errors.ToArray());
        Assert.False(SemanticVersion.TryParse(version, out _));
    }

    private static BlueprintPackageSource CreateSource(params (string Path, byte[] Bytes)[] payloads)
    {
        var entries = payloads.Where(pair => pair.Path != "manifest.json").Select(pair =>
        {
            var (kind, id) = pair.Path.Split('/') switch
            {
                ["definitions", "blueprints", var name] => ("blueprint", Path.GetFileNameWithoutExtension(name)),
                ["definitions", "workflows", var name] => ("workflow", Path.GetFileNameWithoutExtension(name)),
                ["definitions", "roles", var name] => ("role", Path.GetFileNameWithoutExtension(name)),
                ["definitions", "skills", var name] => ("skill", Path.GetFileNameWithoutExtension(name)),
                _ => throw new ArgumentException($"Unsupported test payload path: {pair.Path}"),
            };
            return $$"""{"kind":"{{kind}}","id":"{{id}}","path":"{{pair.Path}}","size":{{pair.Bytes.Length}},"sha256":"{{BlueprintPackageHash.Sha256(pair.Bytes)}}" }""";
        });
        var manifest = $$"""{"schema_version":"1","package":{"id":"engineering","version":"1.0.0"},"definitions":[{{string.Join(",", entries)}}]}""";
        return new BlueprintPackageSource(Encoding.UTF8.GetBytes(manifest), payloads.Select(pair => new KeyValuePair<string, byte[]>(pair.Path, pair.Bytes)));
    }

    private static BlueprintPackageSource CreateDefinitionSource(
        string kind,
        string id,
        string path,
        byte[]? payload = null,
        string packageVersion = "1.0.0",
        string? repository = null,
        string? createdAt = null,
        long? declaredSize = null,
        string? declaredHash = null)
    {
        payload ??= kind is "blueprint" or "role" ? Encoding.UTF8.GetBytes("{}") : Encoding.UTF8.GetBytes("content\n");
        var provenanceFields = new List<string> { "\"source\":\"catalog\"" };
        if (repository is not null) provenanceFields.Add($"\"repository\":{JsonSerializer.Serialize(repository)}");
        if (createdAt is not null) provenanceFields.Add($"\"created_at\":{JsonSerializer.Serialize(createdAt)}");
        var provenance = provenanceFields.Count == 1 ? string.Empty : ",\"provenance\":{" + string.Join(",", provenanceFields) + "}";
        var size = declaredSize ?? payload.Length;
        var hash = declaredHash ?? BlueprintPackageHash.Sha256(payload);
        var manifest = $$"""{"schema_version":"1","package":{"id":"engineering","version":"{{packageVersion}}"}{{provenance}},"definitions":[{"kind":"{{kind}}","id":"{{id}}","path":"{{path}}","size":{{size}},"sha256":"{{hash}}"}]}""";
        return new BlueprintPackageSource(
            Encoding.UTF8.GetBytes(manifest),
            [new KeyValuePair<string, byte[]>(path, payload)]);
    }

    private static string GetSchemaPattern(string objectName, string propertyName)
    {
        using var schema = JsonDocument.Parse(BlueprintPackageSchema.Json);
        return schema.RootElement.GetProperty("properties").GetProperty(objectName).GetProperty("properties")
            .GetProperty(propertyName).GetProperty("pattern").GetString()!;
    }

    private static string GetDefinitionPathPattern(string kind)
    {
        using var schema = JsonDocument.Parse(BlueprintPackageSchema.Json);
        foreach (var rule in schema.RootElement.GetProperty("properties").GetProperty("definitions").GetProperty("items").GetProperty("allOf").EnumerateArray())
        {
            if (rule.GetProperty("if").GetProperty("properties").GetProperty("kind").GetProperty("const").GetString() == kind)
                return rule.GetProperty("then").GetProperty("properties").GetProperty("path").GetProperty("pattern").GetString()!;
        }
        throw new ArgumentOutOfRangeException(nameof(kind));
    }
}
