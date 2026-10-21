using System.Collections.Immutable;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed class AgentSuiteBlueprintConverterTests
{
    /// <summary>Set to <c>1</c> to rewrite the golden archives under <c>Fixtures/AgentSuiteBlueprints</c>.</summary>
    private const string UpdateVariable = "AGENTWEAVER_UPDATE_BLUEPRINT_SUITES";

    /// <summary>
    /// Test-only build inputs. The registry host is reserved (<c>.invalid</c>) and the digests hash fixed labels, so
    /// these images can't be pulled. Shipped packages need real AgentHost images (#1919).
    /// </summary>
    private static AgentSuiteBuildInputs TestOnlyInputs => new()
    {
        BuildProfileId = "test-only",
        SourceEpoch = 1,
        RuntimeBase = [TestOnlyImage("amd64", "runtime-base"), TestOnlyImage("arm64", "runtime-base")],
        Harness = [TestOnlyImage("amd64", "harness"), TestOnlyImage("arm64", "harness")],
        Model = new("openai-compatible", "test-only-model"),
    };

    private static AgentSuiteImageInput TestOnlyImage(string architecture, string name) => new(
        architecture,
        $"registry.invalid/agentweaver/test-only/{name}@sha256:" +
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"test-only {name} {architecture}"))),
        1);

    public static TheoryData<string> BuiltInIds()
    {
        var data = new TheoryData<string>();
        foreach (var blueprint in ProjectBlueprintCatalog.Blueprints)
            data.Add(blueprint.Id);
        return data;
    }

    [Theory]
    [MemberData(nameof(BuiltInIds))]
    public void BuiltInConvertsToOneAgentPerRoleWithThe0xCharterAndProjection(string blueprintId)
    {
        var blueprint = ProjectBlueprintCatalog.Blueprints.Single(item => item.Id == blueprintId);

        var package = AgentSuiteBlueprintConverter.Convert(blueprint, TestOnlyInputs);

        Assert.Equal(blueprint.Id, package.SuiteName);
        using var suite = JsonDocument.Parse(File(package, "agentsuite.json"));
        Assert.Equal(blueprint.Id, suite.RootElement.GetProperty("name").GetString());
        Assert.Equal(
            blueprint.Roster,
            suite.RootElement.GetProperty("agents").EnumerateArray().Select(agent => agent.GetProperty("id").GetString()));
        Assert.Equal(
            blueprint.Roster.Length * 2,
            suite.RootElement.GetProperty("compositions").GetArrayLength());
        foreach (var roleId in blueprint.Roster)
        {
            var (title, charter) = ProjectBlueprintCatalog.ResolveRole(blueprint, roleId);
            Assert.Equal(charter, Encoding.UTF8.GetString(File(package, $"instructions/{roleId}.md")));
            using var agent = JsonDocument.Parse(File(package, $"agents/{roleId}.json"));
            Assert.Equal(title, agent.RootElement.GetProperty("description").GetString());
            Assert.Equal("test-only-model", agent.RootElement.GetProperty("model").GetProperty("model").GetString());
        }

        var projection = package.Blueprint;
        Assert.Equal(blueprint.Id, projection.SourceBlueprintId);
        Assert.Equal(blueprint.Name, projection.DisplayName);
        Assert.Equal(blueprint.Workflows, projection.Workflows);
        Assert.Equal(blueprint.Workflows[0], projection.DefaultWorkflow);
        Assert.Equal(blueprint.ReviewPolicy, projection.ReviewPolicy);
        Assert.Equal(blueprint.SandboxProfile, projection.SandboxProfile);
        Assert.Equal(
            blueprint.SkillBindings.Select(binding => (binding.RoleId, string.Join(',', binding.Skills))),
            projection.SkillBindings.Select(binding => (binding.Role, string.Join(',', binding.Skills))));
    }

    [Theory]
    [MemberData(nameof(BuiltInIds))]
    public void BuiltInPackageFilesAreCanonicalAndMatchTheirDescriptors(string blueprintId)
    {
        var package = AgentSuiteBlueprintConverter.Convert(
            ProjectBlueprintCatalog.Blueprints.Single(item => item.Id == blueprintId),
            TestOnlyInputs);

        foreach (var file in package.Files.Where(file => file.Path.EndsWith(".json", StringComparison.Ordinal)))
            Assert.True(CanonicalJson.IsCanonical(file.Content.AsSpan()), file.Path);

        using var suite = JsonDocument.Parse(File(package, "agentsuite.json"));
        var references = suite.RootElement.GetProperty("agents").EnumerateArray()
            .Concat(suite.RootElement.GetProperty("compositions").EnumerateArray())
            .Concat(suite.RootElement.GetProperty("buildProfiles").EnumerateArray())
            .Append(suite.RootElement.GetProperty("toolProviderCatalog"));
        foreach (var reference in references)
            AssertDescriptor(package, reference);
        foreach (var roleId in suite.RootElement.GetProperty("agents").EnumerateArray().Select(item => item.GetProperty("id").GetString()!))
        {
            using var agent = JsonDocument.Parse(File(package, $"agents/{roleId}.json"));
            AssertDescriptor(package, agent.RootElement.GetProperty("instructions"));
        }
    }

    [Theory]
    [MemberData(nameof(BuiltInIds))]
    public void BuiltInArchiveMatchesTheGoldenFixture(string blueprintId)
    {
        var package = AgentSuiteBlueprintConverter.Convert(
            ProjectBlueprintCatalog.Blueprints.Single(item => item.Id == blueprintId),
            TestOnlyInputs);
        var fixture = Path.Combine(SourceDirectory(), "Fixtures", "AgentSuiteBlueprints", $"{blueprintId}.tar.gz");

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fixture)!);
            System.IO.File.WriteAllBytes(fixture, package.ContentArchive.ToArray());
        }

        // Compression bytes can differ between zlib builds, so compare the tar stream.
        Assert.Equal(Decompress(System.IO.File.ReadAllBytes(fixture)), Decompress(package.ContentArchive.ToArray()));
    }

    [Fact]
    public void ArchiveFollowsTheAgentSuiteContentRules()
    {
        var blueprint = ProjectBlueprintCatalog.Blueprints[0];
        var first = AgentSuiteBlueprintConverter.Convert(blueprint, TestOnlyInputs);
        var second = AgentSuiteBlueprintConverter.Convert(blueprint, TestOnlyInputs);
        var gzip = first.ContentArchive.ToArray();

        Assert.Equal(gzip, second.ContentArchive.ToArray());
        Assert.Equal(AgentSuiteBlueprintConverter.Digest(gzip), first.ContentDigest);
        Assert.Equal(new byte[] { 0x1f, 0x8b }, gzip[..2]);
        Assert.Equal(new byte[4], gzip[4..8]);
        Assert.Equal(0xff, gzip[9]);

        using var reader = new TarReader(new MemoryStream(Decompress(gzip)));
        var names = new List<string>();
        while (reader.GetNextEntry() is { } entry)
        {
            names.Add(entry.Name);
            Assert.Equal(TarEntryFormat.Ustar, entry.Format);
            Assert.Equal(DateTimeOffset.UnixEpoch, entry.ModificationTime);
            Assert.Equal(0, entry.Uid);
            Assert.Equal(0, entry.Gid);
            if (entry.EntryType == TarEntryType.Directory)
                Assert.Equal((UnixFileMode)0b111_101_101, entry.Mode);
            else
            {
                Assert.Equal(TarEntryType.RegularFile, entry.EntryType);
                Assert.Equal((UnixFileMode)0b110_100_100, entry.Mode);
            }
        }
        Assert.Equal(names.Order(StringComparer.Ordinal), names);
        Assert.Contains("agents/", names);
        Assert.Equal(first.Files.Select(file => file.Path).Order(StringComparer.Ordinal), names.Where(name => !name.EndsWith('/')));
    }

    [Fact]
    public void ConvertRejectsUnpinnedImagesMismatchedArchitecturesAndMissingModels()
    {
        var blueprint = ProjectBlueprintCatalog.Blueprints[0];
        var inputs = TestOnlyInputs with
        {
            BuildProfileId = "Bad Profile",
            SourceEpoch = 0,
            RuntimeBase = [new("amd64", "mcr.microsoft.com/dotnet/runtime-deps:10.0.12-noble", 1)],
            Harness = [TestOnlyImage("arm64", "harness")],
            Model = new("openai-compatible", " "),
            RoleModels = ImmutableDictionary<string, AgentSuiteModelInput>.Empty.Add("not-cast", new("openai-compatible", "m")),
        };

        var error = Assert.Throws<AgentSuiteConversionException>(() => AgentSuiteBlueprintConverter.Convert(blueprint, inputs));

        Assert.Contains(error.Errors, item => item.StartsWith("BuildProfileId", StringComparison.Ordinal));
        Assert.Contains(error.Errors, item => item.StartsWith("SourceEpoch", StringComparison.Ordinal));
        Assert.Contains(error.Errors, item => item.Contains("no tag", StringComparison.Ordinal));
        Assert.Contains(error.Errors, item => item.Contains("same architectures", StringComparison.Ordinal));
        Assert.Contains(error.Errors, item => item.StartsWith("Model must", StringComparison.Ordinal));
        Assert.Contains(error.Errors, item => item.Contains("isn't in the roster", StringComparison.Ordinal));
    }

    [Fact]
    public void ConvertRejectsInvalid0xBlueprintsButKeepsWorkflowsV1DoesNotRunYet()
    {
        var invalid = new BlueprintDefinition { Id = "custom-team", Name = "Custom", Roster = ["missing-role"], Workflows = ["default"], ReviewPolicy = "default", SandboxProfile = "default" };
        Assert.Throws<AgentSuiteConversionException>(() => AgentSuiteBlueprintConverter.Convert(invalid, TestOnlyInputs));

        var future = invalid with { Roster = ["backend-engineer"], Workflows = ["software-delivery"] };
        Assert.Equal(["software-delivery"], AgentSuiteBlueprintConverter.Convert(future, TestOnlyInputs).Blueprint.Workflows.ToArray());
    }

    [Fact]
    public void ConvertPackageReadsBlueprintPackageV1RolesAndDefaultModels()
    {
        var (manifest, payloads) = Package();

        var package = AgentSuiteBlueprintConverter.ConvertPackage(manifest, payloads, TestOnlyInputs);

        Assert.Equal("docs-team", package.SuiteName);
        using var agent = JsonDocument.Parse(File(package, "agents/docs-editor.json"));
        Assert.Equal("Docs editor", agent.RootElement.GetProperty("description").GetString());
        Assert.Equal("claude-opus-4.8", agent.RootElement.GetProperty("model").GetProperty("model").GetString());
        Assert.Equal("openai-compatible", agent.RootElement.GetProperty("model").GetProperty("protocol").GetString());
        var charter = Encoding.UTF8.GetString(File(package, "instructions/docs-editor.md"));
        Assert.Contains("Edits product documentation.", charter, StringComparison.Ordinal);
        Assert.Contains("Check every link.", charter, StringComparison.Ordinal);
        using var writer = JsonDocument.Parse(File(package, "agents/backend-engineer.json"));
        Assert.Equal("test-only-model", writer.RootElement.GetProperty("model").GetProperty("model").GetString());

        var explicitModel = TestOnlyInputs with
        {
            RoleModels = ImmutableDictionary<string, AgentSuiteModelInput>.Empty.Add("docs-editor", new("openai-compatible", "chosen")),
        };
        using var overridden = JsonDocument.Parse(File(AgentSuiteBlueprintConverter.ConvertPackage(manifest, payloads, explicitModel), "agents/docs-editor.json"));
        Assert.Equal("chosen", overridden.RootElement.GetProperty("model").GetProperty("model").GetString());
    }

    [Fact]
    public void PackageReaderRejectsDuplicateKeysAndInventoryMismatches()
    {
        var (manifest, payloads) = Package();
        var blueprintPath = "definitions/blueprints/docs-team.json";

        AssertRejected(Encoding.UTF8.GetBytes("{\"schema_version\":\"1\",\"schema_version\":\"1\"}"), payloads, "strict JSON");
        AssertRejected(manifest, payloads.SetItem(blueprintPath, [.. payloads[blueprintPath], (byte)' ']), "size does not match");
        AssertRejected(manifest, payloads.SetItem("extra.txt", [1]), "not listed");
        AssertRejected(manifest, payloads.Remove(blueprintPath), "missing");
        AssertRejected(Manifest(payloads, ("blueprint", "docs-team", "blueprints/docs-team.json")), payloads, "definition.path");
        AssertRejected(Manifest(payloads, version: "1.0"), payloads, "SemVer");
    }

    [Fact]
    public void PackageReaderRefusesWorkflowAndSkillDefinitions()
    {
        var (_, payloads) = Package();
        payloads = payloads
            .Add("definitions/workflows/review.yaml", Encoding.UTF8.GetBytes("id: review\n"))
            .Add("definitions/skills/style.md", Encoding.UTF8.GetBytes("# Style\n"));
        var manifest = Manifest(
            payloads,
            ("blueprint", "docs-team", null),
            ("role", "docs-editor", null),
            ("workflow", "review", null),
            ("skill", "style", null));

        var error = Assert.Throws<AgentSuiteConversionException>(() => BlueprintPackageV1.Read(manifest, payloads));

        Assert.Contains(error.Errors, item => item.Contains("Orchestrator workflow library", StringComparison.Ordinal));
        Assert.Contains(error.Errors, item => item.Contains("project skill catalog", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateBlueprintChecksTheProjectionAgainstSuiteAgents()
    {
        var projection = new ProjectAgentSuiteBlueprint
        {
            SourceBlueprintId = "Bad Id",
            Workflows = ["default", "default", "software-delivery"],
            DefaultWorkflow = "other",
            ReviewPolicy = "strict",
            SandboxProfile = "open",
            SkillBindings = [new("ghost", []), new("writer", ["style"])],
        };

        var errors = ProjectBlueprintCatalog.ValidateBlueprint(projection, ["writer"]);

        Assert.Contains(errors, item => item.StartsWith("Blueprint id", StringComparison.Ordinal));
        Assert.Contains(errors, item => item.StartsWith("Duplicate workflow", StringComparison.Ordinal));
        Assert.Contains(errors, item => item == "Unknown workflow 'software-delivery'.");
        Assert.Contains(errors, item => item.StartsWith("defaultWorkflow", StringComparison.Ordinal));
        Assert.Contains(errors, item => item.StartsWith("Unknown review_policy", StringComparison.Ordinal));
        Assert.Contains(errors, item => item.StartsWith("Unknown sandbox_profile", StringComparison.Ordinal));
        Assert.Contains(errors, item => item == "Skill binding role 'ghost' is not a suite agent.");
        Assert.Empty(ProjectBlueprintCatalog.ValidateBlueprint(new ProjectAgentSuiteBlueprint
        {
            Workflows = ["default"],
            DefaultWorkflow = "default",
            ReviewPolicy = "default",
            SandboxProfile = "restricted",
            SkillBindings = [new("writer", ["style"])],
        }, ["writer"]));
    }

    [Fact]
    public void ApplyBlueprintUsesEffectiveAgentIdsForSkillsAndRestrictsEgress()
    {
        const string effective = "agentsuite:docs-team:writer:0123456789ab";
        var draft = ProjectConfigurationValidator.Validate(new ProjectConfiguration()) with
        {
            AgentCharters = [new ProjectAgentCharter(effective, "Writer", "writer", "Writes.")],
            Casting = [new ProjectAgentCast(effective, "writer", 0)],
            Skills = [new SkillCatalogSetting("style", false, 0) { Revision = 1, ContentDigest = "sha256:" + new string('a', 64), AgentIds = [] }],
            EgressNarrowing =
            [
                new NetworkEgressRule(
                    NetworkEgressPurpose.PublicHttps,
                    NetworkEgressDestinationKind.Fqdn,
                    "api.example.com",
                    443,
                    EgressProtocol.Tcp),
            ],
        };
        var projection = new ProjectAgentSuiteBlueprint
        {
            SourceBlueprintId = "docs-team",
            Workflows = ["default"],
            DefaultWorkflow = "default",
            ReviewPolicy = "default",
            SandboxProfile = "restricted",
            SkillBindings = [new("writer", ["style"])],
        };

        var application = ProjectBlueprintCatalog.ApplyBlueprint(
            draft,
            projection,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["writer"] = effective });
        var applied = application.Draft;

        Assert.Equal([new BlueprintWorkflowReference("docs-team", "default")], applied.BlueprintWorkflowReferences.ToArray());
        Assert.Equal("default", applied.DefaultWorkflowId);
        Assert.Equal([effective], applied.Skills.Single().AgentIds!.Value.ToArray());
        Assert.False(applied.Skills.Single().Enabled);
        Assert.Equal(
            [new BlueprintUnresolvedSkillBinding("writer", "style", BlueprintUnresolvedSkillBinding.SkillDisabled)],
            application.UnresolvedSkillBindings.ToArray());
        Assert.Empty(applied.EgressNarrowing!);

        var unnamed = ProjectBlueprintCatalog.ApplyBlueprint(
            draft,
            projection with { SourceBlueprintId = null, SandboxProfile = "default" },
            new Dictionary<string, string>(StringComparer.Ordinal) { ["writer"] = effective }).Draft;
        Assert.Equal(draft.BlueprintWorkflowReferences, unnamed.BlueprintWorkflowReferences);
        Assert.Equal(draft.EgressNarrowing, unnamed.EgressNarrowing);
    }

    /// <summary>
    /// Inline 0.x apply must give the same proposal as AgentSuite archive import plus the projection. The import
    /// draft follows the #2114 contract: one charter per suite agent with the instructions as the charter, the
    /// description as the name, and the native ID as the role. Effective agent IDs are compared modulo their
    /// #2114-owned hash suffix.
    /// </summary>
    [Theory]
    [MemberData(nameof(BuiltInIds))]
    public void ArchiveImportPlusProjectionMatchesInlineApply(string blueprintId)
    {
        var builtIn = ProjectBlueprintCatalog.Blueprints.Single(item => item.Id == blueprintId);
        var unavailable = builtIn.Workflows.Where(id => id != "default").Select(id => $"Unknown workflow '{id}'.").ToArray();
        var validation = ProjectBlueprintCatalog.Validate(builtIn);
        Assert.Equal(unavailable, validation.Errors.Where(error => error.StartsWith("Unknown workflow", StringComparison.Ordinal)).ToArray());
        Assert.Equal(
            unavailable,
            ProjectBlueprintCatalog.ValidateBlueprint(
                    AgentSuiteBlueprintConverter.Convert(builtIn, TestOnlyInputs).Blueprint, builtIn.Roster)
                .Where(error => error.StartsWith("Unknown workflow", StringComparison.Ordinal))
                .ToArray());

        // Only the default workflow is available until #1914, so both paths are compared with it.
        var blueprint = builtIn with { Workflows = ["default"] };
        var boundSkills = blueprint.SkillBindings.SelectMany(binding => binding.Skills).Distinct().ToList();
        var current = ProjectConfigurationValidator.Validate(new ProjectConfiguration()) with
        {
            Skills = boundSkills
                .Select((skill, index) => new SkillCatalogSetting(skill, index % 2 == 0, index)
                {
                    Revision = 1,
                    ContentDigest = "sha256:" + new string('a', 64),
                    AgentIds = index == 0 ? null : [],
                })
                .ToImmutableArray(),
        };
        var inline = ProjectBlueprintCatalog.CreateDraft(current, null, blueprint);

        var package = AgentSuiteBlueprintConverter.Convert(blueprint, TestOnlyInputs);
        using var suite = JsonDocument.Parse(File(package, "agentsuite.json"));
        var nativeIds = suite.RootElement.GetProperty("agents").EnumerateArray()
            .Select(agent => agent.GetProperty("id").GetString()!)
            .ToList();
        var effective = nativeIds.ToDictionary(
            id => id,
            id => $"agentsuite:{package.SuiteName}:{id}:0123456789ab",
            StringComparer.Ordinal);
        var charters = nativeIds.Select(id =>
        {
            using var agent = JsonDocument.Parse(File(package, $"agents/{id}.json"));
            return new ProjectAgentCharter(
                effective[id],
                agent.RootElement.GetProperty("description").GetString()!,
                id,
                Encoding.UTF8.GetString(File(package, $"instructions/{id}.md")));
        }).ToImmutableArray();
        var imported = current with
        {
            AgentCharters = charters,
            Casting = nativeIds.Select((id, index) => new ProjectAgentCast(effective[id], id, index)).ToImmutableArray(),
        };
        Assert.Empty(ProjectBlueprintCatalog.ValidateBlueprint(package.Blueprint, nativeIds));
        var archive = ProjectBlueprintCatalog.ApplyBlueprint(imported, package.Blueprint, effective);

        var native = effective.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);
        string Native(string agentId) => native.GetValueOrDefault(agentId, agentId);
        Assert.Equal(
            inline.Draft.AgentCharters.Select(item => (item.AgentId, item.Name, item.Role, item.Charter)),
            archive.Draft.AgentCharters.Select(item => (Native(item.AgentId), item.Name, item.Role, item.Charter)));
        Assert.Equal(
            inline.Draft.Casting.Select(item => (item.AgentId, item.Role, item.Order)),
            archive.Draft.Casting.Select(item => (Native(item.AgentId), item.Role, item.Order)));
        Assert.Equal(inline.Draft.BlueprintWorkflowReferences.ToArray(), archive.Draft.BlueprintWorkflowReferences.ToArray());
        Assert.Equal(inline.Draft.DefaultWorkflowId, archive.Draft.DefaultWorkflowId);
        Assert.Equal(inline.Draft.EgressNarrowing?.ToArray(), archive.Draft.EgressNarrowing?.ToArray());
        Assert.Equal(
            inline.Draft.Skills.Select(skill => (skill.SkillId, skill.Enabled, Agents: string.Join(',', skill.AgentIds ?? []))),
            archive.Draft.Skills.Select(skill => (skill.SkillId, skill.Enabled, Agents: string.Join(',', (skill.AgentIds ?? []).Select(Native)))));
        Assert.Equal(inline.UnresolvedSkillBindings.ToArray(), archive.UnresolvedSkillBindings.ToArray());
    }

    private static void AssertRejected(byte[] manifest, IReadOnlyDictionary<string, byte[]> payloads, string expected)
    {
        var error = Assert.Throws<AgentSuiteConversionException>(() => BlueprintPackageV1.Read(manifest, payloads));
        Assert.Contains(error.Errors, item => item.Contains(expected, StringComparison.Ordinal));
    }

    private static (byte[] Manifest, ImmutableDictionary<string, byte[]> Payloads) Package()
    {
        var payloads = ImmutableDictionary<string, byte[]>.Empty
            .Add("definitions/blueprints/docs-team.json", Encoding.UTF8.GetBytes(
                """{"id":"docs-team","name":"Docs team","description":"Writes docs.","roster":["docs-editor","backend-engineer"],"workflows":["default"],"review_policy":"default","sandbox_profile":"restricted","skill_bindings":[{"role_id":"docs-editor","skills":["style"]}]}"""))
            .Add("definitions/roles/docs-editor.json", Encoding.UTF8.GetBytes(
                """{"id":"docs-editor","title":"Docs editor","summary":"Edits product documentation.","default_model":"claude-opus-4.8","capabilities":["docs"],"responsibilities":["Check every link."],"boundaries":["Doesn't change code."]}"""));
        return (Manifest(payloads, ("blueprint", "docs-team", null), ("role", "docs-editor", null)), payloads);
    }

    private static byte[] Manifest(
        IReadOnlyDictionary<string, byte[]> payloads,
        params (string Kind, string Id, string? Path)[] definitions) =>
        Manifest(payloads, "1.0.0", definitions);

    private static byte[] Manifest(IReadOnlyDictionary<string, byte[]> payloads, string version) =>
        Manifest(payloads, version, ("blueprint", "docs-team", null), ("role", "docs-editor", null));

    private static byte[] Manifest(
        IReadOnlyDictionary<string, byte[]> payloads,
        string version,
        params (string Kind, string Id, string? Path)[] definitions)
    {
        if (definitions.Length == 0)
            definitions = [("blueprint", "docs-team", null), ("role", "docs-editor", null)];
        var entries = definitions.Select(definition =>
        {
            var folder = definition.Kind switch { "blueprint" => "blueprints", "role" => "roles", "workflow" => "workflows", _ => "skills" };
            var extension = definition.Kind switch { "workflow" => "yaml", "skill" => "md", _ => "json" };
            var canonicalPath = $"definitions/{folder}/{definition.Id}.{extension}";
            var bytes = payloads.TryGetValue(canonicalPath, out var value) ? value : [];
            return new
            {
                kind = definition.Kind,
                id = definition.Id,
                path = definition.Path ?? canonicalPath,
                size = bytes.Length,
                sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            };
        });
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema_version = "1",
            package = new { id = "docs-team", version },
            definitions = entries,
            provenance = new { source = "catalog" },
        });
    }

    private static string SourceDirectory([CallerFilePath] string sourcePath = "") => Path.GetDirectoryName(sourcePath)!;

    private static byte[] File(AgentSuiteBlueprintPackage package, string path) =>
        package.Files.Single(file => file.Path == path).Content.ToArray();

    private static void AssertDescriptor(AgentSuiteBlueprintPackage package, JsonElement reference)
    {
        var bytes = File(package, reference.GetProperty("path").GetString()!);
        var descriptor = reference.GetProperty("descriptor");
        Assert.Equal(AgentSuiteBlueprintConverter.Digest(bytes), descriptor.GetProperty("digest").GetString());
        Assert.Equal(bytes.Length, descriptor.GetProperty("size").GetInt64());
    }

    private static byte[] Decompress(byte[] gzip)
    {
        using var input = new GZipStream(new MemoryStream(gzip), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }
}
