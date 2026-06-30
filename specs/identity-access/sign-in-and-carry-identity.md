# Sign in and carry identity across Agentweaver

**Issue:** [#2](https://github.com/sabbour/agentweaver/issues/2)  
**Area:** Identity & access

## User story

As a Agentweaver user, I want to sign in once with GitHub and have that identity carried through the web UI, API, and reviews, so that my work is attributable and protected without handling raw secrets in the browser.

## Context / problem

Agentweaver is multi-user and must know who is operating each project, run, review, and GitHub-backed action. Users need a simple sign-in path while the system preserves accountability across surfaces.

## Scope

### In
- GitHub sign-in and sign-out in the browser
- session continuity during a browser tab session
- identity display in the app shell
- ownership-aware access to protected resources

### Out
- managing model-provider credentials
- organization administration outside sign-in policy
- long-term browser account switching beyond the current session

## Acceptance criteria

- [ ] Unauthenticated users see a clear GitHub sign-in path before protected content.
- [ ] Successful sign-in returns users to the app shell with their GitHub identity visible.
- [ ] Protected actions are attributed to the signed-in caller.
- [ ] Sign-out clears the user session and returns the browser to the signed-out state.
- [ ] Identity mismatches fail safely instead of mixing two users in one session.

## Notable edge cases

- GitHub errors are shown on the sign-in surface.
- Expired, missing, or revoked session tokens return the user to sign-in.
- Signing out does not cancel runs that are already in progress.
