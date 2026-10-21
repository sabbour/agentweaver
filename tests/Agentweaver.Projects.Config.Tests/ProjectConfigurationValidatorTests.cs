using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed class ProjectConfigurationValidatorTests
{
    private static PlatformRuntimeDefaults PlatformDefaults() => new()
    {
        EgressBaseline = [Fqdn("API.Example.com.")],
        RunLimits = new CopilotRunLimits
        {
            MaxModelTurns = 12,
            MaxToolCalls = 100,
            MaxChildren = 4,
            MaxConcurrentChildren = 2,
            MaxWallTimeSeconds = 3600,
            MaxPromptTokens = 20000,
            MaxRevisionAttempts = 8,
            CopilotSoftCreditLimit = 10m,
            CopilotHardCreditLimit = 20m,
        },
    };

    private static NetworkEgressRule Fqdn(string host, int port = 443) =>
        new(NetworkEgressPurpose.ModelEndpoint, NetworkEgressDestinationKind.Fqdn,
            host, port, EgressProtocol.Tcp);

    [Fact]
    public void ExplicitModelModesBelongToProjectOrPlatformSettingsAndLegacyModeIsNotInferred()
    {
        var key = new SecretRef("selected-project-key", "version-1");
        var project = new ProjectConfiguration
        {
            ModelSelection = new("project-byok", key, ModelSourceMode.Byok)
        };
        Assert.Equal(ModelSourceMode.Byok,
            ProjectConfigurationValidator.Validate(project).ModelSelection!.SourceMode);
        var platform = PlatformDefaults() with
        {
            ModelSelection = new("platform-copilot", null, ModelSourceMode.HostedCopilot, Guid.NewGuid())
        };
        Assert.Equal(ModelSourceMode.HostedCopilot,
            ProjectConfigurationValidator.Validate(platform).ModelSelection!.SourceMode);
        Assert.Throws<ProjectConfigException>(() => ProjectConfigurationValidator.Validate(project with
        {
            ModelSelection = project.ModelSelection! with { SourceMode = (ModelSourceMode)123 }
        }));
        var legacy = new ModelSelectionSettings("legacy-model", key);
        Assert.Null(legacy.SourceMode);
        Assert.Null(legacy.ConnectionId);
        Assert.DoesNotContain("sourceMode", JsonSerializer.Serialize(legacy, new JsonSerializerOptions(
            JsonSerializerDefaults.Web)));
        Assert.DoesNotContain("connectionId", JsonSerializer.Serialize(legacy, new JsonSerializerOptions(
            JsonSerializerDefaults.Web)));
        Assert.Throws<ProjectConfigException>(() => ProjectConfigurationValidator.Validate(platform with
        {
            ModelSelection = platform.ModelSelection! with { CredentialReference = key }
        }));
        Assert.Throws<ProjectConfigException>(() => ProjectConfigurationValidator.Validate(platform with
        {
            ModelSelection = platform.ModelSelection! with { ConnectionId = null }
        }));
        Assert.Throws<ProjectConfigException>(() => ProjectConfigurationValidator.Validate(project with
        {
            ModelSelection = project.ModelSelection! with { ConnectionId = Guid.NewGuid() }
        }));
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ProjectConfiguration>(
                """{"personalModelSelection":{"reference":"personal-byok"}}"""));
    }

    [Fact]
    public void ByokBindingPinMustMatchTheAcceptedModelReferenceAndProvider()
    {
        var pin = new RuntimeModelBindingPin(1, "byok-model", "azure-deployment", ModelSourceMode.Byok,
            "bindings-v1", new string('a', 64)) { ProviderType = "azure" };
        var project = new ProjectConfiguration
        {
            ModelSelection = new("byok-model", new SecretRef("model-key", "version-1"), ModelSourceMode.Byok)
            {
                ModelBindingPin = pin
            }
        };
        Assert.Equal(pin, ProjectConfigurationValidator.Validate(project).ModelSelection!.ModelBindingPin);
        foreach (var changed in new[]
        {
            pin with { ModelSelectionReference = "foreign" },
            pin with { SourceMode = ModelSourceMode.HostedCopilot },
            pin with { ProviderType = null },
            pin with { ConfigurationHash = "invalid" },
            pin with { ConfigurationHash = new string('A', 64) },
            pin with { ModelId = "invalid model" },
            pin with { ConfigurationRevision = new string('a', 257) }
        })
            Assert.Throws<ProjectConfigException>(() => ProjectConfigurationValidator.Validate(project with
            {
                ModelSelection = project.ModelSelection! with { ModelBindingPin = changed }
            }));
        Assert.DoesNotContain("modelBindingPin", JsonSerializer.Serialize(
            new ModelSelectionSettings("legacy"), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    [Fact]
    public void ResolvesTaskRoleAndAgentModelOverridesAndKeepsExplicitProjectSelectionGlobal()
    {
        var platform = PlatformDefaults() with
        {
            ModelSelection = new("global-model", null, ModelSourceMode.HostedCopilot, Guid.NewGuid()),
        };
        var project = ProjectConfigurationValidator.Validate(new ProjectConfiguration
        {
            AgentCharters = [new ProjectAgentCharter("agent-1", "Engineer", "engineer", "Build software.")],
            Casting = [new ProjectAgentCast("agent-1", "engineer", 0)],
            ModelTaskOverrides =
                [new ModelTaskSelectionOverride(ModelTaskKind.CodeWriting, new("project-code", "medium"))],
            ModelRoleOverrides =
                [new ModelRoleSelectionOverride("engineer", new("role-model", "low"))],
            ModelAgentOverrides =
                [new ModelAgentSelectionOverride("agent-1", new("agent-model", "xhigh"))],
        });
        var context = new RunSelectionContext
        {
            Revision = "model-routing-v1",
            AvailableModelSelectionReferences = ImmutableHashSet.Create(
                StringComparer.Ordinal,
                "global-model",
                "gpt-6.1-sol",
                "gpt-6-luna",
                "project-code",
                "role-model",
                "agent-model"),
        };

        var resolved = ProjectsConfigService.ResolveModelSelections(project, platform, context);
        var taskRoute = Assert.Single(resolved.TaskSelections.Where(item =>
            item.TaskKind == ModelTaskKind.CodeWriting && item.RoleId is null && item.AgentId is null));
        Assert.Equal("project-code", taskRoute.ModelSelectionReference);
        Assert.Equal("medium", taskRoute.ReasoningEffort);
        var roleRoute = Assert.Single(resolved.TaskSelections.Where(item =>
            item.TaskKind == ModelTaskKind.CodeWriting && item.RoleId == "engineer" && item.AgentId is null));
        Assert.Equal("role-model", roleRoute.ModelSelectionReference);
        Assert.Equal("low", roleRoute.ReasoningEffort);
        var agentRoute = Assert.Single(resolved.TaskSelections.Where(item =>
            item.TaskKind == ModelTaskKind.CodeWriting && item.RoleId == "engineer" && item.AgentId == "agent-1"));
        Assert.Equal("agent-model", agentRoute.ModelSelectionReference);
        Assert.Equal("xhigh", agentRoute.ReasoningEffort);
        var defaultRoute = Assert.Single(resolved.TaskSelections.Where(item =>
            item.TaskKind == ModelTaskKind.Planning && item.RoleId is null && item.AgentId is null));
        Assert.Equal("gpt-6.1-sol", defaultRoute.ModelSelectionReference);
        Assert.Equal("high", defaultRoute.ReasoningEffort);

        var explicitProject = ProjectConfigurationValidator.Validate(project with
        {
            ModelSelection = new("manual-model", new SecretRef("model-key", "v1"), ModelSourceMode.Byok),
        });
        var explicitContext = context with
        {
            AvailableModelSelectionReferences = ImmutableHashSet.Create(StringComparer.Ordinal, "manual-model"),
        };
        var globallySelected = ProjectsConfigService.ResolveModelSelections(explicitProject, platform, explicitContext);
        Assert.All(globallySelected.TaskSelections, item =>
        {
            Assert.Equal("manual-model", item.ModelSelectionReference);
            Assert.Equal("high", item.ReasoningEffort);
        });
    }

    [Fact]
    public void CoalescesEquivalentRoutesWithoutDroppingPinsAndRejectsConflictingPins()
    {
        var pin = new RuntimeModelBindingPin(
            1, "gpt-6.1-sol", "reasoning-model", ModelSourceMode.HostedCopilot,
            "model-bindings-v1", new string('a', 64)) { ReasoningEffort = "high" };
        var project = ProjectConfigurationValidator.Validate(new ProjectConfiguration
        {
            AgentCharters = [new ProjectAgentCharter("agent-1", "Engineer", "engineer", "Build software.")],
            Casting = [new ProjectAgentCast("agent-1", "engineer", 0)],
            ModelRoleOverrides =
                [new ModelRoleSelectionOverride("engineer", new("gpt-6.1-sol", "high", pin))]
        });
        var platform = PlatformDefaults() with
        {
            ModelSelection = new("gpt-6.1-sol", null, ModelSourceMode.HostedCopilot, Guid.NewGuid())
        };
        var context = new RunSelectionContext
        {
            Revision = "model-routing-v1",
            AvailableModelSelectionReferences = ImmutableHashSet.Create(
                StringComparer.Ordinal, "gpt-6.1-sol", "gpt-6-luna")
        };

        var resolved = ProjectsConfigService.ResolveModelSelections(project, platform, context);
        var effective = Assert.Single(resolved.EffectiveSelections.Where(item =>
            item.Selection.Reference == "gpt-6.1-sol" && item.Selection.ReasoningEffort == "high"));
        Assert.Equal(pin, effective.Selection.ModelBindingPin);

        var conflictingProject = ProjectConfigurationValidator.Validate(project with
        {
            ModelAgentOverrides =
            [
                new ModelAgentSelectionOverride(
                    "agent-1", new("gpt-6.1-sol", "high", pin with { ModelId = "other-model" }))
            ]
        });
        Assert.Equal("model_selection_conflict",
            Assert.Throws<ProjectConfigException>(() =>
                ProjectsConfigService.ResolveModelSelections(conflictingProject, platform, context)).Code);
    }

    [Fact]
    public void ResolvesOnlyConfiguredEligibleRoutesForLargeCasts()
    {
        var roster = Enumerable.Range(0, 16)
            .Select(index => (AgentId: $"agent-{index}", RoleId: $"role-{index}"))
            .ToArray();
        var project = ProjectConfigurationValidator.Validate(new ProjectConfiguration
        {
            AgentCharters = roster.Select(item =>
                new ProjectAgentCharter(item.AgentId, item.AgentId, item.RoleId, "Complete assigned work."))
                .ToImmutableArray(),
            Casting = roster.Select((item, index) =>
                new ProjectAgentCast(item.AgentId, item.RoleId, index)).ToImmutableArray(),
            ModelRoleOverrides = roster.Select((item, index) =>
                new ModelRoleSelectionOverride(item.RoleId, new($"role-model-{index}", "high")))
                .ToImmutableArray(),
            ModelAgentOverrides = roster.Select((item, index) =>
                new ModelAgentSelectionOverride(item.AgentId, new($"agent-model-{index}", "high")))
                .ToImmutableArray()
        });
        var platform = PlatformDefaults() with
        {
            ModelRoleDefaults = [new("unused-role", new("unused-role-model", "high"))],
            ModelAgentDefaults = [new("unused-agent", new("unused-agent-model", "high"))]
        };
        var context = new RunSelectionContext
        {
            Revision = "model-routing-v1",
            AvailableModelSelectionReferences = roster
                .SelectMany((_, index) => new[] { $"role-model-{index}", $"agent-model-{index}" })
                .Append("gpt-6.1-sol")
                .Append("gpt-6-luna")
                .Append("unused-role-model")
                .Append("unused-agent-model")
                .ToImmutableHashSet(StringComparer.Ordinal)
        };

        var resolved = ProjectsConfigService.ResolveModelSelections(project, platform, context);

        Assert.Equal(264, resolved.TaskSelections.Length);
        Assert.Equal(34, resolved.EffectiveSelections.Length);
        Assert.Equal(8, resolved.TaskSelections.Count(item => item.RoleId is null && item.AgentId is null));
        Assert.Equal(128, resolved.TaskSelections.Count(item => item.RoleId is not null && item.AgentId is null));
        Assert.Equal(128, resolved.TaskSelections.Count(item => item.AgentId is not null));
        Assert.DoesNotContain(resolved.TaskSelections, item =>
            item.RoleId == "unused-role" || item.AgentId == "unused-agent");
        Assert.DoesNotContain(resolved.EffectiveSelections, item =>
            item.Selection.Reference is "unused-role-model" or "unused-agent-model");
    }

    [Fact]
    public void NormalizesEgressAndAllowsOnlySubsetWithRequiredDestinations()
    {
        var baseline = ProjectConfigurationValidator.Validate(PlatformDefaults()).EgressBaseline;
        Assert.Equal("api.example.com", Assert.Single(baseline).Destination);

        var narrowed = ProjectConfigurationValidator.ResolveEgress(
            baseline,
            [Fqdn("api.example.com")],
            [Fqdn("API.EXAMPLE.COM.")]);
        Assert.Equal(baseline.ToArray(), narrowed.ToArray());

        var widened = Assert.Throws<ProjectConfigException>(() =>
            ProjectConfigurationValidator.ResolveEgress(
                baseline,
                [Fqdn("other.example.com")],
                []));
        Assert.Equal(StatusCodes.Status400BadRequest, widened.StatusCode);

        var missingRequired = Assert.Throws<ProjectConfigException>(() =>
            ProjectConfigurationValidator.ResolveEgress(
                baseline,
                [],
                [Fqdn("api.example.com")]));
        Assert.Equal(StatusCodes.Status400BadRequest, missingRequired.StatusCode);
    }

    [Fact]
    public void ReadsLegacyRunSelectionEgressWithoutBroadeningItsEffectiveDestinations()
    {
        const string legacySnapshot = """
            {
              "projectId": "project",
              "runId": "run",
              "projectRevision": 1,
              "projectConfigurationRevision": 1,
              "platformRuntimeRevision": 1,
              "contextRevision": "legacy-context",
              "modelSelection": { "reference": "model" },
              "providers": [],
              "egressAllowlist": [
                { "host": "API.Example.com.", "port": 443, "protocol": "tcp" }
              ],
              "runLimits": {},
              "projectConfiguration": {
                "egressNarrowing": [
                  { "host": "API.Example.com.", "port": 443, "protocol": "tcp" }
                ]
              }
            }
            """;

        var selection = ProjectsConfigService.DeserializeRunSelection(legacySnapshot);
        var effectiveRule = Assert.Single(selection.EgressAllowlist);

        Assert.Equal("api.example.com", effectiveRule.Destination);
        Assert.Equal(NetworkEgressPurpose.PublicHttps, effectiveRule.Purpose);
        Assert.Equal(NetworkEgressDestinationKind.Fqdn, effectiveRule.DestinationKind);
        Assert.Equal(selection.EgressAllowlist, selection.EgressBaseline);
        Assert.Equal(selection.EgressAllowlist, selection.ProjectEgressNarrowing);
        Assert.Equal(selection.EgressAllowlist, selection.RequiredEgress);
        Assert.Equal(effectiveRule, Assert.Single(selection.ProjectConfiguration.EgressNarrowing!.Value));
    }

    [Fact]
    public void ProjectRunLimitsCannotExceedPlatformLimits()
    {
        var platform = ProjectConfigurationValidator.Validate(PlatformDefaults());
        var narrowed = ProjectConfigurationValidator.ResolveLimits(platform, new CopilotRunLimitOverrides
        {
            MaxModelTurns = 6,
            MaxChildren = 0,
        });
        Assert.Equal(6, narrowed.MaxModelTurns);
        Assert.Equal(0, narrowed.MaxChildren);
        Assert.Equal(100, narrowed.MaxToolCalls);

        var widening = Assert.Throws<ProjectConfigException>(() =>
            ProjectConfigurationValidator.ResolveLimits(platform, new CopilotRunLimitOverrides
            {
                MaxModelTurns = 13,
            }));
        Assert.Equal(StatusCodes.Status400BadRequest, widening.StatusCode);
    }

    [Fact]
    public void PromptTokenLimitCanBeAbsentButConfiguredValuesRemainBoundedAndNarrowed()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var uncappedPlatform = ProjectConfigurationValidator.Validate(PlatformDefaults() with
        {
            RunLimits = PlatformDefaults().RunLimits with { MaxPromptTokens = null }
        });
        var uncappedSelection = ProjectConfigurationValidator.ResolveLimits(uncappedPlatform, new());
        Assert.Null(uncappedSelection.MaxPromptTokens);
        Assert.DoesNotContain("maxPromptTokens", JsonSerializer.Serialize(uncappedPlatform.RunLimits, options));
        Assert.DoesNotContain("maxPromptTokens", JsonSerializer.Serialize(uncappedSelection, options));
        Assert.Null(JsonSerializer.Deserialize<CopilotRunLimits>(
            JsonSerializer.Serialize(uncappedPlatform.RunLimits, options), options)!.MaxPromptTokens);
        Assert.Null(JsonSerializer.Deserialize<CopilotRunLimits>(
            JsonSerializer.Serialize(uncappedSelection, options), options)!.MaxPromptTokens);

        var addedByProject = ProjectConfigurationValidator.ResolveLimits(
            uncappedPlatform, new() { MaxPromptTokens = 16000 });
        Assert.Equal(16000, addedByProject.MaxPromptTokens);

        var cappedPlatform = ProjectConfigurationValidator.Validate(PlatformDefaults());
        var inheritedLimits = ProjectConfigurationValidator.ResolveLimits(cappedPlatform, new());
        Assert.Equal(20000, inheritedLimits.MaxPromptTokens);
        Assert.Equal(20000, JsonSerializer.Deserialize<CopilotRunLimits>(
            JsonSerializer.Serialize(inheritedLimits, options), options)!.MaxPromptTokens);
        Assert.Equal(16000,
            ProjectConfigurationValidator.ResolveLimits(cappedPlatform, new()
            {
                MaxPromptTokens = 16000
            }).MaxPromptTokens);
        Assert.Throws<ProjectConfigException>(() =>
            ProjectConfigurationValidator.ResolveLimits(cappedPlatform, new()
            {
                MaxPromptTokens = 20001
            }));

        foreach (var invalid in new[] { 0, 1023, 200001 })
        {
            Assert.Throws<ProjectConfigException>(() =>
                ProjectConfigurationValidator.Validate(PlatformDefaults() with
                {
                    RunLimits = PlatformDefaults().RunLimits with { MaxPromptTokens = invalid }
                }));
            Assert.Throws<ProjectConfigException>(() =>
                ProjectConfigurationValidator.ResolveLimits(uncappedPlatform, new()
                {
                    MaxPromptTokens = invalid
                }));
        }
    }

    [Fact]
    public void OptionalCreditAndRevisionLimitsInheritAndCanOnlyNarrowIncludingZero()
    {
        var platform = PlatformDefaults() with
        {
            RunLimits = PlatformDefaults().RunLimits with
            {
                MaxRevisionAttempts = 3,
                CopilotSoftCreditLimit = 4m,
                CopilotHardCreditLimit = 8m
            }
        };
        var inherited = ProjectConfigurationValidator.ResolveLimits(platform, new());
        Assert.Equal(3, inherited.MaxRevisionAttempts);
        Assert.Equal(4m, inherited.CopilotSoftCreditLimit);
        Assert.Equal(8m, inherited.CopilotHardCreditLimit);
        var zero = ProjectConfigurationValidator.ResolveLimits(platform, new()
        {
            MaxRevisionAttempts = 0,
            CopilotSoftCreditLimit = 0,
            CopilotHardCreditLimit = 0
        });
        Assert.Equal(0, zero.MaxRevisionAttempts);
        Assert.Equal(0m, zero.CopilotSoftCreditLimit);
        Assert.Equal(0m, zero.CopilotHardCreditLimit);
        foreach (var widening in new[]
        {
            new CopilotRunLimitOverrides { MaxRevisionAttempts = 4 },
            new CopilotRunLimitOverrides { CopilotSoftCreditLimit = 5 },
            new CopilotRunLimitOverrides { CopilotHardCreditLimit = 9 },
            new CopilotRunLimitOverrides { CopilotHardCreditLimit = 3 },
            new CopilotRunLimitOverrides { MaxRevisionAttempts = -1 },
            new CopilotRunLimitOverrides { CopilotSoftCreditLimit = -1 },
            new CopilotRunLimitOverrides { CopilotHardCreditLimit = -1 }
        })
            Assert.Throws<ProjectConfigException>(() =>
                ProjectConfigurationValidator.ResolveLimits(platform, widening));
    }

    [Fact]
    public void SpendBudgetsInheritAddProjectBudgetsAndCanOnlyNarrowPlatformBudgets()
    {
        var period = new UsageSpendPeriod("2026-09",
            new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        var platformBudget = new SpendBudget("platform-aic", 1, SpendBudgetScopes.Run,
            SdkMeterSources.CopilotNanoAiu, "AIC", 5m, 10m, 1m);
        var platform = PlatformDefaults() with
        {
            RunLimits = PlatformDefaults().RunLimits with { SpendBudgets = [platformBudget] }
        };
        Assert.Equal(new[] { platformBudget }, ProjectConfigurationValidator.ResolveLimits(platform, new()).SpendBudgets!.Value.ToArray());

        var projectBudget = new SpendBudget("azure-month", 1, SpendBudgetScopes.Project,
            SdkMeterSources.ByokTokens, "USD", 50m, 100m, 0.5m) { Period = period };
        var narrowed = platformBudget with { SoftLimit = 2m, HardLimit = 4m };
        var resolved = ProjectConfigurationValidator.ResolveLimits(platform, new()
        {
            SpendBudgets = [narrowed, projectBudget]
        }).SpendBudgets!.Value;
        Assert.Equal(new[] { narrowed, projectBudget }, resolved.ToArray());

        foreach (var invalid in new ImmutableArray<SpendBudget>[]
        {
            [platformBudget with { HardLimit = 11m }],
            [platformBudget with { SoftLimit = null }],
            [platformBudget with { Unit = "USD" }],
            [projectBudget with { Period = null }],
            [projectBudget with { SoftLimit = 101m }],
            [projectBudget, projectBudget with { BudgetId = "duplicate-bucket" }]
        })
            Assert.Throws<ProjectConfigException>(() =>
                ProjectConfigurationValidator.ResolveLimits(platform, new() { SpendBudgets = invalid }));
        Assert.Throws<ProjectConfigException>(() => ProjectConfigurationValidator.ResolveLimits(
            PlatformDefaults() with
            {
                RunLimits = PlatformDefaults().RunLimits with
                {
                    SpendBudgets = [platformBudget with { ReservePerDispatch = 0m }]
                }
            }, new()));
    }

    [Fact]
    public void OptionalLimitsDoNotChangeHistoricalNullSerializationOrAcceptedSnapshotShape()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var historicalPlatform = PlatformDefaults() with
        {
            RunLimits = PlatformDefaults().RunLimits with
            {
                MaxRevisionAttempts = null,
                CopilotSoftCreditLimit = null,
                CopilotHardCreditLimit = null
            }
        };
        var limits = ProjectConfigurationValidator.ResolveLimits(historicalPlatform, new());
        Assert.Equal(
            """{"maxModelTurns":12,"maxToolCalls":100,"maxChildren":4,"maxConcurrentChildren":2,"maxWallTimeSeconds":3600,"maxPromptTokens":20000}""",
            JsonSerializer.Serialize(limits, options));
        Assert.Equal(
            """{"maxModelTurns":null,"maxToolCalls":null,"maxChildren":null,"maxConcurrentChildren":null,"maxWallTimeSeconds":null,"maxPromptTokens":null}""",
            JsonSerializer.Serialize(new CopilotRunLimitOverrides(), options));
        var configured = ProjectConfigurationValidator.ResolveLimits(
            historicalPlatform, new() { MaxRevisionAttempts = 0, CopilotHardCreditLimit = 0 });
        Assert.Contains("\"maxRevisionAttempts\":0", JsonSerializer.Serialize(configured, options));
        Assert.Contains("\"copilotHardCreditLimit\":0", JsonSerializer.Serialize(configured, options));
        Assert.DoesNotContain("copilotSoftCreditLimit", JsonSerializer.Serialize(configured, options));
    }

    [Fact]
    public void RejectsProviderOverridesForNonOverridableCardinalities()
    {
        var error = Assert.Throws<ProjectConfigException>(() =>
            ProjectConfigurationValidator.Validate(new ProjectConfiguration
            {
                ProviderOverrides = [new ProjectProviderOverride(ProviderSeam.Policy, "policy")],
            }));
        Assert.Equal(StatusCodes.Status400BadRequest, error.StatusCode);
    }

    [Fact]
    public void ValidatesOptionalDefaultWorkflowIdentifier()
    {
        var configuration = ProjectConfigurationValidator.Validate(new ProjectConfiguration
        {
            DefaultWorkflowId = "workflow.default",
        });
        Assert.Equal("workflow.default", configuration.DefaultWorkflowId);

        var error = Assert.Throws<ProjectConfigException>(() =>
            ProjectConfigurationValidator.Validate(new ProjectConfiguration
            {
                DefaultWorkflowId = "not a stable identifier",
            }));
        Assert.Equal(StatusCodes.Status400BadRequest, error.StatusCode);
    }

    [Fact]
    public void CatalogOnlySkillsMustBeDisabledUntilImportedAndAssigned()
    {
        var disabled = ProjectConfigurationValidator.Validate(new ProjectConfiguration
        {
            Skills = [new SkillCatalogSetting("legacy-skill", false, 0)],
        });
        Assert.False(Assert.Single(disabled.Skills).Enabled);

        var error = Assert.Throws<ProjectConfigException>(() =>
            ProjectConfigurationValidator.Validate(new ProjectConfiguration
            {
                Skills = [new SkillCatalogSetting("legacy-skill", true, 0)],
            }));
        Assert.Equal(StatusCodes.Status400BadRequest, error.StatusCode);
    }

    [Fact]
    public void ConfigurationTransitionsPreserveOnlyExactlyUnchangedLegacyEnabledSkills()
    {
        var legacy = new SkillCatalogSetting("legacy-skill", true, 0);
        var previous = new ProjectConfiguration { Skills = [legacy] };
        var unchanged = ProjectConfigurationValidator.ValidateTransition(
            previous,
            previous with { DefaultWorkflowId = "workflow.next" });
        Assert.Equal(new[] { legacy }, unchanged.Skills);

        var added = Assert.Throws<ProjectConfigException>(() =>
            ProjectConfigurationValidator.ValidateTransition(
                previous,
                previous with
                {
                    Skills = [legacy, new SkillCatalogSetting("new-legacy-skill", true, 1)],
                }));
        Assert.Equal(StatusCodes.Status400BadRequest, added.StatusCode);

        var modified = Assert.Throws<ProjectConfigException>(() =>
            ProjectConfigurationValidator.ValidateTransition(
                previous,
                previous with { Skills = [legacy with { Order = 1 }] }));
        Assert.Equal(StatusCodes.Status400BadRequest, modified.StatusCode);
    }

    [Fact]
    public void SourceControlSettingsAreOptionalAndPersistOnlyExactSecretReferences()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var legacy = ProjectConfigurationValidator.Validate(new ProjectConfiguration());
        var legacyJson = JsonSerializer.Serialize(legacy, options);
        Assert.DoesNotContain("\"sourceControl\"", legacyJson, StringComparison.Ordinal);

        var settings = new SourceControlProjectSettings(
            new SourceControlRepositoryIdentity("octo", "repo"),
            new SecretRef("github-api", "v3"),
            new SecretRef("github-checkout", "v2"),
            new SecretRef("github-webhook", "v1"));
        var configuration = ProjectConfigurationValidator.Validate(new ProjectConfiguration
        {
            SourceControl = settings,
        });
        var json = JsonSerializer.Serialize(configuration, options);

        Assert.Equal(settings, configuration.SourceControl);
        Assert.Contains("\"repository\":{\"owner\":\"octo\",\"name\":\"repo\"}", json, StringComparison.Ordinal);
        Assert.Contains("\"apiSecretReference\":{\"id\":\"github-api\",\"version\":\"v3\"}", json, StringComparison.Ordinal);
        Assert.Contains("\"checkoutSecretReference\":{\"id\":\"github-checkout\",\"version\":\"v2\"}", json, StringComparison.Ordinal);
        Assert.Contains("\"webhookSecretReference\":{\"id\":\"github-webhook\",\"version\":\"v1\"}", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"authMode\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"identityConnectionId\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("tokenValue", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReviewedRemoteToolReferencesAreOptionalAndKeepNullReferencesOmitted()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var legacy = ProjectConfigurationValidator.Validate(new ProjectConfiguration());
        var legacyJson = JsonSerializer.Serialize(legacy, options);
        Assert.Equal(
            """{"modelSelection":null,"modelTaskOverrides":[],"modelRoleOverrides":[],"modelAgentOverrides":[],"providerOverrides":[],"orderedProviderOverrides":[],"agentCharters":[],"casting":[],"blueprintWorkflowReferences":[],"defaultWorkflowId":null,"skills":[],"egressNarrowing":null,"runLimits":{"maxModelTurns":null,"maxToolCalls":null,"maxChildren":null,"maxConcurrentChildren":null,"maxWallTimeSeconds":null,"maxPromptTokens":null}}""",
            legacyJson);

        var reference = new ReviewedRemoteToolSnapshotReference(
            "project-1",
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            new string('a', 64),
            "agent-1",
            "node-1");
        var configuration = ProjectConfigurationValidator.Validate(new ProjectConfiguration
        {
            ReviewedRemoteToolSnapshots = ImmutableArray.Create(reference),
        });
        var serialized = JsonSerializer.Serialize(configuration, options);
        var restored = JsonSerializer.Deserialize<ProjectConfiguration>(serialized, options)!;
        var restoredReference = Assert.Single(restored.ReviewedRemoteToolSnapshots!.Value);

        Assert.Equal(reference.ProjectId, restoredReference.ProjectId);
        Assert.Equal(reference.SnapshotId, restoredReference.SnapshotId);
        Assert.Equal(reference.SnapshotDigest, restoredReference.SnapshotDigest);
        Assert.Equal(reference.AgentId, restoredReference.AgentId);
        Assert.Equal(reference.NodeId, restoredReference.NodeId);
    }

    [Fact]
    public void ReviewedRemoteToolReferencesRejectDefaultNullAndDuplicateEntries()
    {
        var reference = new ReviewedRemoteToolSnapshotReference(
            "project-1",
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            new string('a', 64),
            "agent-1",
            "node-1");
        Assert.Throws<ProjectConfigException>(() => ProjectConfigurationValidator.Validate(
            new ProjectConfiguration
            {
                ReviewedRemoteToolSnapshots =
                    (ImmutableArray<ReviewedRemoteToolSnapshotReference>?)default(
                        ImmutableArray<ReviewedRemoteToolSnapshotReference>),
            }));
        Assert.Throws<ProjectConfigException>(() => ProjectConfigurationValidator.Validate(
            new ProjectConfiguration
            {
                ReviewedRemoteToolSnapshots =
                    ImmutableArray.Create<ReviewedRemoteToolSnapshotReference>(
                        new ReviewedRemoteToolSnapshotReference[] { null! }),
            }));
        Assert.Throws<ProjectConfigException>(() => ProjectConfigurationValidator.Validate(
            new ProjectConfiguration
            {
                ReviewedRemoteToolSnapshots = ImmutableArray.Create(reference, reference),
            }));
    }

    [Fact]
    public void GitHubAppSettingsUseStableIdentityConnectionWithoutPersistedApiOrCheckoutSecrets()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var settings = new SourceControlProjectSettings(
            new SourceControlRepositoryIdentity("octo", "repo"),
            apiSecretReference: null,
            authMode: SourceControlAuthMode.GitHubApp,
            identityConnectionId: "app-conn_123");

        var configuration = ProjectConfigurationValidator.Validate(new ProjectConfiguration
        {
            SourceControl = settings,
        });
        var json = JsonSerializer.Serialize(configuration, options);
        var restored = JsonSerializer.Deserialize<ProjectConfiguration>(json, options)!.SourceControl!;

        Assert.Equal(SourceControlAuthMode.GitHubApp, restored.AuthMode);
        Assert.Equal("app-conn_123", restored.IdentityConnectionId);
        Assert.Null(restored.ApiSecretReference);
        Assert.Null(restored.CheckoutSecretReference);
        Assert.Contains("\"authMode\":\"githubApp\"", json, StringComparison.Ordinal);
        Assert.Contains("\"identityConnectionId\":\"app-conn_123\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("identityRepositorySelectionCode", json, StringComparison.Ordinal);
        Assert.DoesNotContain("apiSecretReference", json, StringComparison.Ordinal);
        Assert.DoesNotContain("checkoutSecretReference", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"sourceControl":{"repository":{"owner":"octo","name":"repo"},"authMode":"githubApp","identityConnectionId":"app-conn_123","selectionCode":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}}""")]
    [InlineData("""{"sourceControl":{"repository":{"owner":"octo","name":"repo"},"authMode":"githubApp","identityConnectionId":"app-conn_123","identityRepositorySelectionCode":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}}""")]
    public void RejectsTransientSelectionCodeFromProjectConfiguration(string configuration)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ProjectConfiguration>(configuration, options));
    }

    [Fact]
    public void LegacySettingsRoundTripWithoutAddingAuthModeOrConnectionToAcceptedJson()
    {
        const string legacy = """
            {"repository":{"owner":"octo","name":"repo"},
             "apiSecretReference":{"id":"github-api","version":"v3"}}
            """;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var settings = JsonSerializer.Deserialize<SourceControlProjectSettings>(legacy, options)!;
        var serialized = JsonSerializer.Serialize(settings, options);

        Assert.Null(settings.AuthMode);
        Assert.Null(settings.IdentityConnectionId);
        Assert.Equal(
            """{"repository":{"owner":"octo","name":"repo"},"apiSecretReference":{"id":"github-api","version":"v3"}}""",
            serialized);
    }

    [Fact]
    public void RejectsMismatchedOrUnknownSourceControlAuthBindings()
    {
        var repository = new SourceControlRepositoryIdentity("octo", "repo");
        Assert.Throws<ArgumentException>(() => new SourceControlProjectSettings(
            repository,
            apiSecretReference: null,
            authMode: SourceControlAuthMode.GitHubApp));
        Assert.Throws<ArgumentException>(() => new SourceControlProjectSettings(
            repository,
            new SecretRef("github-api", "v3"),
            authMode: SourceControlAuthMode.GitHubApp,
            identityConnectionId: "app-conn"));
        Assert.Throws<ArgumentException>(() => new SourceControlProjectSettings(
            repository,
            null,
            checkoutSecretReference: new SecretRef("github-checkout", "v2"),
            authMode: SourceControlAuthMode.GitHubApp,
            identityConnectionId: "app-conn"));
        Assert.Throws<ArgumentException>(() => new SourceControlProjectSettings(
            repository,
            new SecretRef("github-api", "v3"),
            identityConnectionId: "app-conn"));
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<SourceControlProjectSettings>(
                """{"repository":{"owner":"octo","name":"repo"},"apiSecretReference":{"id":"api","version":"v1"},"authMode":"unexpected"}""",
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    [Fact]
    public void RejectsNullConfigurationEntriesAsClientErrors()
    {
        var providerError = Assert.Throws<ProjectConfigException>(() =>
            ProjectConfigurationValidator.Validate(new ProjectConfiguration
            {
                ProviderOverrides = ImmutableArray.CreateRange(new ProjectProviderOverride[] { null! }),
            }));
        Assert.Equal(StatusCodes.Status400BadRequest, providerError.StatusCode);

        var charterError = Assert.Throws<ProjectConfigException>(() =>
            ProjectConfigurationValidator.Validate(new ProjectConfiguration
            {
                AgentCharters = ImmutableArray.CreateRange(new ProjectAgentCharter[] { null! }),
            }));
        Assert.Equal(StatusCodes.Status400BadRequest, charterError.StatusCode);
    }

    [Fact]
    public void LoadsProviderCatalogSnapshotFromCatalogOwnerConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ProjectsConfig:ProviderCatalog:Registrations:0:Seam"] = "Sandbox",
                ["ProjectsConfig:ProviderCatalog:Registrations:0:Id"] = "sandbox-platform",
                ["ProjectsConfig:ProviderCatalog:Registrations:0:AdapterVersion"] = "1.0.0",
                ["ProjectsConfig:ProviderCatalog:Registrations:0:OptionsSchemaVersion"] = "1",
                ["ProjectsConfig:ProviderCatalog:Registrations:0:Hosting"] = "KubernetesController",
                ["ProjectsConfig:ProviderCatalog:Registrations:0:AdvertisedCapabilities:0"] = "container.create",
                ["ProjectsConfig:ProviderCatalog:Registrations:0:Enabled"] = "true",
                ["ProjectsConfig:ProviderCatalog:Registrations:0:OptionsRevision"] = "options-v1",
                ["ProjectsConfig:ProviderCatalog:Defaults:0:Seam"] = "Sandbox",
                ["ProjectsConfig:ProviderCatalog:Defaults:0:ProviderId"] = "sandbox-platform",
            })
            .Build();

        var catalog = ProviderCatalogConfiguration.Load(configuration);
        var result = new ProviderResolver(catalog).Resolve(new ProviderResolutionRequest(
            ProviderSeam.Sandbox,
            null,
            new Version(1, 0, 0),
            1,
            ImmutableHashSet.Create(StringComparer.Ordinal, "container.create")));
        Assert.True(result.IsSuccess);
        Assert.Equal("options-v1", result.Value!.Candidate!.OptionsRevision);
    }
}
