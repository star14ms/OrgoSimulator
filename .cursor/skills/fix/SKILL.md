---
name: fix
description: Temporarily authorizes fix phase for production changes to resolve a bug, then returns to debug phase. Use when the user runs /fix or explicitly authorizes implementing the fix after triage.
---

# /fix — Fix phase (this turn only), then back to debug

**Authorization:** The user invoked **/fix** or clearly authorized **implementing the fix** (word **fix** or equivalent per [.cursor/rules/debug-phase-vs-fix-phase.mdc](../../rules/debug-phase-vs-fix-phase.mdc)).

**For this turn:** Production changes **are** allowed: correct behavior, algorithms, data flow intended to resolve the bug.

**Immediately after completing fix work for this invocation:** Treat the **next** turn as **debug phase** again — no chained second fix without fresh verification.

## Workflow

### 1. Preconditions

- Prefer existing **hypotheses and log evidence** from `/debug` or prior triage. If missing, briefly state what the fix assumes and what will **verify** it.
- Re-read [.cursor/rules/debug-phase-vs-fix-phase.mdc](../../rules/debug-phase-vs-fix-phase.mdc): **no** narrow special-case branches that paper over one repro without fixing the shared rule.

### 2. Implement the fix

- Make **focused** production changes; keep unrelated refactors out of the same change set.
- Keep or add **minimal** post-fix logging if it helps confirm behavior (still follow [.cursor/rules/debug-log-bracket-single-line.mdc](../../rules/debug-log-bracket-single-line.mdc)).

### 3. Verification expectation (do not chain fixes)

After this fix:

1. User should **run again** and capture logs / behavior.
2. **Do not** start another behavioral fix in the **same** line of work until there is verification output **and**, for A/B bugs, an explanation of **why** A and B differ.

State this explicitly when handing off.

### 4. Handoff: back to debug phase

Close the response by noting that **subsequent** work defaults to **debug phase** until the user runs **/fix** again or says **fix** for a new change.

## Related

- Instrumentation-first triage: [/debug](../debug/SKILL.md)
- Phase rules: [.cursor/rules/debug-phase-vs-fix-phase.mdc](../../rules/debug-phase-vs-fix-phase.mdc)
