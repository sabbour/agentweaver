using System.Security.Cryptography;
using System.Text;

namespace Agentweaver.Domain;

/// <summary>Canonical permission families enforced for every agent tool call.</summary>
public static class EffectivePermissionOperations
{
    public const string Observe = "observe";
    public const string WorkspaceRead = "workspace.read";
    public const string WorkspaceSearch = "workspace.search";
    public const string WorkspaceWrite = "workspace.write";
    public const string ShellExecute = "shell.execute";
    public const string NetworkAccess = "network.access";
    public const string AgentweaverRead = "agentweaver.read";
    public const string AgentweaverWrite = "agentweaver.write";
    public const string PreviewManage = "preview.manage";
    public const string HumanInteraction = "human.interaction";

    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.Ordinal)
    {
        Observe,
        WorkspaceRead,
        WorkspaceSearch,
        WorkspaceWrite,
        ShellExecute,
        NetworkAccess,
        AgentweaverRead,
        AgentweaverWrite,
        PreviewManage,
        HumanInteraction,
    };
}

/// <summary>
/// Versioned, credential-free permission contract bound to one run lifecycle generation.
/// A binding is an execution ceiling: refreshes and parent bindings may narrow it, never widen it.
/// </summary>
public sealed record EffectivePermissionBinding
{
    public const int CurrentSchemaVersion = 1;

    public required int SchemaVersion { get; init; }
    public required string BindingId { get; init; }
    public required string Version { get; init; }
    public required string Source { get; init; }
    public required string RunId { get; init; }
    public required int Attempt { get; init; }
    public required string Scope { get; init; }
    public required IReadOnlyList<string> AllowedOperations { get; init; }
    public required SandboxPolicy Policy { get; init; }
    public string? ParentBindingId { get; init; }
    public string? ParentVersion { get; init; }

    public bool Allows(string operation) =>
        AllowedOperations.Contains(operation, StringComparer.Ordinal);

    public void Validate(string runId, int attempt)
    {
        if (SchemaVersion != CurrentSchemaVersion)
            throw new EffectivePermissionBindingException(
                $"Unsupported effective permission binding schema version '{SchemaVersion}'.");
        if (string.IsNullOrWhiteSpace(BindingId)
            || string.IsNullOrWhiteSpace(Version)
            || string.IsNullOrWhiteSpace(Source)
            || string.IsNullOrWhiteSpace(Scope))
        {
            throw new EffectivePermissionBindingException(
                "Effective permission binding identity, version, source, and scope are required.");
        }
        if (!string.Equals(RunId, runId, StringComparison.Ordinal) || Attempt != attempt)
            throw new EffectivePermissionBindingException(
                "Effective permission binding does not match the active run and attempt.");
        if (AllowedOperations.Count != AllowedOperations.Distinct(StringComparer.Ordinal).Count()
            || AllowedOperations.Any(operation => !EffectivePermissionOperations.Known.Contains(operation)))
        {
            throw new EffectivePermissionBindingException(
                "Effective permission binding contains duplicate or unsupported operations.");
        }
        if (Policy is null)
            throw new EffectivePermissionBindingException(
                "Effective permission binding is missing its sandbox policy.");

        var expectedPolicyVersion = $"sha256:{ComputePolicyVersion(Policy, AllowedOperations)}";
        if (!string.Equals(Version, expectedPolicyVersion, StringComparison.Ordinal))
            throw new EffectivePermissionBindingException(
                "Effective permission binding version does not match its policy payload.");
        var expectedBindingId = ComputeBindingId(
            RunId,
            Attempt,
            Source,
            Scope,
            Version,
            ParentBindingId,
            ParentVersion);
        if (!string.Equals(BindingId, expectedBindingId, StringComparison.Ordinal))
            throw new EffectivePermissionBindingException(
                "Effective permission binding identity does not match its payload.");
    }

    public static EffectivePermissionBinding Create(
        string runId,
        int attempt,
        string source,
        string scope,
        SandboxPolicy policy,
        EffectivePermissionBinding? parent = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentNullException.ThrowIfNull(policy);

        var allowed = ResolveAllowedOperations(policy);
        var effectivePolicy = policy;
        if (parent is not null)
        {
            allowed.IntersectWith(parent.AllowedOperations);
            effectivePolicy = IntersectPolicy(policy, parent.Policy);
        }

        var policyVersion = ComputePolicyVersion(effectivePolicy, allowed);
        var version = $"sha256:{policyVersion}";
        var effectiveSource = parent is null ? source : $"{source}+parent";

        return new EffectivePermissionBinding
        {
            SchemaVersion = CurrentSchemaVersion,
            BindingId = ComputeBindingId(
                runId,
                attempt,
                effectiveSource,
                scope,
                version,
                parent?.BindingId,
                parent?.Version),
            Version = version,
            Source = effectiveSource,
            RunId = runId,
            Attempt = attempt,
            Scope = scope,
            AllowedOperations = [.. allowed.Order(StringComparer.Ordinal)],
            Policy = effectivePolicy,
            ParentBindingId = parent?.BindingId,
            ParentVersion = parent?.Version,
        };
    }

