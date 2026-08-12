# Select workflow event actions

**Issue:** [#716](https://github.com/sabbour/agentweaver/issues/716)
**Area:** Workflows & automation

## User story

As a project owner, I want to choose the GitHub issue action for an event trigger, so that a
label-driven workflow runs when the label is added rather than only when the issue is opened.

## Context / problem

The workflow editor displayed action-specific event names such as `github.issues.opened` as the
generic **Issues** choice. Because the action was hidden, adding a `hasLabel` condition could appear
to configure label-added automation while the saved trigger still matched only issue creation.

## Scope

### In

- an explicit issue-action selector in the event trigger editor
- support for selecting `labeled` and the other GitHub Issues webhook actions
- preserving generic `github.issues` triggers as **Any issue action**
- regression coverage proving an issue can be labeled after creation and still fire

### Out

- action pickers for non-Issues webhook event types
- changes to GitHub webhook authentication or payload evaluation
- inferring an action from predicate text without showing it to the user

## Acceptance criteria

- [ ] Editing `github.issues.opened` displays **Opened** rather than only **Issues**.
- [ ] Selecting **Labeled** persists `github.issues.labeled`.
- [ ] A matching labeled delivery fires even when the earlier opened delivery did not.
- [ ] Generic `github.issues` remains available as **Any issue action**.

## Notable edge cases

- Changing the event type resets the event name to the generic form for the new type.
- Existing action-specific YAML remains action-specific unless the user changes the selector.
