---
name: "reviewer-protocol"
description: "Reviewer rejection workflow and strict lockout semantics"
domain: "orchestration"
confidence: "high"
source: "extracted"
---

## Context

When a team member has a **Reviewer** role (e.g., Tester, Code Reviewer, Lead), they may approve or reject work from other agents. On rejection, the coordinator enforces strict lockout rules to ensure the original author does NOT self-revise. This prevents defensive feedback loops and ensures independent review.

## Patterns

### Reviewer Rejection Protocol

When a team member has a **Reviewer** role:

- Reviewers may **approve** or **reject** work from other agents.
- On **rejection**, the Reviewer may choose ONE of:
  1. **Reassign:** Require a *different* agent to do the revision (not the original author).
  2. **Escalate:** Require a *new* agent be spawned with specific expertise.
- The Coordinator MUST enforce this. If the Reviewer says "someone else should fix this," the original agent does NOT get to self-revise.
- If the Reviewer approves, work proceeds normally.

### Strict Lockout Semantics

When an artifact is **rejected** by a Reviewer:

1. **The original author is locked out of the next revision.** They may not produce that corrective pass.
2. **One different, fresh-context agent owns one bounded corrective pass.** The Coordinator selects that agent from the Reviewer's recommendation (reassign or escalate), verifies it is not the original author, and gives it the rejection evidence and a defined correction scope.
3. **Do not rotate named charters as theater.** A new name or a chain of replacements is not evidence of independent correction; use the fresh context to address the stated finding.
4. **The locked-out author may not contribute to the corrective pass** in any form — not as a co-author, advisor, or pair.
5. **Lockout scope:** The lockout applies to the specific rejected artifact. The original author may still work on unrelated artifacts.
6. **After the bounded pass, re-review the stated finding.** If a real design, safety, or other blocking issue remains unresolved, escalate it with the evidence; do not rotate another author by default.

## Examples

**Example 1: Reassign after rejection**
1. Fenster writes authentication module
2. Hockney (Tester) reviews → rejects: "Error handling is missing. Verbal should fix this."
3. Coordinator: Fenster is now locked out of the corrective pass
4. Coordinator dispatches Verbal with fresh context and one bounded error-handling correction
5. Verbal produces v2
6. Hockney reviews v2 → approves
7. Lockout clears for the next artifact

**Example 2: Escalate for expertise**
1. Edie writes TypeScript config
2. Keaton (Lead) reviews → rejects: "Need someone with deeper TS knowledge. Escalate."
3. Coordinator: Edie is now locked out
4. Coordinator spawns new agent (or existing TS expert) to revise
5. New agent produces v2
6. Keaton reviews v2

**Example 3: Escalation after corrective pass**
1. Fenster writes module → rejected for an unresolved safety risk
2. Verbal performs the one bounded corrective pass → safety risk remains
3. Coordinator: "The bounded corrective pass did not resolve the safety risk. Escalating with the review evidence: [artifact details]"

**Example 4: Reviewer accidentally names original author**
1. Fenster writes module → rejected
2. Hockney says: "Fenster should fix the error handling"
3. Coordinator: "Fenster is locked out as the original author. Please name a different agent."
4. Hockney: "Verbal, then"
5. Coordinator spawns Verbal

## Anti-Patterns

- ❌ Allowing the original author to self-revise after rejection
- ❌ Treating the locked-out author as an "advisor" or "co-author" on the revision
- ❌ Rotating additional named charters after a corrective pass as a substitute for escalation
- ❌ Applying lockout across unrelated artifacts (scope is per-artifact)
- ❌ Accepting the Reviewer's assignment when they name the original author (must refuse and ask for a different agent)
- ❌ Clearing lockout before the revision is approved (lockout persists through revision cycle)
- ❌ Skipping verification that the revision agent is not the original author
