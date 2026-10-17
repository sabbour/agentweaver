using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using GitHub.Copilot;

namespace Agentweaver.AgentRuntime;

#pragma warning disable GHCP001 // The session skill provider is pinned to SDK 1.0.18.
/// <summary>
/// Adapts the runtime's accepted skill projection to the pinned Copilot SDK skill interface.
/// Runtime names are hashed from stable skill IDs so public names do not expose source IDs.
/// </summary>
internal sealed class RuntimeAcceptedSkillProvider : ISkillProvider
{
    private readonly ImmutableArray<(string Name, SkillRuntimeContentV1 Skill)> _skills;
    private readonly ImmutableDictionary<string, SkillRuntimeContentV1> _byName;

    /// <summary>Validates and indexes the immutable skill projection for the current session.</summary>
    /// <param name="projection">Owner-projected content bound to the active runtime registration.</param>
    /// <exception cref="RuntimeAuthorizationException">Two IDs map to the same SDK skill name.</exception>
    public RuntimeAcceptedSkillProvider(SkillRuntimeContentProjectionV1 projection)
    {
        SkillRuntimeContentContract.ValidateProjection(projection);
        var skills = ImmutableArray.CreateBuilder<(string Name, SkillRuntimeContentV1 Skill)>(
            projection.Skills.Length);
        var byName = ImmutableDictionary.CreateBuilder<string, SkillRuntimeContentV1>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var skill in projection.Skills)
        {
            var name = RuntimeName(skill.SkillId);
            if (!byName.TryAdd(name, skill))
                throw new RuntimeAuthorizationException("runtime_skill_identity_conflict");
            skills.Add((name, skill));
        }
        _skills = skills.ToImmutable();
        _byName = byName.ToImmutable();
    }

    /// <summary>Returns SDK descriptors for the accepted skills without fetching external content.</summary>
    public Task<IReadOnlyList<SkillProviderDescriptor>> ListSkillsAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<SkillProviderDescriptor> descriptors = _skills
            .Select(item => new SkillProviderDescriptor
            {
                Name = item.Name,
                Description = item.Skill.Description
            })
            .ToArray();
        return Task.FromResult(descriptors);
    }

    /// <summary>Returns instructions and workspace resource paths for an exact runtime skill name.</summary>
    /// <returns>Skill text, or null when the requested skill is not in the accepted projection.</returns>
    public Task<string?> ReadSkillAsync(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_byName.TryGetValue(name, out var skill))
            return Task.FromResult<string?>(null);
        if (skill.Resources.IsEmpty)
            return Task.FromResult<string?>(skill.Instructions);

        var resourceDirectory = ResourceDirectory(name);
        var resources = string.Join('\n', skill.Resources.Select(resource =>
            $"- `{resource.RelativePath}`: `/workspace/{resourceDirectory}/{resource.RelativePath}`"));
        return Task.FromResult<string?>(
            $"{skill.Instructions}\n\nBundled resources from revision {skill.Revision}:\n{resources}");
    }

    /// <summary>Derives a stable SDK-safe identifier without embedding the source skill ID.</summary>
    internal static string RuntimeName(string skillId)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(skillId)))
            .ToLowerInvariant();
        return "skill-" + digest[..56];
    }

    /// <summary>Returns the workspace-relative directory used for pinned skill resources.</summary>
    internal static string ResourceDirectory(string runtimeName) =>
        $".agentweaver/skills/{runtimeName}";
}
#pragma warning restore GHCP001
