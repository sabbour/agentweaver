using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agentweaver.Squad.BlueprintPackages;
using FluentAssertions;

namespace Agentweaver.Tests.Blueprints;

public sealed class BlueprintPackageContractTests
{
    [Fact]
    public void Validate_GoldenDefinitionsOnlyPackage_ProducesAllContractDigests()
    {
        var source = CreateSource(
            ("definitions/blueprints/engineering.json", Encoding.UTF8.GetBytes("""{"name":"Engineering","weight":1.0}""")),
            ("definitions/workflows/delivery.yaml", Encoding.UTF8.GetBytes("name: delivery\r\nsteps:\r\n  - build\r\n")));

        var result = BlueprintPackageValidator.Validate(source);

        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors));
        result.Package!.Manifest.Definitions.Should().HaveCount(2);
        result.Package.Digests.RawManifestSha256.Should().Be(BlueprintPackageHash.Sha256(source.RawManifest.AsSpan()));
        result.Package.Digests.SemanticSha256.Should().MatchRegex("^[0-9a-f]{64}$");
        result.Package.Digests.PayloadSetSha256.Should().Be("bbeb4578c069da4b053e50fe4bfa79f4fb52d7c935521201e0c4a314167dfdd3");
        BlueprintPackageValidator.CalculatePayloadSetDigest(source.Payloads).Should().Be(result.Package.Digests.PayloadSetSha256);
    }

    [Fact]
    public void Validate_SemanticDigest_IsStableAcrossJsonFormattingAndExactNumberSpellings()
    {
        var first = CreateSource(("definitions/blueprints/engineering.json", Encoding.UTF8.GetBytes("""{"weight":1.0,"name":"Engineering"}""")));
        var second = CreateSource(("definitions/blueprints/engineering.json", Encoding.UTF8.GetBytes("""{ "name": "Engineering", "weight": 1e0 }""")));

        var firstResult = BlueprintPackageValidator.Validate(first);
        var secondResult = BlueprintPackageValidator.Validate(second);

        firstResult.IsValid.Should().BeTrue();
        secondResult.IsValid.Should().BeTrue();
        firstResult.Package!.Digests.SemanticSha256.Should().Be(secondResult.Package!.Digests.SemanticSha256);
        firstResult.Package.Digests.RawManifestSha256.Should().NotBe(secondResult.Package.Digests.RawManifestSha256);
    }

    [Fact]
    public void Validate_RejectsManifestInInventoryAndDefinitionsOnlyEscapes()
    {
        var source = CreateSource(
            ("definitions/blueprints/engineering.json", Encoding.UTF8.GetBytes("{}")),
            ("manifest.json", Encoding.UTF8.GetBytes("{}")));

        var result = BlueprintPackageValidator.Validate(source);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.Contains("manifest.json is not a payload", StringComparison.Ordinal));
        result.Errors.Should().Contain(error => error.Contains("not listed in the inventory", StringComparison.Ordinal));
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

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.Contains("canonical definitions-only path", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsMalformedUnicodeAndNeverRoundsJsonNumbers()
    {
        var malformed = Encoding.UTF8.GetBytes("""{"schema_version":"1","package":{"id":"bad\uD800","version":"1.0.0"},"definitions":[]}""");

        var result = BlueprintPackageValidator.Validate(new BlueprintPackageSource(malformed, []));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.Contains("Unicode", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsNonAdjacentLowSurrogateWithStableError()
    {
        var malformed = Encoding.UTF8.GetBytes("""{"schema_version":"1","package":{"id":"bad\uD800\n\uDC00","version":"1.0.0"},"definitions":[]}""");

        var result = BlueprintPackageValidator.Validate(new BlueprintPackageSource(malformed, []));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Be("manifest is not strict JSON: unpaired Unicode high surrogate.");
    }

    [Fact]
    public void Validate_RejectsDuplicateJsonPropertyNames()
    {
        var source = CreateSource(("definitions/blueprints/engineering.json", Encoding.UTF8.GetBytes("""{"name":"first","name":"second"}""")));

        var result = BlueprintPackageValidator.Validate(source);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.Contains("duplicate JSON property", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_PayloadSetDigestChangesWhenRawPayloadBytesChange()
    {
        var first = CreateSource(("definitions/blueprints/engineering.json", Encoding.UTF8.GetBytes("""{"weight":1}""")));
        var second = CreateSource(("definitions/blueprints/engineering.json", Encoding.UTF8.GetBytes("""{"weight":2}""")));

        var firstResult = BlueprintPackageValidator.Validate(first);
        var secondResult = BlueprintPackageValidator.Validate(second);

        firstResult.IsValid.Should().BeTrue();
        secondResult.IsValid.Should().BeTrue();
        firstResult.Package!.Digests.PayloadSetSha256.Should().NotBe(secondResult.Package!.Digests.PayloadSetSha256);
    }

    [Fact]
    public void Validate_RejectsOversizeManifestBeforeParsing()
    {
        var source = new BlueprintPackageSource(new byte[BlueprintPackageLimits.MaximumManifestBytes + 1], []);

        var result = BlueprintPackageValidator.Validate(source);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Equal("manifest exceeds the maximum byte length.");
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

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain("payload exceeds the byte limit: definitions/blueprints/engineering.json");
        result.Errors.Should().NotContain(error => error.Contains("inventory SHA-256", StringComparison.Ordinal));
        result.Errors.Should().NotContain(error => error.Contains("JSON payload is invalid", StringComparison.Ordinal));
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

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Equal("payload set exceeds the total byte limit.");
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
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(
            $"JSON payload is invalid (definitions/blueprints/engineering.json): JSON number token exceeds the maximum length of {BlueprintPackageLimits.MaximumCanonicalNumberTokenLength} characters.");
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Validate_AcceptsCanonicalNumberAtTokenLimit()
    {
        var literal = "1e" + new string('7', BlueprintPackageLimits.MaximumCanonicalNumberTokenLength - 2);
        var source = CreateSource(("definitions/blueprints/engineering.json", Encoding.UTF8.GetBytes($$"""{"weight":{{literal}}}""")));

        var result = BlueprintPackageValidator.Validate(source);

        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors));
    }

    [Fact]
    public void SemanticVersion_UsesUnboundedNumericComparison()
    {
        var lower = SemanticVersion.Parse("999999999999999999999999999999999999.1.0-alpha.9");
        var higher = SemanticVersion.Parse("1000000000000000000000000000000000000.0.0-alpha.10");

        lower.CompareTo(higher).Should().BeLessThan(0);
        SemanticVersion.Parse("1.0.0-alpha.10").CompareTo(SemanticVersion.Parse("1.0.0-alpha.2")).Should().BeGreaterThan(0);
        SemanticVersion.TryParse("1.0.0-01", out _).Should().BeFalse();
    }

    [Fact]
    public void SemanticVersion_AcceptsLongVersionsAndHashesEqualBuildMetadataVariantsEqually()
    {
        var longVersion = new string('9', 600) + ".0.0";
        var first = SemanticVersion.Parse("1.2.3-alpha.1+build-a");
        var second = SemanticVersion.Parse("1.2.3-alpha.1+build-b");

        SemanticVersion.TryParse(longVersion, out var parsed).Should().BeTrue();
        parsed!.ToString().Should().Be(longVersion);
        first.Should().Be(second);
        first.GetHashCode().Should().Be(second.GetHashCode());
        BlueprintPackageValidator.Validate(CreateDefinitionSource(
            "blueprint",
            "engineering",
            "definitions/blueprints/engineering.json",
            packageVersion: longVersion)).IsValid.Should().BeTrue();
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

        new Regex(schemaPattern).IsMatch(version).Should().Be(expected);
        SemanticVersion.TryParse(version, out _).Should().Be(expected);
        BlueprintPackageValidator.Validate(CreateDefinitionSource(
            "blueprint",
            "engineering",
            "definitions/blueprints/engineering.json",
            packageVersion: version)).IsValid.Should().Be(expected);
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

        new Regex(schemaPattern).IsMatch(path).Should().Be(expected);
        BlueprintPackageValidator.Validate(CreateDefinitionSource(kind, "engineering", path)).IsValid.Should().Be(expected);
    }

    [Theory]
    [InlineData("https://github.com/example/blueprints", "2026-07-16T10:54:27Z", true)]
    [InlineData("http://github.com/example/blueprints", "2026-07-16T10:54:27Z", false)]
    [InlineData("https://github.com/example/blueprints", "2026-07-16", false)]
    [InlineData("https://github.com/example/%ZZ", "2026-07-16T10:54:27Z", false)]
    [InlineData("https://user:password@github.com/example", "2026-07-16T10:54:27Z", false)]
    [InlineData("https://github.com/example\n", "2026-07-16T10:54:27Z", false)]
    [InlineData("https://github.com/example\r", "2026-07-16T10:54:27Z", false)]
    [InlineData("https://github.com/example\u0000", "2026-07-16T10:54:27Z", false)]
    [InlineData("https://github.com/example", "2026-07-16T10:54:27Z\n", false)]
    [InlineData("https://github.com/example", "2026-07-16T10:54:27Z\r", false)]
    [InlineData("https://github.com/example", "2026-07-16T10:54:27Z\u0000", false)]
    [InlineData("https://github.com/example", "2026-07-16T10:54:27+14:00", true)]
    [InlineData("https://github.com/example", "2026-07-16T10:54:27+14:01", false)]
    [InlineData("https://github.com/example", "2026-07-16T10:54:60Z", false)]
    public void SchemaAndRuntime_ProvenanceGrammarHaveParity(string repository, string createdAt, bool expected)
    {
        using var schema = JsonDocument.Parse(BlueprintPackageSchema.Json);
        var provenance = schema.RootElement.GetProperty("properties").GetProperty("provenance").GetProperty("properties");
        var repositoryPattern = provenance.GetProperty("repository").GetProperty("pattern").GetString()!;
        var createdAtPattern = provenance.GetProperty("created_at").GetProperty("pattern").GetString()!;

        (new Regex(repositoryPattern).IsMatch(repository) && new Regex(createdAtPattern).IsMatch(createdAt)).Should().Be(expected);
        BlueprintPackageValidator.Validate(CreateDefinitionSource(
            "blueprint",
            "engineering",
            "definitions/blueprints/engineering.json",
            repository: repository,
            createdAt: createdAt)).IsValid.Should().Be(expected);
    }

    [Fact]
    public void SchemaCustomPathAssertion_EnforcesKindAndIdEqualityThroughProjectValidator()
    {
        const string path = "definitions/blueprints/other.json";
        using var manifest = JsonDocument.Parse("""
            {"schema_version":"1","package":{"id":"engineering","version":"1.0.0"},"definitions":[{"kind":"blueprint","id":"engineering","path":"definitions/blueprints/other.json","size":2,"sha256":"44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a"}]}
            """);
        var schemaErrors = new List<string>();

        new Regex(GetDefinitionPathPattern("blueprint")).IsMatch(path).Should().BeTrue("standard schema patterns cannot compare sibling values");
        BlueprintPackageSchema.ValidateCustomKeywords(manifest.RootElement, schemaErrors);
        schemaErrors.Should().Equal("definition.path is not a canonical definitions-only path.");
        BlueprintPackageValidator.Validate(CreateDefinitionSource("blueprint", "engineering", path))
            .Errors.Should().Contain("definition.path is not a canonical definitions-only path.");
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

        new Regex(schemaPattern).IsMatch(id).Should().BeFalse();
        BlueprintPackageValidator.Validate(new BlueprintPackageSource(
            Encoding.UTF8.GetBytes(manifest),
            [new KeyValuePair<string, byte[]>("definitions/blueprints/engineering.json", payload)])).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(64, true)]
    [InlineData(65, false)]
    public void Validate_EnforcesDocumentedIdentifierLength(int length, bool expected)
    {
        var id = "a" + new string('b', length - 1);
        var path = $"definitions/blueprints/{id}.json";

        BlueprintPackageValidator.Validate(CreateDefinitionSource("blueprint", id, path)).IsValid.Should().Be(expected);
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

        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors));
        result.Package!.Digests.PayloadSetSha256.Should().Be(payloadDigest);
        source.RawManifest[0].Should().Be((byte)'{');
        source.Payloads["definitions/blueprints/engineering.json"][0].Should().Be((byte)'{');
        result.Package.Manifest.Definitions.GetType().Should().Be(typeof(ImmutableArray<BlueprintPackageDefinition>));
        result.Package.Manifest.Version.PreRelease.GetType().Should().Be(typeof(ImmutableArray<string>));
        source.Payloads.GetType().Should().Be(typeof(ImmutableDictionary<string, ImmutableArray<byte>>));
        result.Errors.GetType().Should().Be(typeof(ImmutableArray<string>));
    }

    [Fact]
    public void Schema_IsStrictAndMirrorsManifestGrammar()
    {
        using var schema = JsonDocument.Parse(BlueprintPackageSchema.Json);
        schema.RootElement.GetProperty("$id").GetString().Should().Be(BlueprintPackageSchema.Id);
        schema.RootElement.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        schema.RootElement.GetProperty("properties").GetProperty("definitions").GetProperty("maxItems").GetInt32()
            .Should().Be(BlueprintPackageLimits.MaximumDefinitions);
        schema.RootElement.GetProperty("properties").GetProperty("provenance").GetProperty("additionalProperties").GetBoolean()
            .Should().BeFalse();
        schema.RootElement.GetProperty("x-agentweaver-canonical-number-token-max-length").GetInt32()
            .Should().Be(BlueprintPackageLimits.MaximumCanonicalNumberTokenLength);
        schema.RootElement.GetProperty("x-agentweaver-vocabulary").GetProperty("id").GetString()
            .Should().Be(BlueprintPackageSchema.VocabularyId);
        schema.RootElement.GetProperty("properties").GetProperty("definitions").GetProperty("items")
            .GetProperty(BlueprintPackageSchema.CanonicalDefinitionPathKeyword).GetBoolean().Should().BeTrue();
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

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.Contains("minimum cannot exceed", StringComparison.Ordinal));
        result.Errors.Should().Contain(error => error.Contains("container SHA-256 conflicts", StringComparison.Ordinal));
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
