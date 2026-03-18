---
name: compact
description: Writes a summary of updates since the last /compact, saves it to instruction/history/, and proposes a commit title and message. Use when the user runs /compact or asks to compact updates.
---

# /compact — Update Summary and Commit Proposal

When the user invokes `/compact`, produce:

1. **Summary** of changes since the last `/compact`
2. **Commit title** (short, imperative, ~50 chars)
3. **Commit message** (body with details)
4. **History file**: write the summary to `instruction/history/YYYY-MM-DD-<slug>.md`

## Workflow

### 1. Determine scope

- **Since last /compact**: Read `.cursor/compact-last.md` if it exists. It stores the ref (commit hash or "initial") from the last compact. Use that as the base for `git diff`.
- **If no marker**: Use `HEAD` (all uncommitted changes since last commit).

### 2. Gather changes

```bash
git status
git diff [base]
git diff --staged [base]
```

If `base` is from compact-last: `git diff base..HEAD` plus `git diff` and `git diff --staged` for working tree.

### 3. Write output

Produce a single response with three sections:

```markdown
## Summary (since last /compact)

[Concise summary of what changed: files touched, features added/fixed, notable behavior changes. 2–5 sentences.]

---

## Commit

**Title:**
```
<imperative verb>(<scope>): <short description>
```

**Message:**
```
<optional longer explanation, bullet points for multiple changes>
```
```

### 4. Write history file

Create `instruction/history/YYYY-MM-DD-<slug>.md` containing the summary (use today's date and a slug from the commit title, e.g. `2025-03-18-molecule-construction-ui.md`).

### 5. Update marker

After outputting, create or update `.cursor/compact-last.md`:

```markdown
# Last compact
- **When**: <ISO date>
- **Ref**: <current HEAD hash after user commits, or "pending" if uncommitted>
```

If the user has uncommitted changes, write `Ref: pending` and note that the marker will reflect the commit hash once they commit.

## Commit title format

- Imperative: "Add", "Fix", "Refactor", "Move" (not "Added", "Fixed")
- Optional scope in parens: `(docs)`, `(rules)`, `(instruction)`
- ~50 chars max

## Examples

**Summary:**
> Moved detailed chemical-reaction-game instructions from `.cursor/rules/` into `instruction/chemical-reaction-game.md`. Updated the cursor rule to a short overview, added the role of `instruction/` for project update summaries, and pointed to the instruction file for full docs.

**Commit:**
```
docs: separate instructions into instruction/ and add compact skill

- Move chemical-reaction-game details to instruction/chemical-reaction-game.md
- Slim cursor rule; add instruction/ role and quick reference
- Add .cursor/skills/compact for /compact workflow
```
