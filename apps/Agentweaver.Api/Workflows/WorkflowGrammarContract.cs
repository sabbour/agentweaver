namespace Agentweaver.Api.Workflows;

internal sealed record WorkflowNodeTypeGrammar(
    WorkflowNodeType Type,
    string YamlType,
    string ApiType,
    string Label,
    bool Authorable,
    bool RuntimeBindable,
    IReadOnlyList<string> RuntimeKinds,
    IReadOnlyList<string> RequiredFields,
    IReadOnlyList<string> AllowedGateKinds);

internal sealed record WorkflowTransitionGrammar(
    NodeKind From,
    NodeKind To,
    IReadOnlyList<string?> Conditions);

/// <summary>
/// Runtime-owned workflow grammar metadata. The loader, serializer, binder, OpenAPI surface, and
/// maintained web client contract are checked against this catalog so accepted YAML cannot drift
/// from the grammar clients discover.
/// </summary>
internal static class WorkflowGrammarContract
{
    public const string Version = "1.1";
    public const int MaxDocumentCharacters = 262_144;
    public const int MaxNodes = 128;
    public const int MaxEdges = 512;
    public const int MaxTriggers = 16;
    public const int MaxPromptCharacters = 16_384;
    public const int MaxCharterCharacters = 8_192;

    public static readonly IReadOnlyList<WorkflowNodeTypeGrammar> NodeTypes =
    [
        Node(WorkflowNodeType.Prompt, "prompt", "prompt", "Prompt (agent turn)", true, true, ["agent"]),
        Node(WorkflowNodeType.PeerReview, "peer_review", "peer-review", "Peer review", true, true, ["peer-review"]),
        Node(WorkflowNodeType.BuildTest, "build_test", "build-test", "Build & Test", true, true, ["peer-review"]),
        Node(WorkflowNodeType.OpenPullRequest, "open_pull_request", "open-pull-request", "Open pull request", true, true, ["open-pull-request"]),
        Node(WorkflowNodeType.Publish, "publish", "publish", "Publish", true, true, ["agent"]),
        Node(WorkflowNodeType.Check, "check", "check", "Check / gate", true, true, ["rai", "human-review", "rubberduck"], ["branches", "gate_kind"], ["rai", "human-review", "rubberduck"]),
        Node(WorkflowNodeType.FanOut, "fan_out", "fan-out", "Fan-out", true, false, ["fan-out"]),
        Node(WorkflowNodeType.FanIn, "fan_in", "fan-in", "Fan-in", true, false, ["fan-in"]),
        Node(WorkflowNodeType.CoordinatorComposed, "coordinator_composed", "coordinator-composed", "Coordinator-composed", true, false, ["coordinator-composed"]),
        Node(WorkflowNodeType.Serial, "serial", "serial", "Serial", true, false, ["serial"]),
        Node(WorkflowNodeType.Merge, "merge", "merge", "Merge", false, true, ["merge"]),
        Node(WorkflowNodeType.Scribe, "scribe", "scribe", "Scribe", false, true, ["scribe"]),
        Node(WorkflowNodeType.Terminal, "terminal", "terminal", "Terminal", true, true, ["terminal"]),
    ];

