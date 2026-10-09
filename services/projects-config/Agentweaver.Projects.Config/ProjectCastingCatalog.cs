using System.Collections.Immutable;
using System.Text;

namespace Agentweaver.Projects.Config;

public sealed record ProjectCastingRoleDefinition(
    string Id,
    string Title,
    string Summary,
    ImmutableArray<string> Responsibilities,
    ImmutableArray<string> Boundaries);

public sealed record ProjectCastingTemplateDefinition(
    string Id,
    string Title,
    string Description,
    ImmutableArray<string> RoleIds);

public static class ProjectCastingCatalog
{
    private static readonly ImmutableArray<ProjectCastingRoleDefinition> RoleDefinitions =
    [
        Role("lead-pm", "Lead PM",
            "Owns the product vision, scope, and prioritization for a feature.",
            ["Define product vision and feature scope", "Prioritize work against user value", "Align stakeholders on outcomes"],
            ["Does not dictate implementation details", "Does not bypass quality gates for delivery"]),
        Role("prototype-designer", "Prototype Designer",
            "Designs and builds prototypes to validate product concepts.",
            ["Design and build interactive prototypes", "Validate concepts before full build", "Iterate based on feedback"],
            ["Does not ship production code", "Does not finalize requirements"]),
        Role("lead-architect", "Lead Architect",
            "Owns the overall technical design and ensures architectural coherence across the project.",
            ["Define and evolve the overall system architecture", "Review designs for coherence and long-term maintainability", "Set technical direction and resolve cross-cutting design conflicts"],
            ["Does not implement entire features alone", "Does not override domain owners without consensus"]),
        Role("core-implementer", "Core Implementer",
            "Implements the core functionality behind the library's public API.",
            ["Implement core functionality and internals", "Optimize for performance and reliability", "Maintain thorough test coverage"],
            ["Does not change public API without lead approval", "Does not skip compatibility tests"]),
        Role("docs-writer", "Docs Writer",
            "Produces and maintains reference documentation, feature docs, user guides, and release notes for users and contributors.",
            ["Write and maintain user and reference documentation", "Author runnable examples and guides", "Keep docs aligned with the current API", "Document features for end users and internal stakeholders", "Author release notes and keep documentation aligned with delivered scope"],
            ["Does not change library behavior", "Does not define feature scope", "Does not document unreleased or speculative features"]),
        Role("frontend-engineer", "Frontend Engineer",
            "Builds and maintains user interfaces and client-side application logic.",
            ["Implement and maintain user-facing interfaces", "Manage client-side state and data fetching", "Ensure accessibility and responsive behavior"],
            ["Does not define backend business logic", "Does not own data persistence decisions"]),
        Role("backend-engineer", "Backend Engineer",
            "Builds and maintains server-side APIs, data models, and business logic.",
            ["Implement and maintain backend services and APIs", "Define and evolve data models and storage schemas", "Enforce backend authority over business logic"],
            ["Does not implement frontend or UI components", "Does not make infrastructure or deployment decisions unilaterally"]),
        Role("security-engineer", "Security Engineer",
            "Safeguards the application and its supply chain through threat modeling, vulnerability review, and security-focused gating.",
            ["Threat-model features and surface abuse cases before implementation", "Review code for vulnerabilities and insecure patterns", "Assess authentication, authorization, and data-handling for weaknesses", "Evaluate dependency and secrets risk across the supply chain", "Track identified security issues through to verified remediation"],
            ["Does not own feature implementation", "Does not make merge decisions unilaterally", "Advises and gates on security grounds rather than directing product scope", "Does not weaken runtime sandbox, approval, or audit guarantees"]),
        Role("devops-engineer", "DevOps Engineer",
            "Owns build, deployment, and infrastructure reliability so software ships and runs predictably.",
            ["Build and maintain CI/CD pipelines for verified, repeatable releases", "Provision and manage infrastructure through version-controlled definitions", "Automate deployment, rollback, and environment promotion", "Instrument services with monitoring, logging, and alerting", "Improve reliability, capacity, and recovery posture over time"],
            ["Does not define product features or business logic", "Does not bypass change-safety or approval gates to expedite a release", "Advises on architecture for operability rather than owning system design", "Does not make irreversible infrastructure changes without review"]),
        Role("qa-engineer", "QA Engineer",
            "Designs and executes tests to verify quality, prevent regressions, and diagnoses and fixes assigned defects.",
            ["Design and maintain test suites and quality gates", "Guard against regressions before release", "Test across supported versions and platforms", "Verify backward compatibility guarantees", "Diagnose and reproduce assigned defects", "Implement minimal, well-tested fixes", "Verify fixes resolve the reported issue"],
            ["Does not implement product features", "Does not define the public API", "Does not approve releases unilaterally", "Does not reprioritize the bug queue", "Does not introduce unrelated changes"]),
        Role("lead-researcher", "Lead Researcher",
            "Drives investigation of an open question and synthesizes findings.",
            ["Frame the research question and approach", "Investigate sources and run experiments", "Synthesize findings into actionable conclusions", "Critically review findings for methodological rigor", "Assess evidence quality and identify gaps or alternative explanations"],
            ["Does not ship production code", "Does not present unverified findings as conclusive", "Does not conduct primary research without a defined question", "Does not approve findings as final authority"]),
        Role("writer", "Writer",
            "Produces clear, accurate written content for the intended audience.",
            ["Draft original content aligned to the brief", "Structure material for clarity and flow", "Incorporate factual sources and references"],
            ["Does not publish without editorial review", "Does not invent unverifiable claims"]),
        Role("editor", "Editor",
            "Reviews and refines written content for clarity, accuracy, and consistency.",
            ["Review drafts for clarity, grammar, and tone", "Enforce a consistent style and voice", "Verify facts and citations before approval"],
            ["Does not rewrite content wholesale without author input", "Does not own original content creation"]),
        Role("customer-researcher", "Customer Researcher",
            "Gathers and synthesizes customer signals to inform the product.",
            ["Gather customer signals and feedback", "Synthesize insights into actionable findings", "Validate assumptions with evidence"],
            ["Does not set final product scope", "Does not present opinions as validated data"]),
        Role("ai-safety-reviewer", "AI Safety Reviewer",
            "Evaluates agent behavior for safety, alignment, and policy compliance.",
            ["Review agent behavior for safety and alignment risks", "Verify compliance with applicable policies", "Red-team prompts and flows for failure modes"],
            ["Does not implement agent features", "Does not waive safety requirements"]),
    ];

