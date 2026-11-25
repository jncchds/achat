---
name: turn-envelope-orchestration
description: "How the AChat structured-turn-envelope series (§4.7) is orchestrated — session ids, branch, user decisions, progress"
metadata:
  node_type: memory
  type: feedback
  originSessionId: 80d2bbe0-29f2-45fe-8fd9-d1214b7dd639
  modified: 2026-11-25T21:12:33.550Z
---

The turn-envelope rework runs as one orchestrator plus one worker session per task (skill `orchestrate-tasks`).

- Orchestrator: session name `achat`, agent id `1171dc`, session id `80d2bbe0-29f2-45fe-8fd9-d1214b7dd639`, worktree `/mnt/git/achat/.claude/worktrees/structured-turn-design`.
- Integration branch: `worktree-structured-turn-design` (draft PR to main, never merge it). Docs: `docs/orchestration/turn-envelope/`.
- Spec: `docs/design.md` §4.7. Tool calling is dropped.

**User decisions:** replace the background passes (extraction removed outright, evolution → consolidation, summary stays); "mode change" means **mood**, conversation state that must not swing; reply streams first; bot changes apply automatically with guardrails; evolved layer becomes a trait list; keep consolidation; UI shell/palette already match the user's home UI — no redesign.

**Why:** the bot knows in the moment what matters, and it costs no extra calls.
**How to apply:** follow the plan's task table; write each brief to `briefs/task-NN.md`; one new worker session per task; check, merge `--no-ff`, clean up (never `claude rm`).

## Progress
- 2026-11-25 T1 merged directly by the orchestrator (`bf4ce22`): envelope + reply streaming + mood.
- 2026-11-25 T2 merged directly (`5ef0c57`): bot memories through `MemoryWriter`; extraction worker removed.
- 2026-11-25 series set up; T3 (traits) brief written.
- 2026-11-25 T3 worker `7c43c559` (AChat T3 traits) finished; merged `35aa642` (traits table + snapshots, envelope traits, guardrails, consolidation pass, personality list UI, `scripts/e2e/traits.mjs`). Deviation: total-chars cap named `Turn:MaxTraitsTotalChars`. Open for T4: obsolete phase5/phase5-mock/workqueue e2e scripts, unused watermarks.
- 2026-11-25 T4 (cleanup) brief written; worker started.