    public static readonly IReadOnlyList<WorkflowTransitionGrammar> Transitions =
    [
        Unconditional(NodeKind.Agent, NodeKind.Rai),
        Transition(NodeKind.Rai, NodeKind.Agent, "revise", "review"),
        Transition(NodeKind.Rai, NodeKind.Terminal, "safety-failed", "no-changes"),
        Transition(NodeKind.Rai, NodeKind.Scribe, "no-changes"),
        Transition(NodeKind.Rai, NodeKind.HumanReview, "review"),
        Transition(NodeKind.HumanReview, NodeKind.Merge, "approved"),
        Transition(NodeKind.HumanReview, NodeKind.Agent, "request-changes", "approved"),
        Transition(NodeKind.HumanReview, NodeKind.Scribe, "approved"),
        Transition(NodeKind.HumanReview, NodeKind.Terminal, "declined", "approved"),
        Transition(NodeKind.Merge, NodeKind.Scribe, "merged"),
        Transition(NodeKind.Merge, NodeKind.OpenPullRequest, "merged"),
        Transition(NodeKind.Merge, NodeKind.HumanReview, "blocked"),
        Transition(NodeKind.Merge, NodeKind.PeerReview, "blocked"),
        Transition(NodeKind.Merge, NodeKind.Agent, "blocked"),
        Unconditional(NodeKind.Scribe, NodeKind.Terminal),
        Unconditional(NodeKind.Agent, NodeKind.Agent),
        Unconditional(NodeKind.Agent, NodeKind.PeerReview),
        Unconditional(NodeKind.Agent, NodeKind.Scribe),
        Unconditional(NodeKind.Agent, NodeKind.Terminal),
        Unconditional(NodeKind.Agent, NodeKind.HumanReview),
        Unconditional(NodeKind.Agent, NodeKind.Rubberduck),
        Unconditional(NodeKind.Agent, NodeKind.OpenPullRequest),
        Transition(NodeKind.PeerReview, NodeKind.OpenPullRequest, "approved", "pass"),
        Unconditional(NodeKind.OpenPullRequest, NodeKind.Scribe),
        Transition(NodeKind.Rai, NodeKind.Merge, "review"),
        Transition(NodeKind.Rai, NodeKind.PeerReview, "approved", "pass", "review"),
        Transition(NodeKind.Rai, NodeKind.Rubberduck, "review"),
        Transition(NodeKind.PeerReview, NodeKind.Merge, "approved", "pass"),
        Transition(NodeKind.PeerReview, NodeKind.PeerReview, "approved", "pass"),
        Transition(NodeKind.PeerReview, NodeKind.HumanReview, "approved", "pass"),
        Transition(NodeKind.PeerReview, NodeKind.Rai, "approved", "pass"),
        Transition(NodeKind.PeerReview, NodeKind.Rubberduck, "pass"),
        Transition(NodeKind.PeerReview, NodeKind.Agent, "request-changes", "fail", "approved", "pass"),
        Transition(NodeKind.PeerReview, NodeKind.Terminal, "approved", "pass", "declined"),
        Transition(NodeKind.Rubberduck, NodeKind.HumanReview, "pass"),
        Transition(NodeKind.Rubberduck, NodeKind.Merge, "pass"),
        Transition(NodeKind.Rubberduck, NodeKind.Agent, "pass", "revise"),
    ];

    public static bool TryParseNodeType(string raw, out WorkflowNodeType type)
    {
        var normalized = Normalize(raw);
        var entry = NodeTypes.FirstOrDefault(candidate => candidate.YamlType == normalized);
        type = entry?.Type ?? default;
        return entry is not null;
    }

    public static string YamlType(WorkflowNodeType type) => Entry(type).YamlType;

    public static string ApiType(WorkflowNodeType type) => Entry(type).ApiType;

    public static bool SupportsTransition(NodeKind from, NodeKind to, string? condition) =>
        Transitions.Any(rule => rule.From == from
            && rule.To == to
            && rule.Conditions.Contains(condition, StringComparer.Ordinal));

