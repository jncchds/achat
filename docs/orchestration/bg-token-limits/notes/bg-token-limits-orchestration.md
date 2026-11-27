---
name: bg-token-limits-orchestration
description: "How the AChat bg-token-limits series (personality jobs failing with token-exceeded) is orchestrated — ids, branch, progress"
metadata:
  node_type: memory
  type: feedback
  originSessionId: 378d05cf-486d-4bba-8448-6832863ea4db
  modified: 2026-11-27T20:54:42.879Z
---

Series fixing background personality jobs (trait consolidation `EvolutionHandler`, summary) that fail with token-exceeded errors. Same orchestrate-tasks flow as [[turn-envelope-orchestration]].

- Orchestrator: session name `achat`, agent id `378d05cf`, session id `378d05cf-486d-4bba-8448-6832863ea4db`, worktree `/mnt/git/achat/.claude/worktrees/bg-job-token-limits`.
- Integration branch: `worktree-bg-job-token-limits` (from origin/main). Docs: `docs/orchestration/bg-token-limits/`.
- **Git decision:** task branches merge into the integration branch; never push main. Local main was synced to origin/main (`bbb0b20`, PR #2 merged on GitHub) on user request.

**Why:** user wants to review before anything is published.
**How to apply:** merge task branches into the integration branch `--no-ff`; final merge to local main only on request; no pushes to main.

## Progress
- 2026-11-27 envelope merged to local main (`cabb239`). Plan + common + T1 brief written on the integration branch; waiting on user to confirm plan / give the actual error text before starting T1 worker.
- 2026-11-27 local main synced to origin/main (bbb0b20). T1 worker `41479cbe` (AChat bgtok T1 consolidation) started on branch task/01-consolidation-tokens. No OpenRouter key available yet: user must supply one (key file path) for the real-model run.
- 2026-11-27 User gave an OpenRouter key (free models); stored at /home/chds/.claude/jobs/378d05cf/tmp/openrouter.key (600) — delete at series end.
- 2026-11-27 T1 merged (worker restarted as `f64098f6`, now stopped; worktree/branch removed). 145 tests; real model OK but 40 traits → 1 (too aggressive) → output floor added to T2.
- 2026-11-27 T2 brief written; worker `36e9c6e4` (AChat bgtok T2 summary) started.
