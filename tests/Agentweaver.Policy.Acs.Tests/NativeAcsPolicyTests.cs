using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AgentControlSpecification;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Agentweaver.Policy.Acs;
using Xunit;

namespace Agentweaver.Policy.Acs.Tests;

[Collection("Native ACS")]
public sealed class NativeAcsPolicyTests
{
    private const string Manifest = """
        agent_control_specification_version: 0.4.0-alpha.1
        policies:
          native_profile:
            type: rego
            bundle: ./policy
            query: data.agent_control_specification.native_profile.pre_model_call_verdict
        intervention_points:
          pre_model_call:
            policy_target: $.model_request
            policy_target_kind: model_request
            policy:
              id: native_profile
              query: data.agent_control_specification.native_profile.pre_model_call_verdict
        """;

    private const string ConversionModule = """
        package agent_control_specification.native_profile

        import rego.v1

        default pre_model_call_verdict := {"decision": "deny"}

        pre_model_call_verdict := {"decision": "allow"} if {
            input.policy_target.value.action_id == "merge"
        }
        """;
    private const string ConversionData = """
        {
          "agent_control_specification": {
            "native_profile": {
              "allowed_actions": ["merge"]
            }
          }
        }
        """;

    [Fact]
    public void RejectsTamperedArtifactsAndNonCanonicalPaths()
    {
        var opaPath = Path.Combine(AppContext.BaseDirectory, "missing-opa");
        var valid = CreateArtifact(ConversionModule);
        var tampered = valid with
        {
            Files = [new NativeAcsArtifactFile(
                "policy/native_profile.rego",
                Encoding.UTF8.GetBytes(ConversionModule + "\n# changed").ToImmutableArray())]
        };
        var tamperedResult = new NativeAcsArtifactValidator(opaPath).Validate(tampered);

        Assert.Contains(tamperedResult.Diagnostics, diagnostic => diagnostic.Code == "artifact_hash");

        var unsafePath = CreateArtifact(ConversionModule, filePath: "../outside.rego");
        var pathResult = new NativeAcsArtifactValidator(opaPath).Validate(unsafePath);

        Assert.Contains(pathResult.Diagnostics, diagnostic => diagnostic.Code == "file_path");

        var invalidEntry = valid with
        {
            Files = [new NativeAcsArtifactFile(null!, ImmutableArray<byte>.Empty)],
            ContentSha256 = string.Empty
        };
        var invalidEntryResult = new NativeAcsArtifactValidator(opaPath).Validate(invalidEntry);

        Assert.Contains(invalidEntryResult.Diagnostics, diagnostic => diagnostic.Code == "file_path");
        Assert.Contains(invalidEntryResult.Diagnostics, diagnostic => diagnostic.Code == "artifact_hash");
    }

    [Fact]
    public async Task InvalidArtifactsDoNotClaimAnUnverifiedHash()
    {
        var opaPath = Path.Combine(AppContext.BaseDirectory, "missing-opa");
        var artifact = CreateArtifact(ConversionModule) with { ContentSha256 = new string('0', 64) };
        var evaluator = new NativeAcsPolicyEvaluator(
            new NativeAcsArtifactValidator(opaPath),
            WorkRoot);

        var result = await evaluator.EvaluateAsync(
            artifact,
            InterventionPoint.PreModelCall,
            Snapshot("merge"));

        Assert.Equal(PolicyEvaluationOutcome.Error, result.Outcome);
        Assert.Equal("unavailable", result.ArtifactSha256);
        Assert.Equal("artifact_hash", result.DiagnosticCode);
    }

