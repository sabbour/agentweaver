# Decision: review outcome auditability and deterministic Squad triage

**Author:** Link (Platform Engineer)
**Date:** 2026-07-20

## Decision

Ordinary GitHub **Changes requested** feedback does not invoke reviewer-rejection lockout; the original author may revise normally. Lockout applies only when a Reviewer explicitly declares **Rejected / independent rewrite required**, recorded with the PR marker `REJECTED — requires independent rewrite`. That marker remains on the PR as the durable GitHub audit trail; `status:locked-out` can be added later if the label is created.

Feature and bug issue templates now apply the existing `squad` label by default, triggering `squad-triage.yml` deterministically. Issues filed outside those templates must have `squad` added manually. The documented operating norm is same-business-day P0 triage and routing other new Squad issues within a few business days.

## Rationale

The previous wording conflated normal review iteration with a formal independent-rewrite decision, and Coordinator-only enforcement was not observable to later PR readers. Existing label-triggered triage is lower risk than adding another scheduler and now receives template-created feature and bug issues directly.