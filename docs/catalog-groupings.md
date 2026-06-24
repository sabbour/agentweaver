# Catalog: Groupings & Blueprints

This document describes the role groupings and blueprints available in the Agentweaver Squad catalog.

---

## Groupings

Groupings are lightweight, curated role sets. They map to common team shapes and are the building blocks for blueprints.

### `content-authoring-and-research`
**Title:** Content Authoring & Research

A team for producing and refining written content, and investigating questions into clear conclusions.

| Role | Purpose |
|------|---------|
| `lead-researcher` | Drives investigation and synthesizes findings |
| `writer` | Produces written content |
| `editor` | Reviews and refines written content |

---

### `quick-software-development`
**Title:** Quick Software Development

A focused team for fast software delivery: frontend, backend, security, and operations.

| Role | Purpose |
|------|---------|
| `frontend-engineer` | Builds UI and client-side logic |
| `backend-engineer` | Builds server-side services and APIs |
| `security-engineer` | Reviews and hardens security posture |
| `devops-engineer` | Manages CI/CD, infra, and deployments |
| `qa-engineer` | Writes and runs tests; ensures quality |

---

### `product-feature-delivery`
**Title:** Product Feature Delivery

A team focused on product discovery and feature delivery, combining PM and lightweight engineering.

*(See `product_feature_delivery.json` for full role list.)*

---

### `azure-feature-delivery`
**Title:** Azure Feature Delivery

A team shaped for Azure-specific feature work, including architecture, compliance, and operations.

*(See `azure_feature_delivery.json` for full role list.)*

---

## Blueprints

Blueprints are fuller, opinionated team configurations. They specify a `roster`, `workflow`, `review_policy`, and `sandbox_profile`.

### `blueprint-content-authoring`
**Name:** Content Authoring

Roster: `lead-researcher`, `writer`, `editor`, `quality-reviewer`, `docs-writer`

A team for long-form content production with quality review and documentation publishing.

---

### `blueprint-product-management`
**Name:** Product Management

Roster: `lead-pm`, `customer-researcher`, `prototype-designer`, `docs-writer`, `product-marketing-manager`, `ux-designer`

A product team covering discovery, prototyping, documentation, UX, and go-to-market readiness. The GTM and Product Launch responsibilities are folded into this blueprint — there is no separate GTM or Product Launch blueprint.

| Role | Why included |
|------|-------------|
| `lead-pm` | Owns product strategy and prioritization |
| `customer-researcher` | Drives customer discovery and validation |
| `prototype-designer` | Rapidly validates concepts |
| `docs-writer` | Produces product documentation |
| `product-marketing-manager` | Handles positioning, messaging, and launch |
| `ux-designer` | Owns user experience design |

---

### `blueprint-software-development`
**Name:** Software Development

Roster: `lead-architect`, `frontend-engineer`, `backend-engineer`, `security-engineer`, `devops-engineer`, `qa-engineer`, `docs-writer`

A complete software delivery team spanning architecture, frontend, backend, security, operations, quality, and documentation.

| Role | Why included |
|------|-------------|
| `lead-architect` | Sets technical direction and reviews design |
| `frontend-engineer` | Builds UI and client-side logic |
| `backend-engineer` | Builds server-side services and APIs |
| `security-engineer` | Reviews and hardens security posture |
| `devops-engineer` | Manages CI/CD, infra, and deployments |
| `qa-engineer` | Ensures quality through testing |
| `docs-writer` | Produces technical documentation |

---

### `blueprint-pm-and-software-development`
**Name:** Product & Software Delivery

Roster: `lead-pm`, `customer-researcher`, `prototype-designer`, `product-marketing-manager`, `ux-designer`, `lead-architect`, `frontend-engineer`, `backend-engineer`, `security-engineer`, `devops-engineer`, `qa-engineer`, `docs-writer`

A combined product and engineering team that takes work from discovery through delivery. Merges the `blueprint-product-management` and `blueprint-software-development` rosters, deduplicated.

This is the recommended starting point for teams shipping a full product.

---

## Notes

- **No standalone GTM/Product Launch blueprint** — those responsibilities live inside `blueprint-product-management`.
- The `workflow` field on all blueprints is currently `"default"`. Custom workflow configuration is planned for a future wave.
- Role IDs use kebab-case and match the `id` field in each role JSON file under `roles/`.
