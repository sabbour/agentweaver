# GitHub Issue Skill — Simulated Output
**Task:** Squad: Reporting bug for investigation and GitHub tracking — assembly_blocked: ineligible_subtasks [59,60,61,62]

---

## Step 1 — Classify

| Field | Value |
|-------|-------|
| **Type** | Bug — assembly stopped with an error |
| **Domain** | Assembly → `squad:tank` (primary domain owner) |
| **Co-assignee** | `squad:smith` — always added for bugs (leads RCA) |

---

## Step 2 — Labels

`type:bug,squad:tank,squad:smith,go:yes,priority:p1,release:backlog`

- `go:yes` — error code and run URL are provided; actionable immediately
- `priority:p1` — bug, this sprint
- `release:backlog` — no specific release targeted

---

## Step 3 — Issue Body (Bug Template)

```markdown
## Summary
Collective assembly stops with error `assembly_blocked: ineligible_subtasks [59,60,61,62]`.

## Run / context
https://agentweaver.6a41f26c75d5cf00019ef7d7.westus2.staging.aksapp.io/projects/f88ff808-2efa-428e-8f6a-9e67a6296b70/orchestrations/2afef9f5-109f-4310-9fea-00db92f67ac7

## Steps to reproduce
Reported directly

## Expected behavior
The collective assembly should complete successfully, with all subtasks evaluated and eligible for assembly.

## Actual behavior
Assembly stopped with error: `assembly_blocked: ineligible_subtasks [59,60,61,62]`

Subtasks 59, 60, 61, and 62 are flagged as ineligible, causing the entire assembly to block.

## Technical notes
Investigation needed into why subtask IDs 59–62 are considered ineligible. Possible causes:
- Unmet dependencies or incomplete upstream tasks
- Subtask status not in an acceptable state for assembly evaluation
- Assembly gate logic rejecting specific task types or outputs
- Data corruption or missing fields on the subtask records

## Reported by
@sabbour — 2026-07-01
```

---

## Step 4 — Simulated `gh` Command

```bash
gh issue create \
  --title "Bug: Collective assembly blocked with assembly_blocked: ineligible_subtasks" \
  --label "type:bug,squad:tank,squad:smith,go:yes,priority:p1,release:backlog" \
  --body "$(cat <<'EOF'
## Summary
Collective assembly stops with error `assembly_blocked: ineligible_subtasks [59,60,61,62]`.

## Run / context
https://agentweaver.6a41f26c75d5cf00019ef7d7.westus2.staging.aksapp.io/projects/f88ff808-2efa-428e-8f6a-9e67a6296b70/orchestrations/2afef9f5-109f-4310-9fea-00db92f67ac7

## Steps to reproduce
Reported directly

## Expected behavior
The collective assembly should complete successfully, with all subtasks evaluated and eligible for assembly.

## Actual behavior
Assembly stopped with error: `assembly_blocked: ineligible_subtasks [59,60,61,62]`

Subtasks 59, 60, 61, and 62 are flagged as ineligible, causing the entire assembly to block.

## Technical notes
Investigation needed into why subtask IDs 59–62 are considered ineligible. Possible causes:
- Unmet dependencies or incomplete upstream tasks
- Subtask status not in an acceptable state for assembly evaluation
- Assembly gate logic rejecting specific task types or outputs
- Data corruption or missing fields on the subtask records

## Reported by
@sabbour — 2026-07-01
EOF
)"
```

---

## Step 5 — Dispatch

```
✅ Filed #142: Bug: Collective assembly blocked with assembly_blocked: ineligible_subtasks
   https://github.com/sabbour/agentweaver/issues/142

📋 Dispatch:
   squad:smith — Lead RCA: investigate why subtasks [59,60,61,62] are flagged as ineligible;
                 inspect assembly gate logic, subtask status transitions, and dependency checks
                 for orchestration 2afef9f5-109f-4310-9fea-00db92f67ac7
   squad:tank  — Implement fix once Smith delivers RCA findings; owns the assembly domain

🏷️  Labels: type:bug,squad:tank,squad:smith,go:yes,priority:p1,release:backlog
```
