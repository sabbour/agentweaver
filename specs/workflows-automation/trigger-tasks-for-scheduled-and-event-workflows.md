# Trigger tasks for scheduled and event workflows

**Issue:** _to be assigned_  
**Area:** Workflows & automation

## User story

As a project owner, I want recurring and event-driven automation rules to start a workflow for me when a schedule (for example, every Monday) or event fires, so that recurring and event-driven processes run on their own without me starting each run by hand.

## Context / problem

Today workflows are pipeline definitions, not invocation rules: there is no automation layer that can express a recurring cadence or "when event X happens, run workflow Y." When a person describes a recurring process — "every Monday, triage the new issues" — the cadence is lost and nothing fires the work. The missing concept is an automation rule that binds a schedule or event to a trigger-agnostic workflow and starts a run when that condition occurs. Generated automation should also faithfully carry the cadence and the target context the person supplied (for example, the repository to triage), rather than dropping them.

## Scope

### In
- an automation-rule layer that expresses a recurring cadence (for example, weekly on a chosen day)
- an automation rule that starts a workflow when a declared event occurs
- a trigger/automation task that evaluates those rules and starts a run when the schedule or event fires
- generated automation rules that preserve the requested cadence and the user-supplied target context
- visibility of upcoming and recent trigger firings on the board/heartbeat surfaces

### Out
- arbitrary cron expressions and sub-daily precision
- external scheduler integrations
- backfilling missed runs from before a workflow was defined
- bypassing existing pickup safety bounds and destructive-action review

## Acceptance criteria

- [ ] Users can define an automation rule that binds a recurring schedule or event to a workflow.
- [ ] Automation rules start the referenced workflow when the schedule or event fires.
- [ ] Scheduled runs start automatically at the configured cadence without a manual start.
- [ ] A workflow generated from a natural-language description remains trigger-agnostic while any generated automation rule carries the described cadence.
- [ ] A workflow generated from a description preserves the target context the person supplied (for example, the repository to act on).
- [ ] Trigger firings are accountable runs visible on the board.

## Notable edge cases

- A rule that fires while a prior run is still in flight respects pickup capacity bounds rather than piling up runs.
- A workflow with no automation rule continues to start only on manual or existing pickup paths.
- An unsupported or malformed cadence is rejected at automation-rule definition time with a clear message rather than silently never firing.
- Disabling automation pauses rule firing while keeping the workflow and its next scheduled time visible.