    private static readonly ImmutableArray<ProjectCastingTemplateDefinition> TemplateDefinitions =
    [
        new(
            "product-feature-delivery",
            "Product Feature Delivery",
            "A team for delivering a product feature: product, research, design, docs, and quality.",
            ["lead-pm", "prototype-designer", "lead-architect", "core-implementer", "docs-writer"]),
        new(
            "quick-software-development",
            "Quick Software Development",
            "A focused team for fast software delivery: frontend, backend, security, and operations.",
            ["frontend-engineer", "backend-engineer", "security-engineer", "devops-engineer", "qa-engineer"]),
        new(
            "content-authoring-and-research",
            "Content Authoring & Research",
            "A team for producing and refining written content, and investigating questions into clear conclusions.",
            ["lead-researcher", "writer", "editor"]),
        new(
            "azure-feature-delivery",
            "Azure Feature Delivery",
            "A PM-led team for delivering Azure product features: strategy, UX, implementation, content, and security.",
            ["lead-pm", "customer-researcher", "prototype-designer", "core-implementer", "docs-writer", "ai-safety-reviewer"]),
    ];

    public static ImmutableArray<ProjectCastingRoleDefinition> Roles => RoleDefinitions;

    public static ImmutableArray<ProjectCastingTemplateDefinition> Templates => TemplateDefinitions;

