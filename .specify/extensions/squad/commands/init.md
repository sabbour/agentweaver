---
description: "Initialize a Squad team from the implementation plan (tech-aware)"
---

# Squad Bridge: Init

Read the implementation plan and bootstrap a Squad team tailored to its
technology stack, architecture layers, and implementation phases. Run this
once after your initial `/speckit.plan` to get a squad that mirrors your
project's concrete technical shape.

**Why plan and not spec?** The spec (`spec.md`) is intentionally tech-agnostic
— it captures goals, users, and constraints. The plan (`plan.md`) is where
concrete technology decisions live (e.g., "React 19 with Next.js", "Go API
with gin"). Generating agents from the plan produces sharper charters with
accurate capabilities and routing rules.

## Prerequisites

Verify Squad CLI is available:

```bash
squad --version
```

If that fails, install it first:

```bash
npm install -g @bradygaster/squad-cli
```

## User Input

$ARGUMENTS

## Steps

1. **Read the plan** from the active spec directory under `specs/` (e.g.,
   `specs/001-<name>/plan.md`). If no plan exists, tell the user to run
   `/speckit.plan` first and stop. Also read `spec.md` for supplementary
   context (goals, constraints, non-functional requirements).

2. **Read tasks** from `specs/<id>/tasks.md` if it exists (used to infer work
   types and routing signals).

3. **Load bridge config** from `.specify/extensions/squad/squad-config.yml`
   if it exists, otherwise use extension defaults.

4. **Analyze the plan** to extract:
   - Technology stack explicitly chosen (e.g., "React 19 with Next.js",
     "Go microservices with gin", "PostgreSQL with Prisma ORM")
   - Architecture layers (frontend, backend, API gateway, data layer, infra)
   - Implementation phases (if the plan defines phased delivery)
   - Cross-cutting concerns (e.g., auth, testing, documentation, CI/CD)
   - Any explicit roles or team structure mentioned in the plan or spec

5. **Initialize Squad** if `.squad/` does not already exist:

   ```bash
   squad init
   ```

6. **Generate agent definitions** — for each identified domain/concern,
   create a Squad agent with:
   - A descriptive `name` (e.g., `backend-engineer`, `frontend-engineer`)
   - A `role` derived from the domain
   - `capabilities` array (name + level: expert/proficient/basic) inferred
     from how prominently the domain features in the plan
   - `model` set to the tier from config that matches the agent's complexity
   - `status: active`

   Write each agent as a `.squad/agents/{name}/charter.md` file following
   Squad's format. Also generate or update `squad.config.ts` at the project
   root using `@bradygaster/squad-sdk`'s `defineSquad()` (with `defineTeam()`,
   `defineAgent()`, and `defineRouting()` sub-builders) covering all agents,
   routing rules, and model tier settings from config.

7. **Generate routing rules** in `.squad/routing.md` that map task keywords
   and domain patterns to the agents created above. Be specific to the
   tech stack from the plan. Examples:
   - `/\bAPI|endpoint|REST|GraphQL\b/i` → backend-engineer
   - `/\bReact|component|UI|frontend\b/i` → frontend-engineer
   - `/\btest|spec|coverage|QA\b/i` → qa-engineer

8. **Print a summary**:

   ```
   ✅ Squad initialized from implementation plan

      Plan source    : specs/001-recipe-app/plan.md
      Tech stack     : React 19, Go 1.22, PostgreSQL 16

      Agents created : 3
        - backend-engineer   (Go/REST API — expert)
        - frontend-engineer  (React/TypeScript — expert)
        - qa-engineer        (Testing/QA — proficient)
      Routing rules  : 6
      Config         : squad.config.ts
   
   Next steps:
     squad doctor          — verify your team
     /speckit.tasks        — generate tasks from the plan
     /speckit.squad.route  — route tasks to agents (after tasks exist)
     /speckit.squad.run    — execute routed tasks
   ```

## Notes

- Running this command more than once is safe — it will not overwrite existing
  agent files. Use `/speckit.squad.generate` to refresh agents as the plan
  evolves.
- The plan is the primary input because it contains technology decisions. The
  spec provides supplementary context (goals, constraints) but is intentionally
  tech-agnostic.
- If `$ARGUMENTS` contains a domain or role name, generate an agent for that
  domain in addition to those inferred from the plan.
