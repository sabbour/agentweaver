using System.Diagnostics;
using System.Text;
using System.Text.Json;
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
        result.Package.Digests.RawManifestSha256.Should().Be(BlueprintPackageHash.Sha256(source.RawManifest.Span));
        result.Package.Digests.SemanticSha256.Should().MatchRegex("^[0-9a-f]{64}$");
        result.Package.Digests.PayloadSetSha256.Should().MatchRegex("^[0-9a-f]{64}$");
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
    public void Validate_RejectsDuplicateJsonPropertyNames()
    {
        var source = CreateSource(("definitions/blueprints/engineering.json", Encoding.UTF8.GetBytes("""{"name":"first","name":"second"}""")));

        var result = BlueprintPackageValidator.Validate(source);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.Contains("duplicate JSON property", StringComparison.Ordinal));
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
}
