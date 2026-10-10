using System.Text.Json;
using System.Text.Json.Nodes;
using Agentweaver.Abstractions;
using Agentweaver.Environment;
using Agentweaver.Environment.Migrations;
using Agentweaver.Providers.Sandbox.AgentSandbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Configuration;

namespace Agentweaver.Environment.Tests;

public sealed class SandboxBuildTestProductionWiringTests
{
    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(true, "AllowedExecutables")]
    [InlineData(true, "CollectorAssemblyArguments")]
    public void ProductionConfigurationAdvertisesBuildTestOnlyForItsPinnedProfile(
        bool configured, string? missingField)
    {
        var profile = new SandboxBuildTestAcceptedExecutionOptions(
            "offline-build",
            "registry.example/build@sha256:" + new string('a', 64),
            "linux/amd64",
            ["/usr/bin/make"],
            "1",
            "512Mi",
            "1Gi",
            60,
            4096,
            1024,
            SandboxBuildTestLimits.OfflineEgressProfile,
            "registry.example/collector@sha256:" + new string('b', 64),
            "linux/amd64",
            SandboxBuildTestLimits.OutputCollectorExecutable,
            [SandboxBuildTestLimits.OutputCollectorAssembly],
            SandboxBuildTestLimits.OutputCollectorMode,
            SandboxBuildTestLimits.OutputCollectorContainerName).Validate();
        var options = new AgentSandboxOptions(
            AgentSandboxOptions.CurrentOptionsSchemaVersion,
            "sandbox-options-1",
            "agentweaver",
            "azure-files-csi",
            "ghcr.io/agentweaver/agenthost@sha256:" + new string('c', 64),
            "linux/amd64",
            1,
            "kata-vm",
            "kata-qemu",
            "500m",
            "512Mi",
            1,
            1,
            new(30, 90, 30, 30, 30, 180))
        {
            AcceptedBuildTestProfile = configured ? profile : null
        };
        var document = JsonSerializer.SerializeToNode(new
        {
            Environment = new { Sandbox = new { AgentSandbox = options } }
        })!;
        if (missingField is not null)
        {
            var configuredProfile = document["Environment"]!["Sandbox"]!["AgentSandbox"]!
                ["AcceptedBuildTestProfile"]!.AsObject();
            Assert.True(configuredProfile.Remove(missingField));
        }
        using var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(document));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();

        if (missingField is not null)
        {
            Assert.Throws<InvalidOperationException>(() => global::Program.ReadSandboxOptions(configuration));
            return;
        }

        var loaded = global::Program.ReadSandboxOptions(configuration);
        var registration = AgentSandboxProviderMetadata.CreateRegistration(loaded);

        Assert.Equal(configured,
            registration.Descriptor.AdvertisedCapabilities.Contains(SandboxCapabilities.BuildTestCommandPod));
        if (configured)
        {
            Assert.NotNull(loaded.AcceptedBuildTestProfile);
            Assert.Equal(
                SandboxBuildTestBindingPreparation.ComputeExecutionOptionsSha256(profile),
                SandboxBuildTestBindingPreparation.ComputeExecutionOptionsSha256(loaded.AcceptedBuildTestProfile));
        }
        else
        {
            Assert.Null(loaded.AcceptedBuildTestProfile);
        }
    }

    [Fact]
    public void BuildTestMigrationConstraintsMatchTheOwnerEffectsModel()
    {
        var options = new DbContextOptionsBuilder<EnvironmentDbContext>()
            .UseNpgsql("Host=unused.invalid;Database=unused;Username=unused")
            .Options;
        using var context = new EnvironmentDbContext(options);
        var model = context.GetService<IDesignTimeModel>().Model;
        var entity = model.FindEntityType(typeof(EnvironmentOwnerEffectRow));
        Assert.NotNull(entity);
        var constraints = entity.GetCheckConstraints().ToDictionary(constraint =>
            constraint.Name ?? throw new InvalidOperationException("The owner-effects constraint must be named."));
        var migrationConstraints = new SandboxBuildTestCommands().UpOperations
            .OfType<AddCheckConstraintOperation>().ToArray();

        Assert.Equal(2, migrationConstraints.Length);
        foreach (var constraint in migrationConstraints)
        {
            Assert.Equal(EnvironmentDbContext.Schema, constraint.Schema);
            Assert.Equal("owner_effects", constraint.Table);
            Assert.Equal(constraints[constraint.Name].Sql, constraint.Sql);
        }
        Assert.Contains("'BuildTestCommand'", constraints["ck_environment_owner_effects_kind"].Sql);
    }
}
