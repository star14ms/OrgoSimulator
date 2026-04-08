---
name: debug
description: Enters debug-only triage for a bug — clears triage logs, rolls back unhelpful production fixes, states hypotheses, adds instrumentation, and stops for new logs. Use when the user runs /debug or asks to stay in debug phase only.
---

# /debug — Debug phase (instrumentation only)

**Operating mode:** **Debug phase only.** Do **not** change production behavior (algorithms, conditionals meant to fix the bug, data flow). Follow [.cursor/rules/debug-phase-vs-fix-phase.mdc](../../rules/debug-phase-vs-fix-phase.mdc).

After this skill completes its steps for the current turn, the conversation stays in **debug phase** until the user authorizes a fix (see [/fix skill](../fix/SKILL.md)).

## Workflow

### 1. Acknowledge phase

State explicitly that work is in **debug phase**: instrumentation and triage only, no bug-fix logic yet.

### 2. Clear existing triage logs

Empty or remove **project-root** debug outputs so the next run has a clean signal:

- `.cursor/cursor-workspace-debug.ndjson` (and legacy aliases like `.cursor/debug-workspace.ndjson` if present)
- `.cursor/debug-*.log` session files
- Any other **known** ingest paths referenced in code (e.g. hard-coded `.cursor/debug-….log` in the feature under investigation)

**Do not** delete unrelated `.cursor` content (rules, skills, config). If a path is uncertain, prefer **truncate to empty file** over deleting, so watchers don’t break.

### 3. Remove the previous fix that didn’t help (if any)

- Inspect `git status` / `git diff` for recent **production** edits (behavior, not just logs).
- If there is an attempted fix that **did not** resolve the bug, **revert** those behavioral changes (full file revert, `git checkout -- <paths>`, or manual undo) while **keeping** useful instrumentation where possible.
- If rever would drop needed logs, **re-apply instrumentation** after revert.
- If it’s unclear what counts as “failed fix,” ask the user which commits or files to roll back.

### 4. Multiple hypotheses

Produce **at least 2–3** distinct, testable hypotheses for **why** the bug occurs (different layers: data in vs out, ordering, parenting, math path, flags, etc.). Map each hypothesis to **what log evidence** would confirm or rule it out.

### 5. Add or adjust debug instrumentation

- Follow [.cursor/rules/debug-log-bracket-single-line.mdc](../../rules/debug-log-bracket-single-line.mdc) (single leading `[tag]`, one line per call, no `\n` spam).
- Follow [.cursor/rules/debug-flags-default-on.mdc](../../rules/debug-flags-default-on.mdc) for new gates (default **on** unless per-frame flood risk).
- Prefer **scoped** logs at divergence points (before/after the suspected unknown).

### 6. Stop and wait

Do **not** implement a behavioral fix in the same turn unless the user separately runs **/fix**.

Tell the user to **reproduce** once and to share **new** logs (paste snippets or confirm `.cursor/` files updated). Next step is **read logs → refine hypotheses**, not ship a fix.

## Related

- Phase gate and no “paper over” branches: [.cursor/rules/debug-phase-vs-fix-phase.mdc](../../rules/debug-phase-vs-fix-phase.mdc)
- Fix authorization: [/fix](../fix/SKILL.md)
