using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Projects.Config.BlueprintPackages;

namespace Agentweaver.Projects.Config;

/// <summary>A 0.x <c>blueprint-package-v1</c> package read as converter input.</summary>
public sealed record BlueprintPackageV1Content(
    string PackageId,
    string Version,
    BlueprintDefinition Blueprint,
    ImmutableDictionary<string, string> RoleDefaultModels,
    BlueprintPackageDigests Digests);

/// <summary>
/// Reads 0.x <c>blueprint-package-v1</c> packages for the build-time AgentSuite converter. Package rules come from
/// the ported 0.x <see cref="BlueprintPackageValidator"/>, so a package is accepted or rejected exactly as in 0.x.
/// It is not a runtime loader; AgentSuite import is the only runtime format.
/// </summary>
public static class BlueprintPackageV1
{
    private static readonly JsonSerializerOptions PayloadJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        AllowDuplicateProperties = false,
    };

    /// <summary>
    /// Validates a package with the 0.x rules and returns its blueprint. Package roles become bespoke roles with the
    /// 0.x charter text (summary, responsibilities and boundaries), and their <c>default_model</c> values are returned
    /// per role.
    /// </summary>
    /// <param name="manifest">The exact <c>manifest.json</c> bytes.</param>
    /// <param name="payloads">Payload bytes keyed by package-relative path.</param>
    /// <param name="containerSha256">The optional digest of the container the package came from.</param>
    /// <exception cref="AgentSuiteConversionException">
    /// The package fails a 0.x rule, or holds workflow or skill definitions that the converter doesn't convert yet.
    /// </exception>
    public static BlueprintPackageV1Content Read(
        ReadOnlySpan<byte> manifest,
        IReadOnlyDictionary<string, byte[]> payloads,
        string? containerSha256 = null)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        var validation = BlueprintPackageValidator.Validate(new BlueprintPackageSource(manifest, payloads, containerSha256));
        if (!validation.IsValid)
            throw new AgentSuiteConversionException(validation.Errors);
        var package = validation.Package!;
        var definitions = package.Manifest.Definitions;
        var errors = new List<string>();

        var blueprints = definitions.Where(item => item.Kind == BlueprintPackageDefinitionKind.Blueprint).ToList();
        if (blueprints.Count != 1)
            throw new AgentSuiteConversionException(["A package must contain exactly one blueprint definition."]);
        foreach (var definition in definitions.Where(item =>
            item.Kind is BlueprintPackageDefinitionKind.Workflow or BlueprintPackageDefinitionKind.Skill))
            errors.Add(definition.Kind == BlueprintPackageDefinitionKind.Workflow
                ? $"Workflow definition '{definition.Id}' can't be converted yet: v1 workflows come from the Orchestrator workflow library."
                : $"Skill definition '{definition.Id}' can't be converted yet: v1 skills install through the project skill catalog.");

        var blueprint = Deserialize<BlueprintDefinition>(payloads[blueprints[0].Path], blueprints[0].Path, errors);
        if (blueprint is not null && !string.Equals(blueprint.Id, blueprints[0].Id, StringComparison.Ordinal))
            errors.Add($"Blueprint payload id '{blueprint.Id}' must match definition id '{blueprints[0].Id}'.");

        var bespoke = ImmutableArray.CreateBuilder<BlueprintBespokeRole>();
        var models = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var definition in definitions.Where(item => item.Kind == BlueprintPackageDefinitionKind.Role))
        {
            var role = Deserialize<PackageRole>(payloads[definition.Path], definition.Path, errors);
            if (role is null)
                continue;
            if (!string.Equals(role.Id, definition.Id, StringComparison.Ordinal))
            {
                errors.Add($"Role payload id '{role.Id}' must match definition id '{definition.Id}'.");
                continue;
            }
            bespoke.Add(new BlueprintBespokeRole
            {
                Id = role.Id,
                Title = role.Title,
                Charter = ProjectCastingCatalog.BuildCharter(new ProjectCastingRoleDefinition(
                    role.Id, role.Title, role.Summary, role.Responsibilities, role.Boundaries)),
            });
            if (!string.IsNullOrWhiteSpace(role.DefaultModel))
                models[role.Id] = role.DefaultModel;
        }

        if (errors.Count > 0 || blueprint is null)
            throw new AgentSuiteConversionException(errors.ToImmutableArray());

        var merged = blueprint with
        {
            BespokeRoles = (blueprint.BespokeRoles.IsDefault ? [] : blueprint.BespokeRoles).AddRange(bespoke),
        };
        return new(package.Manifest.PackageId, package.Manifest.Version.ToString(), merged, models.ToImmutable(), package.Digests);
    }

    private static T? Deserialize<T>(byte[] bytes, string path, List<string> errors) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, PayloadJson)
                ?? throw new JsonException("The payload is empty.");
        }
        catch (JsonException exception)
        {
            errors.Add($"JSON payload is invalid ({path}): {exception.Message}");
            return null;
        }
    }

    /// <summary>The 0.x role definition payload.</summary>
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record PackageRole
    {
        public string Id { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string Summary { get; init; } = string.Empty;
        public string? DefaultModel { get; init; }
        public ImmutableArray<string> Capabilities { get; init; } = [];
        public ImmutableArray<string> Responsibilities { get; init; } = [];
        public ImmutableArray<string> Boundaries { get; init; } = [];
    }
}
