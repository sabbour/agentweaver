using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using GitHub.Copilot;

namespace Agentweaver.AgentRuntime;

#pragma warning disable GHCP001 // The session skill provider is pinned to SDK 1.0.18.
internal sealed class RuntimeAcceptedSkillProvider : ISkillProvider
{
    private readonly ImmutableArray<(string Name, SkillRuntimeContentV1 Skill)> _skills;
    private readonly ImmutableDictionary<string, SkillRuntimeContentV1> _byName;

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

    internal static string RuntimeName(string skillId)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(skillId)))
            .ToLowerInvariant();
        return "skill-" + digest[..56];
    }

    internal static string ResourceDirectory(string runtimeName) =>
        $".agentweaver/skills/{runtimeName}";
}
#pragma warning restore GHCP001
