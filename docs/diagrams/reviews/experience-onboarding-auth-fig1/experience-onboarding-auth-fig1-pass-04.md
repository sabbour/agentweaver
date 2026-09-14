# experience-onboarding-auth-fig1: pass-04

**Takeaway:** Entra establishes identity; AI readiness and optional GitHub capabilities remain separate.

**Author:** GPT-6 Astra. **Page:** one uncompressed A5 landscape, 827 x 583 draw.io units.

**Export:** official draw.io Desktop 31.4.5; PNG, scale 2, border 16. Actual exported PNG and its 827 px print-size derivative opened and inspected.

**Grounding:** [experience-00-overview-fig1/research-identity.md](../experience-00-overview-fig1/research-identity.md). Three independently launched Astra threads: identity, execution, orchestration. Implementation/config/tests outrank legacy diagrams and captures.

## Correction-only review

No composition, content, or visual-language changes. Orientation, overlap, arrow endpoint/direction/routing checks found no permitted defect. Saved and re-exported a distinct source/PNG pair.

Reading direction, card/boundary separation, title/detail fit, label association and arrow direction reviewed at print size and enlarged. Zero remaining defects.

## Symbols and credits

Based on the repository Fluent template and loaded fluent-library.xml; theme corroborated by apps/web/src/theme.ts and CoordinatorTopologyGraph.tsx. Warm canvas/cards, Segoe UI, Cascadia Code metadata, 16 px radii, 5 px accents, shadows, semantic badges, orthogonal connectors and native bridge arcs preserved.

- Browser: `native:c4`.
- Auth API: `native:flowchart`.
- Microsoft Entra: `native:azure`.
- Ready app shell: `native:flowchart`.
- Browser session: `native:database`.
- Callback checks: `native:flowchart`.

Native shapes come from the bundled draw.io Desktop 31.4.5 libraries (https://github.com/jgraph/drawio-desktop/releases/tag/v31.4.5); draw.io is Apache-2.0. Vendor symbols identify their actual technology; no separate logos, screenshots or third-party image bytes were acquired. Custom hexagon marks only product-specific concepts.

## Evidence map and complete final arrow trace

- e0: Browser -> Auth API: authorize. `apps/Agentweaver.Api/Endpoints/AuthEndpoints.cs:290`. Endpoint-attached, correctly directed, gutter-routed; clean.
- e1: Auth API -> Microsoft Entra: redirect. `apps/Agentweaver.Api/Auth/EntraOAuthRedirectService.cs:250-260`. Endpoint-attached, correctly directed, gutter-routed; clean.
- e2: Microsoft Entra -> Callback checks: callback. `apps/Agentweaver.Api/Endpoints/AuthEndpoints.cs:327-365`. Endpoint-attached, correctly directed, gutter-routed; clean.
- e3: Callback checks -> Browser session: exchange. `apps/Agentweaver.Api/Endpoints/AuthEndpoints.cs:416-452`. Endpoint-attached, correctly directed, gutter-routed; clean.
- e4: Browser session -> Ready app shell: setup check. `apps/web/src/App.tsx:278-293`. Endpoint-attached, correctly directed, gutter-routed; clean.