    [Fact]
    public void RejectsArtifactSizeAndFileDirectoryConflicts()
    {
        var validator = new NativeAcsArtifactValidator(Path.Combine(AppContext.BaseDirectory, "missing-opa"));
        var oversizedManifest = Pin(new NativeAcsArtifact(
            new byte[65_537].ToImmutableArray(),
            [new NativeAcsArtifactFile("policy/main.rego", Encoding.UTF8.GetBytes(ConversionModule).ToImmutableArray())],
            string.Empty));
        var oversizedFile = Pin(new NativeAcsArtifact(
            Encoding.UTF8.GetBytes(Manifest).ToImmutableArray(),
            [new NativeAcsArtifactFile("policy/main.rego", new byte[262_145].ToImmutableArray())],
            string.Empty));
        var conflictingPaths = Pin(new NativeAcsArtifact(
            Encoding.UTF8.GetBytes(Manifest).ToImmutableArray(),
            [
                new NativeAcsArtifactFile("policy", Encoding.UTF8.GetBytes("{}").ToImmutableArray()),
                new NativeAcsArtifactFile("policy/main.rego", Encoding.UTF8.GetBytes(ConversionModule).ToImmutableArray())
            ],
            string.Empty));
        var tooManyFiles = new NativeAcsArtifact(
            Encoding.UTF8.GetBytes(Manifest).ToImmutableArray(),
            Enumerable.Range(0, 65)
                .Select(index => new NativeAcsArtifactFile(
                    $"data/{index}.json",
                    Encoding.UTF8.GetBytes("{}").ToImmutableArray()))
                .ToImmutableArray(),
            string.Empty);
        var oversizedArtifact = new NativeAcsArtifact(
            Encoding.UTF8.GetBytes(Manifest).ToImmutableArray(),
            Enumerable.Range(0, 5)
                .Select(index => new NativeAcsArtifactFile(
                    $"data/{index}.json",
                    new byte[262_144].ToImmutableArray()))
                .ToImmutableArray(),
            string.Empty);

        Assert.Contains(
            validator.Validate(oversizedManifest).Diagnostics,
            diagnostic => diagnostic.Code == "manifest_size");
        Assert.Contains(
            validator.Validate(oversizedFile).Diagnostics,
            diagnostic => diagnostic.Code == "file_size");
        Assert.Contains(
            validator.Validate(conflictingPaths).Diagnostics,
            diagnostic => diagnostic.Code == "path_conflict");
        Assert.Contains(
            validator.Validate(tooManyFiles).Diagnostics,
            diagnostic => diagnostic.Code == "file_count");
        Assert.Contains(
            validator.Validate(oversizedArtifact).Diagnostics,
            diagnostic => diagnostic.Code == "artifact_size");
    }

    [NativeAcsFact]
    public void RejectsOutOfProfileFactsImportsAndUnsafeBuiltins()
    {
        var opaPath = RequirePinnedOpa();
        var modules = new[]
        {
            ("budget", "input.policy_target.value.budget.remaining > 0", "unknown_fact"),
            ("dynamic", "input.policy_target.value[requested_fact] == true", "unknown_fact"),
            ("unsafe", "http.send({\"url\": \"https://example.invalid\"})", "unsafe_builtin"),
            ("nested-budget", "data.flags[input.policy_target.value.budget] == true", "unknown_fact"),
            ("nested-unsafe", "data.flags[http.send({\"method\": \"GET\", \"url\": \"http://127.0.0.1:9\"}).status_code] == true", "unsafe_builtin")
        };

        foreach (var (name, body, expectedDiagnostic) in modules)
        {
            var artifact = CreateArtifact($$"""
                package agent_control_specification.native_profile
                import rego.v1

                default pre_model_call_verdict := {"decision": "deny"}

                pre_model_call_verdict := {"decision": "allow"} if {
                    {{body}}
                }
                """);

            var result = new NativeAcsArtifactValidator(opaPath).Validate(artifact);

            Assert.False(result.IsValid, name);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == expectedDiagnostic);
        }

        var inputImport = CreateArtifact("""
            package agent_control_specification.native_profile
            import rego.v1
            import input.policy_target.value as facts

            default pre_model_call_verdict := {"decision": "deny"}

            pre_model_call_verdict := {"decision": "allow"} if {
                facts.agent_id == "agent-1"
            }
            """);
        var inputImportResult = new NativeAcsArtifactValidator(opaPath).Validate(inputImport);
        Assert.Contains(inputImportResult.Diagnostics, diagnostic => diagnostic.Code == "input_import");

        var invalidData = CreateArtifact(
            ConversionModule,
            extraFiles:
            [
                new NativeAcsArtifactFile(
                    "policy/data.json",
                    Encoding.UTF8.GetBytes("not json").ToImmutableArray())
            ]);
        var invalidDataResult = new NativeAcsArtifactValidator(opaPath).Validate(invalidData);
        Assert.Contains(invalidDataResult.Diagnostics, diagnostic => diagnostic.Code == "data_json");

        var urlManifest = Manifest.Replace("bundle: ./policy", "bundle: https://example.invalid/policy", StringComparison.Ordinal);
        var urlResult = new NativeAcsArtifactValidator(opaPath).Validate(CreateArtifact(ConversionModule, manifest: urlManifest));
        Assert.Contains(urlResult.Diagnostics, diagnostic => diagnostic.Code == "external_url");