    public static EffectivePermissionBinding Intersect(
        EffectivePermissionBinding current,
        EffectivePermissionBinding ceiling)
    {
        current.Validate(current.RunId, current.Attempt);
        ceiling.Validate(current.RunId, current.Attempt);
        var allowed = new HashSet<string>(current.AllowedOperations, StringComparer.Ordinal);
        allowed.IntersectWith(ceiling.AllowedOperations);
        var policy = IntersectPolicy(current.Policy, ceiling.Policy);
        var version = ComputePolicyVersion(policy, allowed);

        var source = $"{current.Source}+ceiling";
        var versionValue = $"sha256:{version}";
        return current with
        {
            BindingId = ComputeBindingId(
                current.RunId,
                current.Attempt,
                source,
                current.Scope,
                versionValue,
                ceiling.BindingId,
                ceiling.Version),
            Version = versionValue,
            Source = source,
            AllowedOperations = [.. allowed.Order(StringComparer.Ordinal)],
            Policy = policy,
            ParentBindingId = ceiling.BindingId,
            ParentVersion = ceiling.Version,
        };
    }

    private static HashSet<string> ResolveAllowedOperations(SandboxPolicy policy)
    {
        var allowed = policy.AllowedOperations is { Count: > 0 }
            ? new HashSet<string>(policy.AllowedOperations, StringComparer.Ordinal)
            : new HashSet<string>(EffectivePermissionOperations.Known, StringComparer.Ordinal);

        if (!policy.ShellEnabled)
            allowed.Remove(EffectivePermissionOperations.ShellExecute);
        if (!policy.NetworkEnabled)
            allowed.Remove(EffectivePermissionOperations.NetworkAccess);
        return allowed;
    }

    private static SandboxPolicy IntersectPolicy(SandboxPolicy current, SandboxPolicy ceiling)
    {
        var roots = current.AllowedRepositoryRoots.Count == 0 || ceiling.AllowedRepositoryRoots.Count == 0
            ? Array.Empty<string>()
            : current.AllowedRepositoryRoots
                .Intersect(ceiling.AllowedRepositoryRoots, StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();

        return current with
        {
            ShellEnabled = current.ShellEnabled && ceiling.ShellEnabled,
            Direct = current.Direct && ceiling.Direct,
            NetworkEnabled = current.NetworkEnabled && ceiling.NetworkEnabled,
            AllowedRepositoryRoots = roots,
            DestructiveCommandPatterns =
            [
                .. current.DestructiveCommandPatterns
                    .Concat(ceiling.DestructiveCommandPatterns)
                    .Distinct(StringComparer.Ordinal),
            ],
            RequireApprovalForAllShell =
                current.RequireApprovalForAllShell || ceiling.RequireApprovalForAllShell,
            RedactPii = current.RedactPii || ceiling.RedactPii,
            MaxOutputBytes = Math.Min(current.MaxOutputBytes, ceiling.MaxOutputBytes),
            AllowedOperations = null,
        };
    }

    private static string ComputePolicyVersion(
        SandboxPolicy policy,
        IEnumerable<string> allowedOperations)
    {
        var canonical = string.Join(
            "\n",
            policy.RepositoryPath,
            policy.ShellEnabled,
            policy.Direct,
            policy.NetworkEnabled,
            string.Join("|", policy.AllowedRepositoryRoots.Order(StringComparer.Ordinal)),
            string.Join("|", policy.DestructiveCommandPatterns.Order(StringComparer.Ordinal)),
            policy.RequireApprovalForAllShell,
            policy.RedactPii,
            policy.MaxOutputBytes,
            string.Join("|", allowedOperations.Order(StringComparer.Ordinal)));
        return Hash(canonical);
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string ComputeBindingId(
        string runId,
        int attempt,
        string source,
        string scope,
        string version,
        string? parentBindingId,
        string? parentVersion) =>
        $"epb-{Hash(
            $"{CurrentSchemaVersion}\n{runId}\n{attempt}\n{source}\n{scope}\n{version}\n" +
            $"{parentBindingId}\n{parentVersion}")}";
}

public sealed class EffectivePermissionBindingException : InvalidOperationException
{
    public EffectivePermissionBindingException(string message) : base(message) { }
    public EffectivePermissionBindingException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>Resolves the current effective binding for a run.</summary>
public interface IEffectivePermissionBindingProvider
{
    Task<EffectivePermissionBinding> ResolveAsync(
        string runId,
        string repositoryPath,
        EffectivePermissionBinding? ceiling = null,
        CancellationToken ct = default);

    Task<EffectivePermissionBinding> ResolveForInspectionAsync(
        string runId,
        string repositoryPath,
        CancellationToken ct = default) =>
        ResolveAsync(runId, repositoryPath, ceiling: null, ct);
}
