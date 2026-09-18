---
name: "reviewer-protocol"
description: "Reviewer rejection workflow and corrective-pass semantics"
domain: "orchestration"
confidence: "high"
source: "extracted"
---

## Context

When a team member has a **Reviewer** role (e.g., Tester, Code Reviewer, Lead), they may approve or reject work from other agents. A rejection requires a bounded, evidence-based corrective pass and re-review of the stated finding.

## Patterns

### Reviewer Rejection Protocol

When a team member has a **Reviewer** role:

- Reviewers may **approve** or **reject** work from other agents.
- On **rejection**, the Reviewer provides the evidence and corrective scope for one bounded corrective pass. The Reviewer may recommend expertise, and the Coordinator MUST preserve the stated review finding for re-review.
- The Coordinator MUST use one fresh agent context for that pass. It may use the same named agent and charter as the original author.
- If the Reviewer approves, work proceeds normally.

### Reviewer Rejection Corrective-Pass Semantics

When an artifact is **rejected** by a Reviewer:

1. **Use one fresh agent context for one bounded corrective pass.** Give that context the rejection evidence and a defined correction scope.
2. **The fresh context may use the same named agent and charter as the original author.** Do not require a different agent, lock out the original author, or treat charter rotation as independent review.
3. **After the bounded pass, re-review the stated finding using the evidence.** If concrete design or safety risks remain unresolved, escalate with the review evidence.

## Examples

**Example 1: Fresh-context correction**
1. Fenster writes authentication module
2. Hockney (Tester) reviews → rejects: "Error handling is missing."
3. Coordinator gives a fresh Fenster context the rejection evidence and one bounded error-handling correction
4. Fenster produces v2
5. Hockney re-reviews the stated finding using the evidence
6. Hockney reviews v2 → approves

**Example 2: Expertise recommendation**
1. Edie writes TypeScript config
2. Keaton (Lead) reviews → rejects: "Need someone with deeper TS knowledge."
3. Coordinator uses a fresh agent context for one bounded correction and may use the recommended TS expertise
4. Edie produces v2
5. Keaton re-reviews the stated finding using the evidence

**Example 3: Escalation after corrective pass**
1. Fenster writes module → rejected for an unresolved safety risk
2. A fresh agent context performs the one bounded corrective pass → concrete safety risk remains
3. Coordinator: "The bounded corrective pass did not resolve the safety risk. Escalating with the review evidence: [artifact details]"

## Anti-Patterns

- ❌ Requiring a different agent or locking out the original author rather than using a fresh context
- ❌ Treating charter rotation as evidence of independent review
- ❌ Omitting rejection evidence or a defined corrective scope
- ❌ Skipping evidence-based re-review of the stated finding
- ❌ Escalating without concrete unresolved design or safety risks
