# Guide validation finding

**Area:** docs. **Priority:** medium. **Status:** corrected; final rerun recorded
in `link-validation.json`. Logged in this owned review directory rather than
changing an out-of-scope global learning/skill file.

The first docs build succeeded, but its configuration sets ignoreDeadLinks=true.
An independent check of actual built main-content links found nine failures:
a removed sandbox anchor, a numeric heading whose VitePress ID starts `_1`,
the preserved em dash in Scenario 4's ID, and repository files outside the docs
build root (params example, contribution/release docs, changelog skill).

Correct the owned Markdown links using actual generated IDs. Link repository-only
files to their canonical dev-branch GitHub locations instead of pretending they
are deployed VitePress pages. Do not change global build settings for this task.

Reproduction: build docs, then run `validate-guide.py links`. This also checks
every embedded image's dimensions; file existence alone did not detect the
original 1x1 placeholders. Earlier failures were reported explicitly, not ignored
or converted into success-shaped fallback results.
