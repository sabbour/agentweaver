# Tank new-project dialogs v3

- Kept the v2 shared dialog shell and polished it into two separate bordered cards with one corner close button and shared footer.
- Replaced segmented blueprint controls with underline tabs and a shared compact blueprint-card treatment for previews and template grids.
- Blank flow now emphasizes project basics, goal-driven generation, generated empty state, starter previews, and a three-step “what happens next” box.
- GitHub flow is repository-first: one combobox owns search/select, recents/org browsing/paste all feed the same selected repository, and project name derives from that selection.
- Suggested GitHub blueprint view restores the recommended card, curated other-blueprints preview, and custom-generation row.
- Validation: npm --prefix apps/web run build; npm --prefix apps/web test -- --run.
