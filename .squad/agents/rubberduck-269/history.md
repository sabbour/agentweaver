

## 2026-07-13T23:59:00-07:00 — #269 review
Identified and reviewed mechanism, registration-site, and token-exposure risks in the initial #269 theory. The final Kata-only conditional passthrough implementation incorporated the review findings.


## 2026-07-20T12-01-24-07-00 — CI/docs/dev.mjs review
- Reviewed the staged CI workflow, CONTRIBUTING/RELEASING docs, and `scripts/azure/dev.mjs` changes.
- Found four real issues (missing docs CI job, `dev.mjs` TOCTOU overwrite race, CI lint-status masking, and a secret-guidance conflict); Link fixed all four and the re-review passed.
