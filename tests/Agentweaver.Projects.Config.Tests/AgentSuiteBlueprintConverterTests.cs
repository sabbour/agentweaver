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
    /// these images can't be pulled. Shipped packages need real, published AgentHost images.
    /// </summary>
    internal static AgentSuiteBuildInputs TestOnlyInputs => new()
    {
        BuildProfileId = "test-only",
        SourceEpoch = 1,
        RuntimeBase = [TestOnlyImage("amd64", "runtime-base"), TestOnlyImage("arm64", "runtime-base")],
        Harness = [TestOnlyImage("amd64", "harness"), TestOnlyImage("arm64", "harness")],
        Model = new("openai-compatible", "test-only-model"),
    };

    // Builds a digest-pinned image reference. The digest is a SHA-256 of a fixed label, so it is stable across
    // runs but never points at a real image.
    private static AgentSuiteImageInput TestOnlyImage(string architecture, string name) => new(
        architecture,
        $"registry.invalid/agentweaver/test-only/{name}@sha256:" +
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"test-only {name} {architecture}"))),
        1);

    public static TheoryData<string> BuiltInIds()
    {
        // One theory case per built-in blueprint, so a failure names the blueprint that broke.
        var data = new TheoryData<string>();
        foreach (var blueprint in ProjectBlueprintCatalog.Blueprints)
            data.Add(blueprint.Id);
        return data;
    }

    [Theory]
    [MemberData(nameof(BuiltInIds))]
    public void BuiltInConvertsToOneAgentPerRoleWithThe0xCharterAndProjection(string blueprintId)
    {
        // Arrange: pick the built-in blueprint for this theory case.
        var blueprint = ProjectBlueprintCatalog.Blueprints.Single(item => item.Id == blueprintId);

        // Act: convert it into an AgentSuite package with the test-only images.
        var package = AgentSuiteBlueprintConverter.Convert(blueprint, TestOnlyInputs);

        // The suite takes the blueprint ID as its name.
        Assert.Equal(blueprint.Id, package.SuiteName);
        using var suite = JsonDocument.Parse(File(package, "agentsuite.json"));
        Assert.Equal(blueprint.Id, suite.RootElement.GetProperty("name").GetString());
        // There is exactly one suite agent per roster role, in roster order.
        Assert.Equal(
            blueprint.Roster,
            suite.RootElement.GetProperty("agents").EnumerateArray().Select(agent => agent.GetProperty("id").GetString()));
        // Each role gets two compositions (one per test-only architecture: amd64 and arm64).
        Assert.Equal(
            blueprint.Roster.Length * 2,
            suite.RootElement.GetProperty("compositions").GetArrayLength());
        foreach (var roleId in blueprint.Roster)
        {
            // The agent's instructions file must be the exact 0.x charter text, and its description the role title.
            var (title, charter) = ProjectBlueprintCatalog.ResolveRole(blueprint, roleId);
            Assert.Equal(charter, Encoding.UTF8.GetString(File(package, $"instructions/{roleId}.md")));
            using var agent = JsonDocument.Parse(File(package, $"agents/{roleId}.json"));
            Assert.Equal(title, agent.RootElement.GetProperty("description").GetString());
            // Built-ins have no per-role model, so every agent uses the default model from the build inputs.
            Assert.Equal("test-only-model", agent.RootElement.GetProperty("model").GetProperty("model").GetString());
        }

        // The blueprint projection carries the settings AgentSuite has no place for (workflows, policies,
        // skill bindings). Check each one is copied from the 0.x blueprint unchanged.
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

        // Every JSON file must be canonical so its digest is stable; the file path is the failure message.
        foreach (var file in package.Files.Where(file => file.Path.EndsWith(".json", StringComparison.Ordinal)))
            Assert.True(CanonicalJson.IsCanonical(file.Content.AsSpan()), file.Path);

        // Collect every file reference in the suite root (agents, compositions, build profiles and the tool
        // provider catalog) and check its digest and size match the referenced file.
        using var suite = JsonDocument.Parse(File(package, "agentsuite.json"));
        var references = suite.RootElement.GetProperty("agents").EnumerateArray()
            .Concat(suite.RootElement.GetProperty("compositions").EnumerateArray())
            .Concat(suite.RootElement.GetProperty("buildProfiles").EnumerateArray())
            .Append(suite.RootElement.GetProperty("toolProviderCatalog"));
        foreach (var reference in references)
            AssertDescriptor(package, reference);
        // Agent files reference their instructions file one level down, so check those descriptors too.
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
        // The golden archive lives next to this source file, not in the build output, so updates land in git.
        var fixture = Path.Combine(SourceDirectory(), "Fixtures", "AgentSuiteBlueprints", $"{blueprintId}.tar.gz");

        // Opt-in regeneration: when the update variable is set, overwrite the fixture before comparing.
        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fixture)!);
            System.IO.File.WriteAllBytes(fixture, package.ContentArchive.ToArray());
        }

        // Compression bytes can differ between zlib builds, so compare the tar stream.
        Assert.Equal(Decompress(System.IO.File.ReadAllBytes(fixture)), Decompress(package.ContentArchive.ToArray()));
    }

    /// <summary>Every converted built-in passes the AgentSuite import validator, which is the only AgentSuite loader.</summary>
    [Theory]
    [MemberData(nameof(BuiltInIds))]
    public void BuiltInArchivePassesTheAgentSuiteImportValidator(string blueprintId)
    {
        // Arrange: convert the built-in blueprint into an archive.
        var blueprint = ProjectBlueprintCatalog.Blueprints.Single(item => item.Id == blueprintId);
        var package = AgentSuiteBlueprintConverter.Convert(blueprint, TestOnlyInputs);

        // Act: run the same validator that project import uses on the gzip archive bytes.
        var result = Agentweaver.Projects.Config.AgentSuite.AgentSuiteValidator.Validate(
            Agentweaver.Projects.Config.AgentSuite.AgentSuiteArchiveFormat.ContentTarGzip,
            package.ContentArchive.ToArray());

        // The archive is accepted; on failure, print every native and profile error to help debugging.
        Assert.True(
            result.Accepted,
            string.Join('\n', result.NativeErrors.Concat(result.ProfileErrors.Select(item => $"{item.Code}: {item.Message}"))));
        // The validator sees the roster as the agent list, and no role collides with a reserved orchestration role.
        Assert.Equal(blueprint.Roster, result.Graph!.Agents.Select(agent => agent.Id));
        Assert.DoesNotContain(blueprint.Roster, ProjectConfigurationValidator.IsReservedOrchestrationRole);
    }

    [Fact]
    public void ArchiveFollowsTheAgentSuiteContentRules()
    {
        // Arrange/act: convert the same blueprint twice.
        var blueprint = ProjectBlueprintCatalog.Blueprints[0];
        var first = AgentSuiteBlueprintConverter.Convert(blueprint, TestOnlyInputs);
        var second = AgentSuiteBlueprintConverter.Convert(blueprint, TestOnlyInputs);
        var gzip = first.ContentArchive.ToArray();

        // The output is byte-for-byte deterministic and the reported digest matches the bytes.
        Assert.Equal(gzip, second.ContentArchive.ToArray());
        Assert.Equal(AgentSuiteBlueprintConverter.Digest(gzip), first.ContentDigest);
        // Gzip header checks: magic bytes 1f 8b, a zero modification time (bytes 4-8),
        // and OS byte 0xff ("unknown") so the host OS doesn't leak into the bytes.
        Assert.Equal(new byte[] { 0x1f, 0x8b }, gzip[..2]);
        Assert.Equal(new byte[4], gzip[4..8]);
        Assert.Equal(0xff, gzip[9]);

        // Walk every tar entry and check the fixed metadata rules.
        using var reader = new TarReader(new MemoryStream(Decompress(gzip)));
        var names = new List<string>();
        while (reader.GetNextEntry() is { } entry)
        {
            names.Add(entry.Name);
            // USTAR format, epoch timestamps and root ownership keep the archive reproducible.
            Assert.Equal(TarEntryFormat.Ustar, entry.Format);
            Assert.Equal(DateTimeOffset.UnixEpoch, entry.ModificationTime);
            Assert.Equal(0, entry.Uid);
            Assert.Equal(0, entry.Gid);
            // Directories are 0755 and files are plain regular files with 0644; no links or special entries.
            if (entry.EntryType == TarEntryType.Directory)
                Assert.Equal((UnixFileMode)0b111_101_101, entry.Mode);
            else
            {
                Assert.Equal(TarEntryType.RegularFile, entry.EntryType);
                Assert.Equal((UnixFileMode)0b110_100_100, entry.Mode);
            }
        }
        // Entries are in ordinal order, directory entries are present, and the files match the package file list.
        Assert.Equal(names.Order(StringComparer.Ordinal), names);
        Assert.Contains("agents/", names);
        Assert.Equal(first.Files.Select(file => file.Path).Order(StringComparer.Ordinal), names.Where(name => !name.EndsWith('/')));
    }

    [Fact]
    public void ConvertRejectsUnpinnedImagesMismatchedArchitecturesAndMissingModels()
    {
        // Arrange: break every build input at once so one call reports all the errors together:
        // an invalid profile ID, a zero epoch, a tag-only image, amd64-only runtime vs arm64-only harness,
        // a blank model, and a role model for a role that isn't in the roster.
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

        // Act: conversion fails instead of producing a package.
        var error = Assert.Throws<AgentSuiteConversionException>(() => AgentSuiteBlueprintConverter.Convert(blueprint, inputs));

        // Assert: one error for each broken input, so the converter collects errors rather than stopping at the first.
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
        // A roster role that doesn't exist makes the 0.x blueprint invalid, so conversion is refused.
        var invalid = new BlueprintDefinition { Id = "custom-team", Name = "Custom", Roster = ["missing-role"], Workflows = ["default"], ReviewPolicy = "default", SandboxProfile = "default" };
        Assert.Throws<AgentSuiteConversionException>(() => AgentSuiteBlueprintConverter.Convert(invalid, TestOnlyInputs));

        // A valid roster with a workflow v1 can't run yet still converts, and the workflow ID is kept in the
        // projection so it isn't lost when that workflow becomes available.
        var future = invalid with { Roster = ["backend-engineer"], Workflows = ["software-delivery"] };
        Assert.Equal(["software-delivery"], AgentSuiteBlueprintConverter.Convert(future, TestOnlyInputs).Blueprint.Workflows.ToArray());
    }

    [Fact]
    public void ConvertPackageReadsBlueprintPackageV1RolesAndDefaultModels()
    {
        // Arrange: a package with a custom "docs-editor" role (with its own default model) and a roster that also
        // uses the built-in "backend-engineer" role (which has no model).
        var (manifest, payloads) = Package();

        // Act: convert the package rather than a built-in blueprint.
        var package = AgentSuiteBlueprintConverter.ConvertPackage(manifest, payloads, TestOnlyInputs);

        Assert.Equal("docs-team", package.SuiteName);
        // The custom role's title becomes the agent description, and its default_model is used.
        using var agent = JsonDocument.Parse(File(package, "agents/docs-editor.json"));
        Assert.Equal("Docs editor", agent.RootElement.GetProperty("description").GetString());
        Assert.Equal("claude-opus-4.8", agent.RootElement.GetProperty("model").GetProperty("model").GetString());
        Assert.Equal("openai-compatible", agent.RootElement.GetProperty("model").GetProperty("protocol").GetString());
        // The generated charter includes the role summary and its responsibilities.
        var charter = Encoding.UTF8.GetString(File(package, "instructions/docs-editor.md"));
        Assert.Contains("Edits product documentation.", charter, StringComparison.Ordinal);
        Assert.Contains("Check every link.", charter, StringComparison.Ordinal);
        // The built-in role has no default model, so it falls back to the build input model.
        using var writer = JsonDocument.Parse(File(package, "agents/backend-engineer.json"));
        Assert.Equal("test-only-model", writer.RootElement.GetProperty("model").GetProperty("model").GetString());

        // An explicit per-role model in the build inputs wins over the role's default_model.
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

        // Start from a valid package and break one thing per call:
        // a manifest with a duplicate JSON key,
        AssertRejected(Encoding.UTF8.GetBytes("{\"schema_version\":\"1\",\"schema_version\":\"1\"}"), payloads, "strict JSON");
        // a payload one byte longer than the size the manifest records,
        AssertRejected(manifest, payloads.SetItem(blueprintPath, [.. payloads[blueprintPath], (byte)' ']), "size does not match");
        // an extra file the manifest doesn't list,
        AssertRejected(manifest, payloads.SetItem("extra.txt", [1]), "not listed");
        // a listed file that is missing,
        AssertRejected(manifest, payloads.Remove(blueprintPath), "missing");
        // a definition path outside the canonical definitions/ folder,
        AssertRejected(Manifest(payloads, ("blueprint", "docs-team", "blueprints/docs-team.json")), payloads, "definition.path");
        // and a package version that isn't full SemVer.
        AssertRejected(Manifest(payloads, version: "1.0"), payloads, "SemVer");
    }

    [Fact]
    public void PackageReaderRefusesWorkflowAndSkillDefinitions()
    {
        // Arrange: add a workflow and a skill definition to an otherwise valid package and list them in the manifest.
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

        // Both are refused, and each error points to where that kind of definition belongs in v1.
        Assert.Contains(error.Errors, item => item.Contains("Orchestrator workflow library", StringComparison.Ordinal));
        Assert.Contains(error.Errors, item => item.Contains("project skill catalog", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateBlueprintChecksTheProjectionAgainstSuiteAgents()
    {
        // Arrange: a projection where every field is wrong: an invalid ID, a duplicate and an unknown workflow,
        // a default workflow outside the list, unknown policies, and a skill binding for a role the suite lacks.
        var projection = new ProjectAgentSuiteBlueprint
        {
            SourceBlueprintId = "Bad Id",
            Workflows = ["default", "default", "software-delivery"],
            DefaultWorkflow = "other",
            ReviewPolicy = "strict",
            SandboxProfile = "open",
            SkillBindings = [new("ghost", []), new("writer", ["style"])],
        };

        // Act: validate against a suite whose only agent is "writer".
        var errors = ProjectBlueprintCatalog.ValidateBlueprint(projection, ["writer"]);

        // Assert: one error per problem.
        Assert.Contains(errors, item => item.StartsWith("Blueprint id", StringComparison.Ordinal));
        Assert.Contains(errors, item => item.StartsWith("Duplicate workflow", StringComparison.Ordinal));
        Assert.Contains(errors, item => item == "Unknown workflow 'software-delivery'.");
        Assert.Contains(errors, item => item.StartsWith("defaultWorkflow", StringComparison.Ordinal));
        Assert.Contains(errors, item => item.StartsWith("Unknown review_policy", StringComparison.Ordinal));
        Assert.Contains(errors, item => item.StartsWith("Unknown sandbox_profile", StringComparison.Ordinal));
        Assert.Contains(errors, item => item == "Skill binding role 'ghost' is not a suite agent.");
        // Control case: a valid projection (no source ID is allowed) produces no errors.
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
        // Arrange: a draft as import would leave it. The agent uses its effective ID (suite name, native ID and
        // hash), the "style" skill is disabled and bound to no agents, and there is one egress rule.
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
        // The projection binds "style" to the native role "writer" and asks for the restricted sandbox.
        var projection = new ProjectAgentSuiteBlueprint
        {
            SourceBlueprintId = "docs-team",
            Workflows = ["default"],
            DefaultWorkflow = "default",
            ReviewPolicy = "default",
            SandboxProfile = "restricted",
            SkillBindings = [new("writer", ["style"])],
        };

        // Act: apply the projection, mapping the native role ID to the effective agent ID.
        var application = ProjectBlueprintCatalog.ApplyBlueprint(
            draft,
            projection,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["writer"] = effective });
        var applied = application.Draft;

        // The workflow is recorded as coming from this blueprint and becomes the default.
        Assert.Equal([new BlueprintWorkflowReference("docs-team", "default")], applied.BlueprintWorkflowReferences.ToArray());
        Assert.Equal("default", applied.DefaultWorkflowId);
        // The skill is bound to the effective agent ID, not the native "writer" ID.
        Assert.Equal([effective], applied.Skills.Single().AgentIds!.Value.ToArray());
        // Apply doesn't enable a disabled skill; it reports the binding as unresolved instead.
        Assert.False(applied.Skills.Single().Enabled);
        Assert.Equal(
            [new BlueprintUnresolvedSkillBinding("writer", "style", BlueprintUnresolvedSkillBinding.SkillDisabled)],
            application.UnresolvedSkillBindings.ToArray());
        // The restricted sandbox profile clears all egress rules.
        Assert.Empty(applied.EgressNarrowing!);

        // Without a source blueprint ID and with the default sandbox, workflow references and egress are untouched.
        var unnamed = ProjectBlueprintCatalog.ApplyBlueprint(
            draft,
            projection with { SourceBlueprintId = null, SandboxProfile = "default" },
            new Dictionary<string, string>(StringComparer.Ordinal) { ["writer"] = effective }).Draft;
        Assert.Equal(draft.BlueprintWorkflowReferences, unnamed.BlueprintWorkflowReferences);
        Assert.Equal(draft.EgressNarrowing, unnamed.EgressNarrowing);
    }

    /// <summary>
    /// Inline 0.x apply must give the same proposal as AgentSuite archive import plus the projection. The import
    /// draft follows the AgentSuite import contract: one charter per suite agent with the instructions as the charter, the
    /// description as the name, and the native ID as the role. Effective agent IDs are compared ignoring
    /// the hash suffix the import service adds.
    /// </summary>
    [Theory]
    [MemberData(nameof(BuiltInIds))]
    public void ArchiveImportPlusProjectionMatchesInlineApply(string blueprintId)
    {
        // First, confirm both validators report the same unavailable workflows for the original built-in,
        // so trimming the workflows below hides nothing else.
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

        // Only the default workflow is registered today, so both paths use it.
        var blueprint = builtIn with { Workflows = ["default"] };
        // Put every bound skill in the catalog with a mix of states: even indexes enabled, odd disabled; the first
        // has no agent list (null) and the rest an empty list. This exercises each skill-binding branch.
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
        // Path 1: the 0.x inline apply.
        var inline = ProjectBlueprintCatalog.CreateDraft(current, null, blueprint);

        // Path 2: convert to an archive, then rebuild the draft that import would produce from it.
        var package = AgentSuiteBlueprintConverter.Convert(blueprint, TestOnlyInputs);
        using var suite = JsonDocument.Parse(File(package, "agentsuite.json"));
        var nativeIds = suite.RootElement.GetProperty("agents").EnumerateArray()
            .Select(agent => agent.GetProperty("id").GetString()!)
            .ToList();
        // Effective IDs use a fixed fake hash; the real hash comes from the import service.
        var effective = nativeIds.ToDictionary(
            id => id,
            id => $"agentsuite:{package.SuiteName}:{id}:0123456789ab",
            StringComparer.Ordinal);
        // Build charters the way import does: description -> name, native ID -> role, instructions -> charter.
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
        // The projection is valid for the imported agents, then gets applied on top of the imported draft.
        Assert.Empty(ProjectBlueprintCatalog.ValidateBlueprint(package.Blueprint, nativeIds));
        var archive = ProjectBlueprintCatalog.ApplyBlueprint(imported, package.Blueprint, effective);

        // Map effective IDs back to native IDs so the two drafts can be compared directly.
        var native = effective.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);
        string Native(string agentId) => native.GetValueOrDefault(agentId, agentId);
        // Assert: charters, casting, workflows, egress, skills and unresolved bindings are the same on both paths.
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

    // Reading the package must fail with at least one error that contains the expected text.
    private static void AssertRejected(byte[] manifest, IReadOnlyDictionary<string, byte[]> payloads, string expected)
    {
        var error = Assert.Throws<AgentSuiteConversionException>(() => BlueprintPackageV1.Read(manifest, payloads));
        Assert.Contains(error.Errors, item => item.Contains(expected, StringComparison.Ordinal));
    }

    // A minimal valid blueprint package: one blueprint and one custom role. The roster also names the built-in
    // "backend-engineer" role, which has no file here, to check that built-in roles resolve without a definition.
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
        // Builds a manifest whose sizes and hashes match the given payloads, so tests can break one thing at a time.
        if (definitions.Length == 0)
            definitions = [("blueprint", "docs-team", null), ("role", "docs-editor", null)];
        var entries = definitions.Select(definition =>
        {
            // Each kind lives in its own folder and file type. A missing payload hashes as empty bytes;
            // a path override lets a test point the entry somewhere invalid.
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

    // A file reference's descriptor must record the referenced file's real digest and byte size.
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
