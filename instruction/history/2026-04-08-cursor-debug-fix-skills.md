## Summary (since last /compact)

The sigma phase-3 work described in the previous compact is now **committed** as `497f991` (`fix(sigma-phase3): tie max-δ legs; remove phase 3 NDJSON`): all σ legs whose hybrid δ matches the global maximum within ε participate in fragment targeting; phase-3 NDJSON / `Phase3GuideBondLerpActive` plumbing was removed from `SigmaBondFormation` and related `#region agent log` hooks from `CovalentBond`; compact marker and the prior history note were folded into that commit.

**Still untracked:** project agent skills `.cursor/skills/debug/SKILL.md` (`/debug` — debug-only triage) and `.cursor/skills/fix/SKILL.md` (`/fix` — transient fix phase, then back to debug), aligned with `.cursor/rules/debug-phase-vs-fix-phase.mdc`.
