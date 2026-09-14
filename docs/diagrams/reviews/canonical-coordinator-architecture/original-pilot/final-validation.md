# Final validation evidence

- Focused Node tests: `node --test scripts\docs\render-diagrams.test.mjs scripts\docs\diagram-sources.test.mjs scripts\docs\diagram-inventory.test.mjs scripts\docs\capture-diagrams.test.mjs` — 18 passed.
- Pitch tests: `python -m unittest discover -s .github\skills\docs-diagram-pitch\tests` — 5 passed.
- Iteration tests: `python -m unittest discover -s .github\skills\docs-diagram-iterate\tests` — 14 passed.
- Official growth validator: 3,162 -> 28,462, 9.001265x; no invisible/off-page/duplicate/padding violations.
- Official manifest validator: valid. Also validated against supplied Draft 2020-12 JSON Schema with jsonschema.
- Session artifact audit: 5 complete one-page A5 draw.io/PNG/record sets; XML parse and unique IDs; 9/9 traced edge IDs with exact source/target pairs; one dashed revision rail; all coordinates inside page.
- Actual pitch and every actual pass PNG opened enlarged and at print scale; final A5-size raster is 794 x 559 at 96 DPI. No claim of physical printer inspection.
- Pinned draw.io Desktop: executable ProductVersion 31.4.5.0 / verified renderer 31.4.5.
- Selective final render and `npm run docs:check-diagrams -- --spec canonical-coordinator-architecture` — pass.
- Published source exactly matches final pass bytes; published re-export PNG exactly matches inspected final pass pixels.
- `node scripts\docs\inventory-diagrams.mjs --check --spec canonical-coordinator-architecture` — all 149 discovered entries match; read-only, no shard modified.
- `npm run docs:build` — exit 0, build complete in 69.87 seconds. Non-blocking Fluent UI `"use client"`, chunk size, and missing promql highlighter warnings.
- Scoped `git diff --check` — pass.

Final stamp:

```text
normalized source SHA-256 b4fe3ff9e3edcd27f2ed9973e5c1c89d09874b5042d075961df8597d4d452c5e
raw draw.io SHA-256     d7ce18f9249e43176f197bb0d22337e7327abf634cab11895e028bd4dd152614
published PNG SHA-256   ccdf397527fe1e102bd434ce2aeed8e0c68d450d0f8600ecdc789dd396159318
```

Task status remains blocked only because the mandatory shared inventory owner/disposition
write is outside the explicit exclusive paths. The actual diagram, its final review,
PNG and hash are persisted in the target worktree; no commit or product change.
All iteration and failed-preflight evidence is retained here, not deleted as temporary.