    public static WorkflowGrammarDto ToDto() => new()
    {
        GrammarVersion = Version,
        Format = "yaml",
        Root = new WorkflowRootGrammarDto
        {
            RequiredFields = ["id", "name", "start", "nodes"],
            OptionalFields = ["description", "version", "edges", "stages", "trigger", "triggers"],
            MaximumDocumentCharacters = MaxDocumentCharacters,
            MaximumNodes = MaxNodes,
            MaximumEdges = MaxEdges,
            MaximumTriggers = MaxTriggers,
        },
        NodeFields = new WorkflowNodeFieldsGrammarDto
        {
            RequiredFields = ["id", "type"],
            OptionalFields =
            [
                "label", "role", "kind", "gate_kind", "agent", "prompt", "charter", "target",
                "steps", "branches", "title", "body", "base", "head", "draft",
            ],
            MaximumPromptCharacters = MaxPromptCharacters,
            MaximumCharterCharacters = MaxCharterCharacters,
        },
        NodeTypes = NodeTypes.Select(node => new WorkflowNodeTypeGrammarDto
        {
            YamlType = node.YamlType,
            ApiType = node.ApiType,
            Label = node.Label,
            Authorable = node.Authorable,
            RuntimeBindable = node.RuntimeBindable,
            RuntimeKinds = node.RuntimeKinds,
            RequiredFields = node.RequiredFields,
            AllowedGateKinds = node.AllowedGateKinds,
        }).ToList(),
        Edge = new WorkflowEdgeGrammarDto
        {
            RequiredFields = ["from", "to"],
            OptionalFields = ["when"],
            ConditionsAreCaseSensitive = false,
            Transitions = Transitions.Select(rule => new WorkflowTransitionGrammarDto
            {
                FromKind = KindName(rule.From),
                ToKind = KindName(rule.To),
                Unconditional = rule.Conditions.Contains(null),
                When = rule.Conditions.OfType<string>().ToList(),
            }).ToList(),
        },
        Triggers = new WorkflowTriggerGrammarDto
        {
            Types = ["schedule", "event"],
            ScheduleIntervals = ["daily", "weekly", "monthly"],
            ReviewStates = ["approved", "changes_requested", "commented"],
            RefMatchModes = ["equals", "prefix"],
            PredicateTypes =
            [
                "has_label", "is_not_labeled_with", "base_branch", "review_state", "ref",
                "category", "comment_matches", "or", "not",
            ],
        },
        Compatibility = new WorkflowCompatibilityGrammarDto
        {
            LegacyLoadingOnly = true,
            CheckGateIdMatching = "trimmed, case-insensitive, with underscores and spaces normalized to hyphens",
            CheckGateIdFallbacks = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["rai"] = "rai",
                ["review"] = "human-review",
                ["human-review"] = "human-review",
                ["rubberduck"] = "rubberduck",
                ["rubber-duck"] = "rubberduck",
            },
        },
    };

    private static WorkflowNodeTypeGrammar Entry(WorkflowNodeType type) =>
        NodeTypes.Single(entry => entry.Type == type);

    private static WorkflowNodeTypeGrammar Node(
        WorkflowNodeType type,
        string yamlType,
        string apiType,
        string label,
        bool authorable,
        bool runtimeBindable,
        IReadOnlyList<string> runtimeKinds,
        IReadOnlyList<string>? requiredFields = null,
        IReadOnlyList<string>? allowedGateKinds = null) =>
        new(type, yamlType, apiType, label, authorable, runtimeBindable, runtimeKinds,
            requiredFields ?? [], allowedGateKinds ?? []);

    private static WorkflowTransitionGrammar Transition(
        NodeKind from, NodeKind to, params string?[] conditions) =>
        new(from, to, conditions);

    private static WorkflowTransitionGrammar Unconditional(NodeKind from, NodeKind to) =>
        new(from, to, [null]);

    private static string Normalize(string raw) =>
        raw.Trim().Replace('-', '_').Replace(' ', '_').ToLowerInvariant();

    private static string KindName(NodeKind kind) => kind switch
    {
        NodeKind.Agent => "agent",
        NodeKind.Rai => "rai",
        NodeKind.HumanReview => "human-review",
        NodeKind.Rubberduck => "rubberduck",
        NodeKind.Check => "check",
        NodeKind.Merge => "merge",
        NodeKind.Scribe => "scribe",
        NodeKind.Terminal => "terminal",
        NodeKind.FanOut => "fan-out",
        NodeKind.FanIn => "fan-in",
        NodeKind.Serial => "serial",
        NodeKind.PeerReview => "peer-review",
        NodeKind.OpenPullRequest => "open-pull-request",
        NodeKind.CoordinatorComposed => "coordinator-composed",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
