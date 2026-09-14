# First-pass preflight history

Before any correction-only pass, the upgrade was measured at 6.751739x, then 8.558507x.
Those drafts were not exported or counted as separate review passes.

The first exported pass-1 candidate measured 28,412 / 3,162 = 8.985452x: **failed**.
Its source, actual PNG and print-size image are retained under `pass-01-preflight.*`;
it is not accepted as pass 1. Both actual image sizes were opened. Findings:

- outer feedback label extended beyond the A5 right page;
- database icon default parameters rendered as a circle;
- small UML component jetties intruded into adjacent row text;
- output/review labels hid too much of their short connector shafts.

Continue the same visual-upgrade phase before pass 2: correct those native rendering/
label defects and clarify source-grounded metadata and Direct-mode wording. Re-export
and remeasure. Never lower the 9x threshold or count metadata/hidden/off-page padding.
