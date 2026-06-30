# Trigger tasks for scheduled and event workflows

**Issue:** _to be assigned_  
**Area:** Workflows & automation

## User story

As a project owner, I want a workflow to define a schedule (for example, every Monday) or an event as its trigger, and have a trigger task automatically start the workflow when that schedule or event fires, so that recurring and event-driven processes run on their own without me starting each run by hand.

## Context / problem

Today a workflow's trigger is largely metadata: there is no way to express a recurring cadence, and even a supported trigger does not, on its own, start a run. When a person describes a recurring process — "every Monday, triage the new issues" — the cadence is lost and nothing fires the work. A trigger should be a first-class part of the workflow: an entry task that initiates the run when its schedule or event occurs. Generated workflows should also faithfully carry the cadence and the target context the person supplied (for example, the repository to triage), rather than dropping them.

## Scope

### In
- a schedule trigger expressing a recurring cadence (for example, weekly on a chosen day)
- an event trigger that starts a workflow when a declared event occurs
- a trigger task that acts as the workflow's entry point and starts a run when its schedule or event fires
- generated workflows that preserve the requested cadence and the user-supplied target context
- visibility of upcoming and recent trigger firings on the board/heartbeat surfaces

### Out
- arbitrary cron expressions and sub-daily precision
- external scheduler integrations
- backfilling missed runs from before a workflow was defined
- bypassing existing pickup safety bounds and destructive-action review

## Acceptance criteria

- [ ] A workflow can declare a schedule trigger with a recurring cadence, or an event trigger.
- [ ] A workflow with a schedule or event trigger has a trigger task that starts the run when the schedule or event fires.
- [ ] Scheduled runs start automatically at the configured cadence without a manual start.
- [ ] A workflow generated from a natural-language description carries the described cadence as its trigger.
- [ ] A workflow generated from a description preserves the target context the person supplied (for example, the repository to act on).
- [ ] Trigger firings are accountable runs visible on the board.

## Notable edge cases

- A trigger that fires while a prior run is still in flight respects pickup capacity bounds rather than piling up runs.
- A workflow with no schedule or event trigger continues to start only on manual or existing pickup paths.
- An unsupported or malformed cadence is rejected at definition time with a clear message rather than silently never firing.
- Disabling automation pauses trigger firing while keeping the workflow and its next scheduled time visible.