    public static ProjectConfiguration CreateScenarioDraft(ProjectConfiguration current, string templateId)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (string.IsNullOrWhiteSpace(templateId))
            throw ProjectConfigException.Invalid("invalid_casting_template", "A scenario template ID is required.");

        var template = TemplateDefinitions.FirstOrDefault(item =>
            string.Equals(item.Id, templateId, StringComparison.Ordinal));
        if (template is null)
            throw ProjectConfigException.Invalid(
                "unknown_casting_template",
                $"Unknown scenario template '{templateId}'.");

        return CreateDraft(current, template.RoleIds);
    }

    public static ProjectConfiguration CreateManualDraft(
        ProjectConfiguration current,
        ImmutableArray<string> roleIds)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (roleIds.IsDefaultOrEmpty || roleIds.Any(string.IsNullOrWhiteSpace))
            throw ProjectConfigException.Invalid(
                "invalid_casting_roles",
                "Manual casting requires at least one non-empty role ID.");
        if (roleIds.Distinct(StringComparer.Ordinal).Count() != roleIds.Length)
            throw ProjectConfigException.Invalid(
                "duplicate_casting_role",
                "Manual casting cannot select the same role more than once.");

        var reservedRoles = roleIds.Where(IsReservedRole).ToArray();
        if (reservedRoles.Length > 0)
            throw ProjectConfigException.Invalid(
                "reserved_casting_role",
                $"Reserved orchestration role(s) cannot be cast: {string.Join(", ", reservedRoles)}.");

        var unknownRoles = roleIds
            .Where(roleId => !RoleDefinitions.Any(role =>
                string.Equals(role.Id, roleId, StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (unknownRoles.Length > 0)
            throw ProjectConfigException.Invalid(
                "unknown_casting_role",
                $"Unknown casting role ID(s): {string.Join(", ", unknownRoles)}.");

        return CreateDraft(current, roleIds);
    }

    private static ProjectConfiguration CreateDraft(
        ProjectConfiguration current,
        ImmutableArray<string> roleIds)
    {
        var charters = ImmutableArray.CreateBuilder<ProjectAgentCharter>(roleIds.Length);
        var casting = ImmutableArray.CreateBuilder<ProjectAgentCast>(roleIds.Length);
        for (var index = 0; index < roleIds.Length; index++)
        {
            var role = RoleDefinitions.Single(item =>
                string.Equals(item.Id, roleIds[index], StringComparison.Ordinal));
            charters.Add(new ProjectAgentCharter(
                role.Id,
                role.Title,
                role.Id,
                BuildCharter(role)));
            casting.Add(new ProjectAgentCast(role.Id, role.Id, index));
        }

        return current with
        {
            AgentCharters = charters.ToImmutable(),
            Casting = casting.ToImmutable(),
        };
    }

    private static string BuildCharter(ProjectCastingRoleDefinition role)
    {
        var charter = new StringBuilder(role.Summary);
        charter.AppendLine();
        charter.AppendLine();
        charter.AppendLine("Responsibilities:");
        foreach (var responsibility in role.Responsibilities)
            charter.Append("- ").AppendLine(responsibility);
        charter.AppendLine();
        charter.AppendLine("Boundaries:");
        foreach (var boundary in role.Boundaries)
            charter.Append("- ").AppendLine(boundary);
        return charter.ToString().TrimEnd();
    }

    private static bool IsReservedRole(string roleId) =>
        roleId.Equals("scribe", StringComparison.OrdinalIgnoreCase) ||
        roleId.Equals("work-monitor", StringComparison.OrdinalIgnoreCase) ||
        roleId.Equals("coordinator", StringComparison.OrdinalIgnoreCase) ||
        roleId.Equals("rai", StringComparison.OrdinalIgnoreCase);

    private static ProjectCastingRoleDefinition Role(
        string id,
        string title,
        string summary,
        ImmutableArray<string> responsibilities,
        ImmutableArray<string> boundaries) =>
        new(id, title, summary, responsibilities, boundaries);
}