        var absoluteManifest = Manifest.Replace("bundle: ./policy", "bundle: /etc/agentweaver/policy", StringComparison.Ordinal);
        var absoluteResult = new NativeAcsArtifactValidator(opaPath).Validate(CreateArtifact(ConversionModule, manifest: absoluteManifest));
        Assert.Contains(absoluteResult.Diagnostics, diagnostic => diagnostic.Code == "external_path");

        var flowBundleManifest = """
            agent_control_specification_version: 0.4.0-alpha.1
            policies:
              native_profile: { type: rego, bundle: /etc/agentweaver/outside, query: data.agent_control_specification.native_profile.pre_model_call_verdict }
            intervention_points:
              pre_model_call:
                policy_target: $.model_request
                policy_target_kind: model_request
                policy:
                  id: native_profile
                  query: data.agent_control_specification.native_profile.pre_model_call_verdict
            """;
        var flowBundleResult = new NativeAcsArtifactValidator(opaPath).Validate(CreateArtifact(ConversionModule, manifest: flowBundleManifest));
        Assert.Contains(flowBundleResult.Diagnostics, diagnostic => diagnostic.Code == "external_path");

        var extendsManifest = Manifest.Replace(
            "policies:",
            "extends: [/etc/agentweaver/outside.yaml]\npolicies:",
            StringComparison.Ordinal);
        var extendsResult = new NativeAcsArtifactValidator(opaPath).Validate(CreateArtifact(ConversionModule, manifest: extendsManifest));
        Assert.Contains(extendsResult.Diagnostics, diagnostic => diagnostic.Code == "external_path");

        var dataPathsManifest = Manifest.Replace(
            "    bundle: ./policy",
            "    bundle: ./policy\n    data_paths: [/etc/agentweaver/outside.json]",
            StringComparison.Ordinal);
        var dataPathsResult = new NativeAcsArtifactValidator(opaPath).Validate(CreateArtifact(ConversionModule, manifest: dataPathsManifest));
        Assert.Contains(dataPathsResult.Diagnostics, diagnostic => diagnostic.Code == "external_path");

        var unknownHookManifest = Manifest.Replace(
            "  pre_model_call:",
            "  unknown_hook:\n    policy_target: $.model_request\n    policy_target_kind: model_request\n    policy:\n      id: native_profile\n      query: data.agent_control_specification.native_profile.pre_model_call_verdict\n  pre_model_call:",
            StringComparison.Ordinal);
        var unknownHookResult = new NativeAcsArtifactValidator(opaPath).Validate(CreateArtifact(ConversionModule, manifest: unknownHookManifest));
        Assert.False(unknownHookResult.IsValid);

        var unknownRuleManifest = Manifest.Replace("pre_model_call_verdict", "untrusted_rule", StringComparison.Ordinal);
        var unknownRuleResult = new NativeAcsArtifactValidator(opaPath).Validate(CreateArtifact(ConversionModule, manifest: unknownRuleManifest));
        Assert.False(unknownRuleResult.IsValid);

        var transformManifest = Manifest.Replace(
            "    policy:",
            "    transforms:\n      - type: unsupported\n    policy:",
            StringComparison.Ordinal);
        var transformResult = new NativeAcsArtifactValidator(opaPath).Validate(CreateArtifact(ConversionModule, manifest: transformManifest));
        Assert.False(transformResult.IsValid);

        var cyclicManifest = Manifest + "\nextra: &loop { child: *loop }\n";
        var cyclicResult = new NativeAcsArtifactValidator(opaPath).Validate(CreateArtifact(ConversionModule, manifest: cyclicManifest));
        Assert.Contains(cyclicResult.Diagnostics, diagnostic => diagnostic.Code == "manifest_cycle");

