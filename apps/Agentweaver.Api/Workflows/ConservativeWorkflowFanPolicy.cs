using System.Text.RegularExpressions;

namespace Agentweaver.Api.Workflows;

internal sealed record ConservativeWorkflowFanResult(
    WorkflowDefinition Workflow,
    bool WasNormalized,
    string? NormalizationReason);

/// <summary>
/// Enforces the deliberately narrow generated-fan contract until complete-input and conflict
/// handling exists. Scope-unsafe but structurally valid generated fans are deterministically
/// linearized in declared branch order. Dependency-bearing and malformed fans fail validation rather
/// than being guessed into a new graph.
/// </summary>
internal static partial class ConservativeWorkflowFanPolicy
{
    private static readonly HashSet<string> BroadScopes = new(StringComparer.OrdinalIgnoreCase)
    {
        ".",
        "repo",
        "repository",
        "workspace",
        "source",
        "src",
        "docs",
        "documentation",
        "apps",
        "packages",
        "tests",
    };

    private static readonly HashSet<string> SharedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "package.json",
        "package-lock.json",
        "npm-shrinkwrap.json",
        "pnpm-lock.yaml",
        "yarn.lock",
        "packages.lock.json",
        "nuget.config",
        "requirements.txt",
        "pyproject.toml",
        "poetry.lock",
        "go.mod",
        "go.sum",
        "cargo.toml",
        "cargo.lock",
        "composer.json",
        "composer.lock",
        "gemfile",
        "gemfile.lock",
        "global.json",
        "directory.build.props",
        "directory.build.targets",
        "directory.packages.props",
    };

    private static readonly HashSet<string> SupportedContentExtensions = new(
        [".adoc", ".csv", ".markdown", ".md", ".rst", ".tsv", ".txt"],
        StringComparer.OrdinalIgnoreCase);

    private sealed record FanSafetyIssue(string Reason, bool PreventLinearization);

    public static bool TryGetRequiredFanOutputPaths(
        string description,
        out IReadOnlyList<string> outputPaths)
    {
        outputPaths = [];
        if (string.IsNullOrWhiteSpace(description) ||
            !ExplicitIndependentIntentRegex().IsMatch(description) ||
            NegatedIndependentIntentRegex().IsMatch(description) ||
            RequestDependencyLanguageRegex().IsMatch(description) ||
            SourceMutationLanguageRegex().IsMatch(description) ||
            SharedArtifactLanguageRegex().IsMatch(description))
        {
            return false;
        }

        var paths = ExplicitOutputPathRegex().Matches(description)
            .Cast<Match>()
            .Select(match => match.Groups["path"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length < 2)
            return false;

        var normalized = new List<string>(paths.Length);
        foreach (var path in paths)
        {
            if (!TryNormalizeExactPath(path, out var safePath, out _))
                return false;
            normalized.Add(safePath!);
        }

        if (HasOverlap(normalized, out _))
            return false;

        outputPaths = normalized;
        return true;
    }

    public static bool TryPromoteSequentialRequiredFan(
        WorkflowDefinition workflow,
        IReadOnlyList<string> requiredOutputPaths,
        out WorkflowDefinition promoted)
    {
        promoted = workflow;
        if (requiredOutputPaths.Count < 2 ||
            workflow.Nodes.Any(node => node.Type is WorkflowNodeType.FanOut or WorkflowNodeType.FanIn))
        {
            return false;
        }

        var branches = new List<WorkflowNode>(requiredOutputPaths.Count);
        foreach (var requiredPath in requiredOutputPaths)
        {
            var matches = workflow.Nodes
                .Where(node =>
                    node.Type == WorkflowNodeType.Prompt &&
                    node.DeclaredOutputPaths.Contains(requiredPath, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            if (matches.Length != 1 || branches.Contains(matches[0]))
                return false;
            branches.Add(matches[0]);
        }

        var branchIds = branches.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        var declaredPaths = branches
            .SelectMany(node => node.DeclaredOutputPaths)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (declaredPaths.Count != requiredOutputPaths.Count ||
            requiredOutputPaths.Any(path => !declaredPaths.Contains(path)))
        {
            return false;
        }
        if (!CanReach(workflow, workflow.Start, branches[0].Id))
            return false;

        for (var i = 0; i < branches.Count - 1; i++)
        {
            if (!workflow.Edges.Any(edge =>
                    string.Equals(edge.From, branches[i].Id, StringComparison.Ordinal) &&
                    string.Equals(edge.To, branches[i + 1].Id, StringComparison.Ordinal) &&
                    string.IsNullOrWhiteSpace(edge.When)))
            {
                return false;
            }
        }

        var firstIncoming = workflow.Edges
            .Where(edge => string.Equals(edge.To, branches[0].Id, StringComparison.Ordinal))
            .ToArray();
        var lastOutgoing = workflow.Edges
            .Where(edge => string.Equals(edge.From, branches[^1].Id, StringComparison.Ordinal))
            .ToArray();
        if ((string.Equals(workflow.Start, branches[0].Id, StringComparison.Ordinal)
                ? firstIncoming.Length != 0
                : firstIncoming.Length != 1 || !string.IsNullOrWhiteSpace(firstIncoming[0].When)) ||
            lastOutgoing.Length != 1 ||
            !string.IsNullOrWhiteSpace(lastOutgoing[0].When) ||
            branchIds.Contains(lastOutgoing[0].To))
        {
            return false;
        }
        if (branches.Any(branch => CanReach(workflow, lastOutgoing[0].To, branch.Id)))
            return false;

        foreach (var branch in branches)
        {
            var incoming = workflow.Edges
                .Where(edge => string.Equals(edge.To, branch.Id, StringComparison.Ordinal))
                .ToArray();
            var outgoing = workflow.Edges
                .Where(edge => string.Equals(edge.From, branch.Id, StringComparison.Ordinal))
                .ToArray();
            var expectedIncoming = ReferenceEquals(branch, branches[0]) ? firstIncoming.Length : 1;
            if (incoming.Length != expectedIncoming || outgoing.Length != 1)
                return false;
        }

        var fanOutId = UniqueNodeId(workflow, "generated-fan-out");
        var fanInId = UniqueNodeId(workflow, "generated-fan-in");
        var fanOut = new WorkflowNode
        {
            Id = fanOutId,
            Type = WorkflowNodeType.FanOut,
            Label = "Parallel work",
        };
        var fanIn = new WorkflowNode
        {
            Id = fanInId,
            Type = WorkflowNodeType.FanIn,
            Label = "Join parallel work",
            Target = fanOutId,
        };
        var replacedEdges = workflow.Edges
            .Where(edge => !branchIds.Contains(edge.From) && !branchIds.Contains(edge.To))
            .ToList();
        if (firstIncoming.Length == 1)
        {
            replacedEdges.Add(new WorkflowEdge
            {
                From = firstIncoming[0].From,
                To = fanOutId,
            });
        }
        foreach (var branch in branches)
        {
            replacedEdges.Add(new WorkflowEdge { From = fanOutId, To = branch.Id });
            replacedEdges.Add(new WorkflowEdge { From = branch.Id, To = fanInId });
        }
        replacedEdges.Add(new WorkflowEdge { From = fanInId, To = lastOutgoing[0].To });

        var nodes = workflow.Nodes
            .Select(node => branchIds.Contains(node.Id) ? node with { Independent = true } : node)
            .ToList();
        var firstIndex = nodes.FindIndex(node =>
            string.Equals(node.Id, branches[0].Id, StringComparison.Ordinal));
        var lastIndex = nodes.FindIndex(node =>
            string.Equals(node.Id, branches[^1].Id, StringComparison.Ordinal));
        nodes.Insert(firstIndex, fanOut);
        nodes.Insert(lastIndex + 2, fanIn);
        promoted = workflow with
        {
            Start = string.Equals(workflow.Start, branches[0].Id, StringComparison.Ordinal)
                ? fanOutId
                : workflow.Start,
            Nodes = nodes,
            Edges = replacedEdges,
        };
        return true;
    }

    public static bool TryApply(
        WorkflowDefinition workflow,
        out ConservativeWorkflowFanResult result,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        var bindabilityErrors = RunWorkflowGraphBinder.GetBindabilityErrors(workflow);
        if (bindabilityErrors.Count > 0)
        {
            result = new(workflow, false, null);
            error = string.Join(" ", bindabilityErrors);
            return false;
        }

        var fanOut = workflow.Nodes.SingleOrDefault(node => node.Type == WorkflowNodeType.FanOut);
        var fanIn = workflow.Nodes.SingleOrDefault(node => node.Type == WorkflowNodeType.FanIn);
        if (fanOut is null && fanIn is null)
        {
            result = new(workflow, false, null);
            error = null;
            return true;
        }

        var branchNodes = workflow.Edges
            .Where(edge => string.Equals(edge.From, fanOut!.Id, StringComparison.Ordinal))
            .Select(edge => workflow.Nodes.Single(node =>
                string.Equals(node.Id, edge.To, StringComparison.Ordinal)))
            .ToList();

        var normalizedBranches = new Dictionary<string, WorkflowNode>(StringComparer.Ordinal);
        var safetyIssue = ValidateBranches(branchNodes, normalizedBranches);
        if (safetyIssue is null)
        {
            var normalizedNodes = workflow.Nodes
                .Select(node => normalizedBranches.GetValueOrDefault(node.Id, node))
                .ToList();
            var normalizedWorkflow = workflow with { Nodes = normalizedNodes };
            result = new(
                normalizedWorkflow,
                !workflow.Nodes.SequenceEqual(normalizedNodes),
                null);
            error = null;
            return true;
        }

        if (safetyIssue.PreventLinearization)
        {
            result = new(workflow, false, null);
            error =
                $"Generated fan topology contains a branch dependency and cannot be safely " +
                $"linearized in declaration order: {safetyIssue.Reason}";
            return false;
        }

        var sequential = Linearize(workflow, fanOut!, fanIn!, branchNodes);
        var sequentialErrors = RunWorkflowGraphBinder.GetBindabilityErrors(sequential);
        if (sequentialErrors.Count > 0)
        {
            result = new(workflow, false, null);
            error =
                $"Generated fan topology was unsafe ({safetyIssue.Reason}) and could not be conservatively " +
                $"linearized: {string.Join(" ", sequentialErrors)}";
            return false;
        }

        result = new(sequential, true, safetyIssue.Reason);
        error = null;
        return true;
    }

    public static WorkflowGenerationResult Enforce(WorkflowGenerationResult generated)
    {
        if (!TryApply(generated.Workflow, out var result, out var error))
        {
            throw new WorkflowGenerationException(
                $"The generated workflow is not safely executable. {error}",
                "workflow_generation_failed",
                [error ?? "The generated workflow is not safely executable."]);
        }

        if (!result.WasNormalized)
            return generated;

        return generated with
        {
            Workflow = result.Workflow,
            GeneratedYaml = WorkflowDefinitionYamlSerializer.Serialize(result.Workflow),
            WasCorrected = true,
        };
    }

    private static FanSafetyIssue? ValidateBranches(
        IReadOnlyList<WorkflowNode> branches,
        IDictionary<string, WorkflowNode> normalizedBranches)
    {
        var scopesByBranch = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var scopeIssues = new List<string>();
        var dependencyIssues = new List<string>();

        foreach (var branch in branches)
        {
            if (branch.Independent is not true)
                scopeIssues.Add($"branch '{branch.Id}' did not explicitly declare independent: true");
            if (string.IsNullOrWhiteSpace(branch.Prompt))
            {
                scopeIssues.Add($"branch '{branch.Id}' has no prompt to assess for dependencies and write scope");
            }
            else
            {
                if (StrongDependencyLanguageRegex().IsMatch(branch.Prompt))
                    dependencyIssues.Add($"branch '{branch.Id}' contains dependency language");
                if (SharedArtifactLanguageRegex().IsMatch(branch.Prompt))
                    scopeIssues.Add($"branch '{branch.Id}' contains shared-artifact language");
                if (SourceMutationLanguageRegex().IsMatch(branch.Prompt))
                    scopeIssues.Add($"branch '{branch.Id}' requests source or repository mutation");
                if (!ExplicitContentOutputContractRegex().IsMatch(branch.Prompt))
                {
                    scopeIssues.Add(
                        $"branch '{branch.Id}' must use an explicit content-only output contract such as 'write only <path>'");
                }
            }

            if (branch.DeclaredOutputPaths.Count == 0)
                scopeIssues.Add($"branch '{branch.Id}' has no declared_output_paths");

            var normalized = new List<string>(branch.DeclaredOutputPaths.Count);
            foreach (var rawPath in branch.DeclaredOutputPaths)
            {
                if (!TryNormalizeExactPath(rawPath, out var path, out var pathError))
                {
                    scopeIssues.Add(
                        $"branch '{branch.Id}' output scope '{rawPath}' is not exact and safe: {pathError}");
                    continue;
                }
                normalized.Add(path!);
            }

            if (HasOverlap(normalized, out var sameBranchOverlap))
                scopeIssues.Add($"branch '{branch.Id}' declares overlapping output scopes '{sameBranchOverlap}'");

            if (!string.IsNullOrWhiteSpace(branch.Prompt))
            {
                var promptPaths = PromptPathRegex().Matches(branch.Prompt)
                    .Cast<Match>()
                    .Select(match => match.Value.Replace('\\', '/'))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                foreach (var promptPath in promptPaths)
                {
                    if (!normalized.Contains(promptPath, StringComparer.OrdinalIgnoreCase))
                    {
                        scopeIssues.Add(
                            $"branch '{branch.Id}' prompt names undeclared output path '{promptPath}'");
                    }
                }

                foreach (var declaredPath in normalized)
                {
                    if (!branch.Prompt.Contains(declaredPath, StringComparison.OrdinalIgnoreCase))
                    {
                        scopeIssues.Add(
                            $"branch '{branch.Id}' prompt does not explicitly restrict writes to declared output '{declaredPath}'");
                    }
                }
            }

            scopesByBranch[branch.Id] = normalized;
            normalizedBranches[branch.Id] = branch.DeclaredOutputPaths.SequenceEqual(normalized, StringComparer.Ordinal)
                ? branch
                : branch with { DeclaredOutputPaths = normalized };
        }

        for (var i = 0; i < branches.Count; i++)
        {
            for (var j = i + 1; j < branches.Count; j++)
            {
                foreach (var left in scopesByBranch[branches[i].Id])
                {
                    foreach (var right in scopesByBranch[branches[j].Id])
                    {
                        if (PathsOverlap(left, right))
                        {
                            scopeIssues.Add(
                                $"branches '{branches[i].Id}' and '{branches[j].Id}' have overlapping " +
                                $"output scopes '{left}' and '{right}'");
                        }
                    }
                }

                if (ReferencesSiblingBranch(branches[i], branches[j]) ||
                    ReferencesSiblingBranch(branches[j], branches[i]))
                {
                    dependencyIssues.Add(
                        $"branches '{branches[i].Id}' and '{branches[j].Id}' contain sibling-consumption language");
                }
            }
        }

        if (dependencyIssues.Count > 0)
            return new(string.Join("; ", dependencyIssues.Distinct(StringComparer.Ordinal)), true);
        return scopeIssues.Count > 0
            ? new(string.Join("; ", scopeIssues.Distinct(StringComparer.Ordinal)), false)
            : null;
    }

    private static bool TryNormalizeExactPath(
        string rawPath,
        out string? normalized,
        out string? error)
    {
        normalized = null;
        error = null;
        var path = rawPath.Trim().Replace('\\', '/');
        while (path.Contains("//", StringComparison.Ordinal))
            path = path.Replace("//", "/", StringComparison.Ordinal);
        var hasTrailingSeparator = path.EndsWith("/", StringComparison.Ordinal);

        if (path.Length == 0)
        {
            error = "the scope is empty";
            return false;
        }
        if (path.StartsWith("/", StringComparison.Ordinal) ||
            path.StartsWith("~/", StringComparison.Ordinal) ||
            path.Contains(":", StringComparison.Ordinal) ||
            WindowsDriveRegex().IsMatch(path) ||
            DynamicPathRegex().IsMatch(path))
        {
            error = "absolute and dynamic paths are not supported";
            return false;
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            error = "relative traversal and empty directory scopes are not supported";
            return false;
        }

        path = string.Join('/', segments);
        if (BroadScopes.Contains(path))
        {
            error = "broad repository, source, or documentation scopes are not supported";
            return false;
        }

        var fileName = segments[^1];
        if (!fileName.Contains('.') || hasTrailingSeparator)
        {
            error = "the scope must identify an exact file";
            return false;
        }
        if (segments.Any(segment => segment.StartsWith(".", StringComparison.Ordinal)) ||
            segments.Any(segment =>
                segment.Equals("src", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("source", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("app", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("apps", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("package", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("packages", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("test", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("tests", StringComparison.OrdinalIgnoreCase)) ||
            SharedFileNames.Contains(fileName) ||
            fileName.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("manifest", StringComparison.OrdinalIgnoreCase) ||
            segments.Any(segment =>
                segment.Equals("migrations", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("migration", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("generated", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("dist", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("build", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("bin", StringComparison.OrdinalIgnoreCase)))
        {
            error = "manifests, migrations, generated artifacts, and dependency metadata are shared scopes";
            return false;
        }

        if (!SupportedContentExtensions.Contains(Path.GetExtension(fileName)))
        {
            error =
                "generated parallel branches may only write research, documentation, or other " +
                "supported content artifacts";
            return false;
        }

        normalized = path;
        return true;
    }

    private static bool HasOverlap(IReadOnlyList<string> paths, out string overlap)
    {
        for (var i = 0; i < paths.Count; i++)
        {
            for (var j = i + 1; j < paths.Count; j++)
            {
                if (PathsOverlap(paths[i], paths[j]))
                {
                    overlap = $"{paths[i]}' and '{paths[j]}";
                    return true;
                }
            }
        }

        overlap = string.Empty;
        return false;
    }

    private static bool PathsOverlap(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase) ||
        left.StartsWith(right + "/", StringComparison.OrdinalIgnoreCase) ||
        right.StartsWith(left + "/", StringComparison.OrdinalIgnoreCase);

    private static string UniqueNodeId(WorkflowDefinition workflow, string prefix)
    {
        var id = prefix;
        var suffix = 2;
        while (workflow.Nodes.Any(node => string.Equals(node.Id, id, StringComparison.Ordinal)))
            id = $"{prefix}-{suffix++}";
        return id;
    }

    private static bool CanReach(WorkflowDefinition workflow, string start, string target)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>([start]);
        while (pending.TryDequeue(out var current))
        {
            if (!visited.Add(current))
                continue;
            if (string.Equals(current, target, StringComparison.Ordinal))
                return true;
            foreach (var edge in workflow.Edges.Where(edge =>
                         string.Equals(edge.From, current, StringComparison.Ordinal)))
            {
                pending.Enqueue(edge.To);
            }
        }

        return false;
    }

    private static bool ReferencesSiblingBranch(WorkflowNode branch, WorkflowNode sibling)
    {
        if (string.IsNullOrWhiteSpace(branch.Prompt) ||
            !ConsumptionLanguageRegex().IsMatch(branch.Prompt))
        {
            return false;
        }

        var aliases = new List<string> { sibling.Id };
        if (!string.IsNullOrWhiteSpace(sibling.Label))
            aliases.Add(sibling.Label);
        foreach (var rawPath in sibling.DeclaredOutputPaths)
        {
            var path = rawPath.Trim().Replace('\\', '/');
            aliases.Add(path);
            aliases.Add(path[(path.LastIndexOf('/') + 1)..]);
        }

        return aliases
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Any(alias => branch.Prompt.Contains(alias, StringComparison.OrdinalIgnoreCase));
    }

    private static WorkflowDefinition Linearize(
        WorkflowDefinition workflow,
        WorkflowNode fanOut,
        WorkflowNode fanIn,
        IReadOnlyList<WorkflowNode> branches)
    {
        var incoming = workflow.Edges.SingleOrDefault(edge =>
            string.Equals(edge.To, fanOut.Id, StringComparison.Ordinal));
        var continuation = workflow.Edges.Single(edge =>
            string.Equals(edge.From, fanIn.Id, StringComparison.Ordinal));
        var removedIds = new HashSet<string>([fanOut.Id, fanIn.Id], StringComparer.Ordinal);
        var nodes = workflow.Nodes
            .Where(node => !removedIds.Contains(node.Id))
            .Select(node => branches.Any(branch => string.Equals(branch.Id, node.Id, StringComparison.Ordinal))
                ? node with { Independent = null }
                : node)
            .ToList();
        var edges = workflow.Edges
            .Where(edge => !removedIds.Contains(edge.From) && !removedIds.Contains(edge.To))
            .ToList();

        if (incoming is not null)
            edges.Add(new WorkflowEdge { From = incoming.From, To = branches[0].Id, When = incoming.When });
        for (var i = 0; i < branches.Count - 1; i++)
            edges.Add(new WorkflowEdge { From = branches[i].Id, To = branches[i + 1].Id });
        edges.Add(new WorkflowEdge
        {
            From = branches[^1].Id,
            To = continuation.To,
            When = continuation.When,
        });

        return workflow with
        {
            Start = string.Equals(workflow.Start, fanOut.Id, StringComparison.Ordinal)
                ? branches[0].Id
                : workflow.Start,
            Nodes = nodes,
            Edges = edges,
        };
    }

    [GeneratedRegex(
        @"\b(?:after|before|then|once|depends?\s+on|wait(?:ing)?\s+for|builds?\s+on|requires?\s+(?:the\s+)?(?:output|result|artifact|report|analysis|findings)|from\s+(?:the\s+)?(?:(?:other|previous|prior|sibling)\s+)?branch)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StrongDependencyLanguageRegex();

    [GeneratedRegex(
        @"\b(?:consume|use|using|incorporate|integrate|combine|merge|based\s+on|derive(?:d)?\s+from|findings|results?|outputs?|artifacts?|from\s+(?:the\s+)?branch)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConsumptionLanguageRegex();

    [GeneratedRegex(
        @"\b(?:implement|refactor|patch|modify|edit|update|delete|remove|rename|move|compile|migrate|change\s+(?:the\s+)?(?:code|source)|generate\s+(?:the\s+)?(?:code|source))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SourceMutationLanguageRegex();

    [GeneratedRegex(
        @"\b(?:write|draft|produce|create|save)\s+only\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitContentOutputContractRegex();

    [GeneratedRegex(
        @"\bindependent(?:ly)?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitIndependentIntentRegex();

    [GeneratedRegex(
        @"\b(?:(?:do\s+not|don't|never|not)\b.{0,48})independent(?:ly)?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NegatedIndependentIntentRegex();

    [GeneratedRegex(
        @"\b(?:depends?\s+on|based\s+on|requires?\s+(?:the\s+)?(?:output|result|artifact|report|analysis|findings)|from\s+(?:the\s+)?(?:(?:other|previous|prior|sibling)\s+)?branch|use\s+(?:those|the|its|their)\s+(?:findings|results?|outputs?|artifacts?))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RequestDependencyLanguageRegex();

    [GeneratedRegex(
        @"\b(?:write|draft|produce|create|save)\s+only\s+(?<path>(?:[a-zA-Z0-9_.-]+[\\/])*[a-zA-Z0-9_.-]+\.[a-zA-Z0-9]{1,16})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitOutputPathRegex();

    [GeneratedRegex(
        @"(?<![a-zA-Z0-9_])(?:[a-zA-Z0-9_.-]+[\\/])*[a-zA-Z0-9_.-]+\.[a-zA-Z0-9]{1,16}(?![a-zA-Z0-9_])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PromptPathRegex();

    [GeneratedRegex(
        @"\b(?:same|shared|common|existing)\s+(?:file|document|artifact|report|spec|prd|manifest|migration|output)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SharedArtifactLanguageRegex();

    [GeneratedRegex(@"^[a-zA-Z]:")]
    private static partial Regex WindowsDriveRegex();

    [GeneratedRegex(@"[*?\[\]{}$%<>]")]
    private static partial Regex DynamicPathRegex();
}
