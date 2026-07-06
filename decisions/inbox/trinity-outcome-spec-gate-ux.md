# Trinity outcome spec gate UX

- Kept the outcome spec gate visible during early coordinator orchestration by auto-opening the Spec slide panel once coordinator signals exist and no subtask plan has appeared.
- Treat GET /outcome-spec 404 as a pending draft state: show "Drafting the outcome spec..." and poll until the spec is available.
- If the run reaches a failed terminal status before a spec exists, show a terminal error instead of spinning forever.
- Confirm now uses an in-flight guard, disables Confirm/Revise while pending, shows "Confirming...", moves to the confirmed state on success, and maps 409 conflicts to user-readable messages.
- Validation: `npm --prefix apps/web run build` passed; `npm --prefix apps/web test -- --run` passed (473 tests).
