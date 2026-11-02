# Sources behind the documentation standards

Primary sources were read on **2026-11-02**. The templates and instructions
are original Agentweaver material informed by these sources, not copies of
their template bodies or a claim of endorsement.

## What we learned and adopted

| Source | Observed practice | Agentweaver application |
| --- | --- | --- |
| Microsoft Learn [contributor overview](https://learn.microsoft.com/en-us/contribute/content/how-to-write-overview) and [procedure style](https://learn.microsoft.com/en-us/style-guide/procedures-instructions/writing-step-by-step-instructions) | Contribution routes are distinct from product help. Task headings describe an outcome; numbered instructions identify where the action occurs. | Keep authoring material separate. Put the reader's goal, prerequisites and location before steps. |
| Microsoft Learn [`dotnet build` reference](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-build) | Applicability, name, synopsis, description, arguments, options and examples support lookup. The implicit-restore explanation states an important command side effect. | Reference exact syntax, defaults, conditions and effects; link procedures rather than mixing deployment evidence into lookup data. |
| GitHub Docs [content model](https://docs.github.com/en/contributing/style-guide-and-content-model/about-the-content-model) | Content types have distinct purposes. Predictable structure helps readers navigate; both overly deep and overly wide hierarchies impede discovery. Large repeated sections should have one permanent home. | Choose one primary purpose. Keep one canonical fact/procedure and link it; avoid a new navigation hierarchy for each template. |
| GitHub Docs [style guide](https://docs.github.com/en/contributing/style-guide-and-content-model/style-guide) | Clarity and meaning take priority over style rules. Alerts communicate special risks rather than decorate every paragraph. | Prefer plain technical English and precise conditions, not rigid word counts or repetitive caution banners. |
| GitHub Docs [cloning task](https://docs.github.com/en/repositories/creating-and-managing-repositories/cloning-a-repository) | The page explains the task, separates tool-specific instructions, defines repository substitutions, and shows expected command output. | Name the shell/platform and define required values. Keep commands copyable and results separate. |
| The Good Docs Project [template index](https://www.thegooddocsproject.dev/template), [tutorial](https://www.thegooddocsproject.dev/template/tutorial) and [how-to guidance](https://www.thegooddocsproject.dev/template/how-to) | Learning-oriented tutorials and practical task-oriented how-tos serve different starting knowledge and goals. Prerequisites and error scenarios prevent blocked readers. | Use separate learning/task skeletons. Getting started uses a short tutorial; planned operations reuse how-to. |
| The Good Docs Project [reference](https://www.thegooddocsproject.dev/template/reference) and [troubleshooting guidance](https://www.thegooddocsproject.dev/template/troubleshooting) | Reference favors concise structured facts; diagnosis starts from a recognizable symptom and leads to resolution. | Keep lookup distinct from procedure. Use safe checks that distinguish causes before recommending recovery. |
| [arc42 overview](https://arc42.org/overview/) and official section guidance below | Twelve complementary sections address drivers, boundaries, structure, behavior, infrastructure, decisions, quality and risk. Relevant views matter more than filling every possible detail. | Use the architecture template for deep dives, tailor deliberately and link shared views/ADRs rather than duplicating them. |

We do not import vendor publishing systems, Liquid/includes, their exact
navigation taxonomies, every style rule, or prescribed article lengths.
Agentweaver uses its existing Markdown, VitePress, source-map and diagram
pipeline. Templates guide judgement; they cannot establish factual accuracy.

## arc42 section provenance

The [architecture template](./templates/architecture.md) retains these twelve
section purposes, expressed as original reader questions and evidence prompts:

1. [Introduction and Goals](https://docs.arc42.org/section-1/):
   requirements, stakeholders and the quality goals that drive design.
2. [Constraints](https://docs.arc42.org/section-2/):
   limits on architectural freedom.
3. [Context and Scope](https://docs.arc42.org/section-3/):
   system boundaries, partners and external exchanges.
4. [Solution Strategy](https://docs.arc42.org/section-4/):
   fundamental approaches and their relationship to the goals.
5. [Building Block View](https://docs.arc42.org/section-5/):
   static decomposition and responsibilities; expand relevant internals.
6. [Runtime View](https://docs.arc42.org/section-6/):
   representative interactions, operations and failure scenarios.
7. [Deployment View](https://docs.arc42.org/section-7/):
   infrastructure and the mapping of software onto it.
8. [Crosscutting Concepts](https://docs.arc42.org/section-8/):
   selected shared mechanisms described once.
9. [Architecture Decisions](https://docs.arc42.org/section-9/):
   consequential choices, rationale and alternatives, with no redundant logs.
10. [Quality Requirements](https://docs.arc42.org/section-10/):
    specific, measurable usage or change scenarios.
11. [Risks and Technical Debt](https://docs.arc42.org/section-11/):
    prioritized weaknesses and mitigation.
12. [Glossary](https://docs.arc42.org/section-12/):
    shared domain and technical meanings.

The extra source/target/deployment labels and verification-state prompts are
Agentweaver-specific safeguards, not additional arc42 sections.

## URL verification

The user-supplied `https://www.thegooddocsproject.dev/template` was reachable
and exposed the Core Pack and individual template guides. Its links identify
the [GitLab template repository](https://gitlab.com/tgdp/templates) as the
template source; the comparison above used the reachable official website
guidance, not an unverified download.

The older GitHub URL
`https://docs.github.com/en/contributing/writing-for-github-docs/content-model`
redirected to the current content-model URL linked above. arc42's overview
links to the detailed guidance on `docs.arc42.org`. All twelve linked sections
were read; structural, runtime, deployment, decision and quality guidance
directly informed the template.
