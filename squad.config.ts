import {
  defineAgent,
  defineRouting,
  defineSquad,
  defineTeam,
} from "@bradygaster/squad-sdk";

const modelTiers = {
  expert: "claude-opus-4.8",
  proficient: "claude-sonnet-4.6",
  basic: "claude-haiku-4.5",
} as const;

export default defineSquad({
  modelTiers,
  team: defineTeam({
    name: "agentweaver-core",
    description:
      "Squad for the agentweaver-core team, single-agent file-editing vertical slice (specs/001-single-agent-run).",
  }),
  agents: [
    defineAgent({
      name: "backend-engineer",
      role: "Owns ASP.NET Core 10 backend API, run lifecycle, and streaming contracts",
      model: modelTiers.expert,
      status: "active",
      capabilities: [
        { name: "aspnetcore-api", level: "expert" },
        { name: "sse-streaming", level: "expert" },
        { name: "sqlite-persistence", level: "proficient" },
      ],
    }),
    defineAgent({
      name: "frontend-engineer",
      role: "Builds Web and CLI thin clients over backend APIs",
      model: modelTiers.expert,
      status: "active",
      capabilities: [
        { name: "react-19-fluent2", level: "expert" },
        { name: "spectre-console-tui", level: "expert" },
        { name: "api-client-integration", level: "expert" },
      ],
    }),
    defineAgent({
      name: "runtime-engineer",
      role: "Implements Microsoft Agent Framework loop and sandboxed tools",
      model: modelTiers.expert,
      status: "active",
      capabilities: [
        { name: "microsoft-agent-framework", level: "expert" },
        { name: "sandbox-path-security", level: "expert" },
        { name: "provider-adapters", level: "proficient" },
      ],
    }),
    defineAgent({
      name: "qa-engineer",
      role: "Owns unit/integration/e2e and contract quality validation",
      model: modelTiers.proficient,
      status: "active",
      capabilities: [
        { name: "vitest", level: "expert" },
        { name: "playwright", level: "proficient" },
        { name: "contract-testing", level: "proficient" },
      ],
    }),
    defineAgent({
      name: "platform-engineer",
      role: "Maintains CI/CD, environment parity, and monorepo reliability",
      model: modelTiers.proficient,
      status: "active",
      capabilities: [
        { name: "github-actions", level: "proficient" },
        { name: "dotnet-platform", level: "proficient" },
        { name: "developer-experience", level: "proficient" },
      ],
    }),
  ],
  routing: defineRouting({
    rules: [
      { pattern: /\bapi|endpoint|aspnetcore|route|sse|stream\b/i, agent: "backend-engineer" },
      { pattern: /\breact|fluent|component|web|frontend|ui\b/i, agent: "frontend-engineer" },
      { pattern: /\bink|cli|terminal|tui\b/i, agent: "frontend-engineer" },
      { pattern: /\bagent framework|loop|orchestr|tool call|sandbox|worktree\b/i, agent: "runtime-engineer" },
      { pattern: /\btest|vitest|playwright|coverage|contract|qa\b/i, agent: "qa-engineer" },
      { pattern: /\bci|cd|workflow|pipeline|container|dotnet|platform\b/i, agent: "platform-engineer" },
    ],
  }),
});