        var deeplyNestedManifest = new StringBuilder(Manifest);
        deeplyNestedManifest.AppendLine();
        deeplyNestedManifest.AppendLine("extra:");
        for (var index = 0; index < 66; index++)
            deeplyNestedManifest.Append(' ', (index + 1) * 2).Append("nested_").Append(index).AppendLine(":");
        deeplyNestedManifest.Append(' ', 134).AppendLine("value: terminal");
        var deeplyNestedResult = new NativeAcsArtifactValidator(opaPath).Validate(
            CreateArtifact(ConversionModule, manifest: deeplyNestedManifest.ToString()));
        Assert.Contains(deeplyNestedResult.Diagnostics, diagnostic => diagnostic.Code == "manifest_depth");
    }

    [NativeAcsFact]
    public async Task NativeDecisionsMatchLegacyActionConversionGoldens()
    {
        var opaPath = RequireNativeRuntime();
        var goldenPath = Path.Combine(AppContext.BaseDirectory, "goldens", "policy-conversion.json");
        using var goldenDocument = JsonDocument.Parse(await File.ReadAllTextAsync(goldenPath));
        var legacyProvider = new AgtPolicyProvider();
        var legacyOptions = new AgtPolicyProviderOptions(
            "native-acs-golden",
            1,
            "native-acs-golden-v1",
            ["""
             apiVersion: governance.toolkit/v1
             version: "1.0"
             name: platform-policy
             scope: global
             default_action: deny
             rules:
               - name: allow-merge
                 condition: "action_id == 'merge'"
                 action: allow
                 priority: 100
             """]);
        var evaluator = new NativeAcsPolicyEvaluator(
            new NativeAcsArtifactValidator(opaPath),
            WorkRoot);
        var artifact = CreateArtifact(
            ConversionModule,
            extraFiles:
            [
                new NativeAcsArtifactFile(
                    "policy/data.json",
                    Encoding.UTF8.GetBytes(ConversionData).ToImmutableArray())
            ]);
        var changedData = CreateArtifact(
            ConversionModule,
            extraFiles:
            [
                new NativeAcsArtifactFile(
                    "policy/data.json",
                    Encoding.UTF8.GetBytes(ConversionData.Replace("merge", "inspect", StringComparison.Ordinal)).ToImmutableArray())
            ]);
        Assert.NotEqual(artifact.ContentSha256, changedData.ContentSha256);

        foreach (var testCase in goldenDocument.RootElement.GetProperty("cases").EnumerateArray())
        {
            var actionId = testCase.GetProperty("actionId").GetString()!;
            var expected = Enum.Parse<PolicyEvaluationOutcome>(testCase.GetProperty("outcome").GetString()!);
            var legacy = legacyProvider.Evaluate(
                legacyOptions,
                new AgtPolicyEvaluationRequest("agent-1", actionId));
            var validation = new NativeAcsArtifactValidator(opaPath).Validate(artifact);
            Assert.True(
                validation.IsValid,
                string.Join("; ", validation.Diagnostics.Select(diagnostic => $"{diagnostic.Code}:{diagnostic.Message}")));
            var native = await evaluator.EvaluateAsync(
                artifact,
                InterventionPoint.PreModelCall,
                Snapshot(actionId));

            Assert.Equal(expected, legacy.Outcome);
            Assert.True(
                native.Outcome == expected,
                $"Native outcome was {native.Outcome}: {native.DiagnosticCode ?? "no diagnostic"}.");
            Assert.Equal(artifact.ContentSha256, native.ArtifactSha256);
            AssertNoEvaluationWorkspaces();
        }
    }

    [NativeAcsFact]
    public void NetworkFactsDoNotExpandTheActionProfileInputGate()
    {
        var artifact = CreateArtifact("""
            package agent_control_specification.native_profile
            import rego.v1

            default pre_model_call_verdict := {"decision": "deny"}

            pre_model_call_verdict := {"decision": "allow"} if {
                input.policy_target.value.network.destination.value == "approved.example.com"
            }
            """);

        var result = new NativeAcsArtifactValidator(RequireNativeRuntime()).Validate(artifact);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "unknown_fact");
    }

    [NativeAcsFact]
    public async Task HardDenyPrecedesAllowAndUnavailableHumanFactsRemainUnavailable()
    {
        var opaPath = RequireNativeRuntime();
        var evaluator = new NativeAcsPolicyEvaluator(
            new NativeAcsArtifactValidator(opaPath),
            WorkRoot);
        var artifact = CreateArtifact("""
            package agent_control_specification.native_profile
            import rego.v1

            default pre_model_call_verdict := {"decision": "deny"}

            pre_model_call_verdict := {"decision": "allow"} if {
                input.policy_target.value.action_id == "merge"
                input.policy_target.value.agent_id == "agent-1"
                input.policy_target.value.session_id == "session-1"
                input.policy_target.value.human_available == true
            }
            """);

        var unavailableHuman = await evaluator.EvaluateAsync(
            artifact,
            InterventionPoint.PreModelCall,
            Snapshot("merge"));
        var verifiedHuman = await evaluator.EvaluateAsync(
            artifact,
            InterventionPoint.PreModelCall,
            Snapshot("merge") with
            {
                VerifiedHumanIdentity = new NativeAcsVerifiedHumanIdentity("tenant-1", "human-1")
            });

        Assert.True(
            unavailableHuman.Outcome == PolicyEvaluationOutcome.Deny,
            $"Native outcome was {unavailableHuman.Outcome}: {unavailableHuman.DiagnosticCode ?? "no diagnostic"}.");
        Assert.Equal(PolicyEvaluationOutcome.Allow, verifiedHuman.Outcome);

        var absentHumanFactsArtifact = CreateArtifact("""
            package agent_control_specification.native_profile
            import rego.v1

            default pre_model_call_verdict := {"decision": "deny"}

            pre_model_call_verdict := {"decision": "allow"} if {
                input.policy_target.value.human_oid != ""
            }
            """);
        var absentHumanFacts = await evaluator.EvaluateAsync(
            absentHumanFactsArtifact,
            InterventionPoint.PreModelCall,
            Snapshot("merge"));

        Assert.Equal(PolicyEvaluationOutcome.Deny, absentHumanFacts.Outcome);

        var hardDenyArtifact = CreateArtifact("""
            package agent_control_specification.native_profile
            import rego.v1

            pre_model_call_verdict := {"decision": "deny", "reason": "hard_deny"} if {
                input.policy_target.value.agent_id == "blocked-agent"
            }
            else := {"decision": "allow"} if {
                input.policy_target.value.action_id == "merge"
            }
            else := {"decision": "deny"}
            """);
        var hardDeny = await evaluator.EvaluateAsync(
            hardDenyArtifact,
            InterventionPoint.PreModelCall,
            Snapshot("merge", "blocked-agent"));

        Assert.Equal(PolicyEvaluationOutcome.Deny, hardDeny.Outcome);
        AssertNoEvaluationWorkspaces();
    }

    [NativeAcsFact]
    public async Task NativeRuntimeErrorsFailClosed()
    {
        var realOpa = RequirePinnedOpa();

        var scriptPath = CreateOpaWrapper(realOpa, "exit 2");
        var originalOpaPath = Environment.GetEnvironmentVariable("ACS_OPA_PATH");
        try
        {
            Environment.SetEnvironmentVariable("ACS_OPA_PATH", scriptPath);
            var evaluator = new NativeAcsPolicyEvaluator(
                new NativeAcsArtifactValidator(scriptPath),
                WorkRoot);
            var artifact = CreateArtifact(ConversionModule);
            var validation = new NativeAcsArtifactValidator(scriptPath).Validate(artifact);
            Assert.True(
                validation.IsValid,
                string.Join("; ", validation.Diagnostics.Select(diagnostic => $"{diagnostic.Code}:{diagnostic.Message}")));
            var result = await evaluator.EvaluateAsync(
                artifact,
                InterventionPoint.PreModelCall,
                Snapshot("merge"));

            Assert.Equal(PolicyEvaluationOutcome.Error, result.Outcome);
            Assert.Equal("native_policy_runtime_error", result.DiagnosticCode);
            AssertNoEvaluationWorkspaces();
        }
        finally
        {
            Environment.SetEnvironmentVariable("ACS_OPA_PATH", originalOpaPath);
            File.Delete(scriptPath);
        }
    }

    [NativeAcsFact]
    public async Task NativeEvaluationTimeoutFailsClosed()
    {
        var realOpa = RequirePinnedOpa();

        var markerPath = Path.Combine(AppContext.BaseDirectory, $"acs-opa-{Guid.NewGuid():N}.started");
        var releasePath = Path.Combine(AppContext.BaseDirectory, $"acs-opa-{Guid.NewGuid():N}.release");
        var waitForRelease = $@"while [ ! -f ""{releasePath}"" ]; do sleep 0.01; done";
        var scriptPath = CreateOpaWrapper(realOpa, waitForRelease, markerPath);
        var originalOpaPath = Environment.GetEnvironmentVariable("ACS_OPA_PATH");
        try
        {
            Environment.SetEnvironmentVariable("ACS_OPA_PATH", scriptPath);
            var evaluator = new NativeAcsPolicyEvaluator(
                new NativeAcsArtifactValidator(scriptPath),
                WorkRoot);
            var artifact = CreateArtifact(ConversionModule);
            using var timeout = new CancellationTokenSource();
            var evaluationTask = evaluator.EvaluateAsync(
                artifact,
                InterventionPoint.PreModelCall,
                Snapshot("merge"),
                timeout.Token);
            var stopwatch = Stopwatch.StartNew();
            while (!File.Exists(markerPath) &&
                   !evaluationTask.IsCompleted &&
                   stopwatch.Elapsed < TimeSpan.FromSeconds(10))
                await Task.Delay(20);

            Assert.True(File.Exists(markerPath), "The native OPA evaluation did not start.");
            timeout.Cancel();
            File.WriteAllText(releasePath, string.Empty);
            var result = await evaluationTask.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(PolicyEvaluationOutcome.Error, result.Outcome);
            Assert.Equal("evaluation_cancelled", result.DiagnosticCode);
            AssertNoEvaluationWorkspaces();
        }
        finally
        {
            Environment.SetEnvironmentVariable("ACS_OPA_PATH", originalOpaPath);
            File.Delete(scriptPath);
            File.Delete(markerPath);
            File.Delete(releasePath);
        }
    }

    private static NativeAcsArtifact CreateArtifact(
        string module,
        string filePath = "policy/native_profile.rego",
        string? manifest = null,
        ImmutableArray<NativeAcsArtifactFile> extraFiles = default)
    {
        var files = ImmutableArray.CreateBuilder<NativeAcsArtifactFile>();
        files.Add(new NativeAcsArtifactFile(filePath, Encoding.UTF8.GetBytes(module).ToImmutableArray()));
        if (!extraFiles.IsDefault)
            files.AddRange(extraFiles);
        var artifact = new NativeAcsArtifact(
            Encoding.UTF8.GetBytes(manifest ?? Manifest).ToImmutableArray(),
            files.ToImmutable(),
            string.Empty);
        return Pin(artifact);
    }

    private static NativeAcsArtifact Pin(NativeAcsArtifact artifact) =>
        artifact with { ContentSha256 = NativeAcsArtifactValidator.ComputeContentSha256(artifact) };

    private static NativeAcsEvaluationSnapshot Snapshot(string actionId, string agentId = "agent-1") =>
        new(new SessionIdentity("project-1", "run-1", "session-1"), "tenant-1", agentId, actionId);

    private static void AssertNoEvaluationWorkspaces()
    {
        Assert.DoesNotContain(
            Directory.GetDirectories(WorkRoot),
            path => Guid.TryParseExact(Path.GetFileName(path), "N", out _));
    }

    private static string RequirePinnedOpa()
    {
        var path = Environment.GetEnvironmentVariable("ACS_OPA_PATH");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new InvalidOperationException("Set ACS_OPA_PATH to the pinned OPA 0.70.0 executable.");
        return Path.GetFullPath(path);
    }

    private static string CreateOpaWrapper(string realOpa, string evaluationCommand, string? markerPath = null)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("The pinned ACS package has a Linux-x64 native library.");

        var path = Path.Combine(AppContext.BaseDirectory, $"acs-opa-{Guid.NewGuid():N}.sh");
        var markerCommand = markerPath is null ? string.Empty : $": > \"{markerPath}\"; ";
        File.WriteAllText(
            path,
            $"#!/bin/sh\nif [ \"$1\" = \"eval\" ]; then {markerCommand}{evaluationCommand}; fi\nexec \"{realOpa}\" \"$@\"\n");
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return path;
    }

    private static string RequireNativeRuntime()
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("The pinned ACS package has a Linux-x64 native library.");
        return RequirePinnedOpa();
    }

    private static string WorkRoot => Path.Combine(AppContext.BaseDirectory, "acs-work");
}

[CollectionDefinition("Native ACS", DisableParallelization = true)]
public sealed class NativeAcsTestCollection
{
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class NativeAcsFactAttribute : FactAttribute
{
    public NativeAcsFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
            Skip = "The pinned ACS package has a Linux-x64 native library.";
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ACS_OPA_PATH")) ||
                 !File.Exists(Environment.GetEnvironmentVariable("ACS_OPA_PATH")))
            Skip = "Set ACS_OPA_PATH to the pinned OPA 0.70.0 executable.";
    }
}
