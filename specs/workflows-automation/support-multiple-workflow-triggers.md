# Support multiple workflow triggers

**Issue:** [#713](https://github.com/sabbour/agentweaver/issues/713)
**Area:** Workflows & automation

## User story

As a project owner, I want one workflow to run on a recurring schedule and on a GitHub event, so
that the same process can handle periodic sweeps and immediate event-driven work.

## Context / problem

The persisted workflow definition, API DTOs, trigger services, and editor all modeled one singular
trigger. Saving a schedule therefore replaced an event trigger, and saving an event replaced the
schedule.

## Scope

### In

- multiple automation triggers on one workflow
- backward-compatible loading and serialization of existing singular `trigger:` YAML
- independent schedule and GitHub-event evaluation
- editor controls that add, edit, remove, and display schedule and event triggers independently
- structured API round-trips for the complete trigger list

### Out

- new trigger kinds
- arbitrary cron expressions or sub-daily schedules
- changes to webhook authentication or delivery semantics

## Acceptance criteria

- [ ] Existing workflows with no trigger or one `trigger:` object keep working unchanged.
- [ ] A workflow can persist a weekly schedule and a GitHub Issues label event together.
- [ ] Schedule evaluation still fires when an event trigger is also present.
- [ ] Event evaluation still fires when a schedule trigger is also present.
- [ ] API and YAML round-trips preserve every configured trigger.
- [ ] The workflows page displays and edits each trigger without replacing the other.

## Notable edge cases

- A YAML document that declares both `trigger:` and `triggers:` is rejected as ambiguous.
- A workflow can declare at most one trigger of each supported type.
- Removing one trigger leaves the other configured.
- Deleting the legacy structured `/trigger` resource without a type continues to clear all triggers.
