# Complete platform setup and tour the product

**Issue:** [#1135](https://github.com/sabbour/agentweaver/issues/1135)  
**Area:** Identity & access

## User story

As a Platform Admin, I want clear model-provider setup and a short product tour.
This flow helps me prepare Agentweaver and find its main actions.

## Context / problem

Agentweaver cannot start AI work until one deployment-wide model provider is ready.
The setup page must support GitHub Copilot and custom providers.

After setup, mixed-experience administrators need a short introduction to the app shell.
The introduction must not block normal product use.

## Scope

### In

- GitHub Copilot authorization during required setup
- Custom provider add, edit, activation, disconnect, and removal
- An explicit continue action after provider setup
- A three-step tour for Projects, Sessions, and Start task
- Per-user and versioned tour completion
- Tour replay from the settings menu
- Desktop and narrow-width coach-mark layouts

### Out

- A general redesign of Platform settings
- A general narrow-screen shell redesign
- Repository authorization onboarding
- A long or required product tutorial

## Acceptance criteria

- [ ] Required setup shows GitHub Copilot and custom provider paths.
- [ ] The administrator can manage custom providers during required setup.
- [ ] A ready provider does not open the app shell without an explicit continue action.
- [ ] The explicit continue action remains after the GitHub authorization return.
- [ ] The first shell visit starts a three-step product tour.
- [ ] The tour introduces Projects, Sessions, and Start task.
- [ ] The user can skip, finish, or replay the tour.
- [ ] Agentweaver stores tour completion for each Entra user and tour version.
- [ ] Coach marks stay inside desktop and narrow viewports.
- [ ] Tour controls support keyboard input and restore focus after dismissal.
- [ ] New onboarding copy uses Simple English.

## Notable edge cases

- GitHub authorization leaves Agentweaver and returns through a full page load.
- An Entra user can use a custom provider without a linked GitHub login.
- Browser storage can be unavailable.
- The user can resize or scroll the page while a coach mark is open.
- A short viewport can require a bottom-docked coach mark.
