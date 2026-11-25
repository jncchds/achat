# Structured turn envelope — plan

Spec: [`docs/design.md` §4.7](../../design.md) (authoritative). This folder only tracks *how the
work is run*. Integration branch: `worktree-structured-turn-design`; one draft PR to `main`.

## Goal
Remove the tool-calling plan. Every chat turn returns one JSON object that carries the reply and
what the turn changed: `{reply, mood, memories, traits}`. The background extraction pass is gone;
the evolution pass shrinks to a consolidation step. Summarisation is unchanged.

## User decisions
- Replace the passes (not "add alongside"); the extraction worker is removed outright (no fallback switch).
- "Mode change" = **mood**: conversation state, stable, a natural consequence of the conversation — it must not swing.
- `reply` first, streamed as it arrives (incremental JSON reader).
- Bot-driven memory and trait changes apply automatically, with guardrails; trait changes are versioned and revertable.
- Evolved layer becomes a list of short trait items; keep a slow consolidation pass over the list.
- The UI shell already matches the user's home UI design (abook / DebtBot pattern, Grove palette) — no UI redesign.

## Task series
| Task | What | Status |
|---|---|---|
| T1 | Envelope + `reply` streaming + tolerant parsing + `mood` end to end | ✅ merged (`bf4ce22`, done before the series started) |
| T2 | `memories` through the existing pipeline (`MemoryWriter`); extraction worker removed | ✅ merged (`5ef0c57`) |
| T3 | Trait list, `traits` field, guardrails, creator footer; evolution worker → consolidation — [brief](briefs/task-03.md) | next |
| T4 | Cleanup: drop unused watermarks (migration), fix stale e2e scripts and README, bring design §2.4 / §5.3 up to date | |
| T5 | End-to-end check against a real model in Docker, polish, PR description | |
