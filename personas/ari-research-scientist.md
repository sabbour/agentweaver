# Ari Thompson — Research Scientist

## Identity & background

- Applied researcher who reviews papers, designs experiments, and writes grant or internal research proposals.
- Comfortable with dense technical material, uncertainty, and methodological critique.
- Uses Agentweaver to coordinate literature review, replication planning, and proposal review.

## Domain

Scientific research, literature synthesis, experimental design, grant writing, technical critique.

## Goals & motivations

- Synthesize large bodies of research without losing methodological nuance.
- Identify gaps, contradictions, and replication risks.
- Produce reviewable artifacts that distinguish evidence from speculation.

## What Ari wants from a multi-agent system

- Agents for paper summarization, methodology critique, citation mapping, experimental design, and skeptical review.
- Fine-grained source tracking and uncertainty labels.
- Ability to challenge conclusions and request deeper review of specific claims.

## Behavioral profile & decision patterns

- Starts with a research question, corpus, and inclusion/exclusion criteria.
- Drills into methods, datasets, sample sizes, and limitations before trusting conclusions.
- High tolerance for complex outputs; low tolerance for fabricated citations or oversimplified summaries.
- Reacts to errors by reducing corpus size or asking for claim-level evidence.
- Expects traceable source references and explicit disagreements among agents.

## Agentweaver scenarios

### Literature review swarm

- **Trigger/goal:** Summarize current approaches to agentic UI testing and identify open research gaps.
- **Team/agents:** Literature searcher, paper summarizer, methodology critic, taxonomy builder, synthesis writer.
- **UI steps attempted:** Create research project; specify question and criteria; run review swarm; inspect source map, themes, and gaps.
- **Success looks like:** Taxonomy of approaches, key papers, evidence table, disagreements, limitations, and future-work opportunities.

### Grant proposal critique

- **Trigger/goal:** Strengthen a draft grant proposal before submission.
- **Team/agents:** Scientific reviewer, novelty critic, methods reviewer, impact writer, budget-risk reviewer.
- **UI steps attempted:** Paste proposal sections; request critique; review ranked weaknesses; ask for revised aims and risk mitigations.
- **Success looks like:** Specific critique by section, improved aims, risk mitigation plan, and unresolved reviewer questions.

### Replication-plan review

- **Trigger/goal:** Determine whether a published result can be replicated with available resources.
- **Team/agents:** Methods analyst, data availability checker, implementation planner, statistics reviewer.
- **UI steps attempted:** Enter paper summary and resources; start replication review; inspect dependencies, assumptions, and feasibility.
- **Success looks like:** Replication checklist, required data/code, threats to validity, resource estimate, and go/no-go recommendation.

## Failure signals to watch for

- Citations are missing, malformed, or not tied to specific claims.
- Agent disagreement is collapsed into an overconfident single answer.
- Ari cannot inspect intermediate summaries or source inclusion decisions.
- The UI cannot handle long technical text or many references gracefully.
- Outputs omit methods, limitations, or threats to validity.
