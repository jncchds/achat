# T4 report — cleanup

Branch `task/04-cleanup` (based on `worktree-structured-turn-design`).

## Built
- **Dead schema.** `Conversation.ExtractionUpToMessageId` and `Bot.EvolutionUpToMessageId` removed (models, `AppDbContext`), migration `DropWatermarks` made with `dotnet ef`. Grep afterwards: no remaining reference outside old migrations and docs. No `Extraction:*` / `Evolution:MinNewMessages` / `MaxTailMessages` keys are read anywhere (all `GetValue` keys checked against the README table; the table was already complete). Kept as the brief said: `InteractionPurpose.MemoryExtraction`, `WorkKind.Extraction`, `MemorySource.Extraction` (old rows). The `WorkItems.Kind` and `Memory.Source` column defaults still name `Extraction`; changing them would need a migration for nothing. Stale comments/UI copy mentioning extraction updated (Admin background-work hint, memory viewer hint, XML docs).
- **E2E scripts.**
  - `mockllm.mjs`: extraction branch removed; new `[[remember:TEXT]]` marker (combines with `[[trait:]]`/`[[retire:]]`) produces an envelope with `memories`.
  - `phase4.mjs` rewritten: bot memories via envelope → stored `BotRemember` → recalled in the prompt (checked in the interaction's request body) → viewer edit/pin/forget → embedding lock. Now self-contained on the Mock preset.
  - `phase5-mock.mjs` rewritten as the envelope pipeline check (prompt order core → trait → directive, envelope instructions, remember + trait in one turn, footer payload, a non-envelope reply is delivered and has no side effects, no raw JSON in history).
  - `phase5.mjs` **deleted**: it needed a real model and asserted the prose evolution pass and `PUT /persona`; its surviving scenarios (layer order, revert) are covered by `phase5-mock.mjs` and `traits.mjs`.
  - `phase6.mjs`: moved to the mock preset; memory scope check is now deterministic (via `[[remember:]]`) instead of "best effort".
  - `workqueue.mjs` rewritten on the mock: dead endpoint → failed turns, **summary and consolidation** dead-letter (3/3), no Extraction item ever armed, "try again", admin retry of both, queue clears, a `Consolidation` version exists. Also fixed a latent bug: runs are detached from the connection (§4.2), so `hubRun` returned before the terminal event; it now waits for `chat.completed`/`chat.failed`.
  - `AGENTS.md` and the README *E2E scripts* section list the current scripts and the config each needs.
- **README.** New "How a turn works" paragraph; Phase 4/5 and work-pipeline status entries rewritten (envelope memories, trait list, consolidation); new turn-envelope entry; E2E section updated.
- **RELEASE_NOTES.md.** *Unreleased* entry (envelope, mood, bot memories, trait list, removals); older notes untouched.
- **design.md.** §2.4 (list, bounds, cooldown, versioning, consolidation, legacy, no watermarks; old rewrite pass as a short historical note), §4.7 (all steps done, "what went away", persistence corrected: no `Message.Envelope` column, deviations listed), §5.3 (live spec = bot memories + summary; extraction folded into §5.3.1 *Historical*), §12b (kinds Summary + Consolidation, mechanics), §13 resolved list, plus stray extraction mentions in §3, §4.4, §5.4–5.8, §8, §12a.

## Tests
- `dotnet build`: 0 warnings, 0 errors. `dotnet test`: 124 passed, 0 failed (no tests needed deleting). `npx tsc -b --noEmit`: clean.

## Runtime (throwaway compose project `achat-t4` on 5002 + `mockllm.mjs` on 18999)
Default config: `phase5-mock.mjs` 13/13, `traits.mjs` 14/14, `phase4.mjs` 9/9 (+ PASS), `phase6.mjs` 14/14, `phase8-verify.mjs` 8/8 — all PASS. (`phase6` first run hit leftover state from a run that failed before I installed `scripts/e2e` deps; after deleting that bot it passed.)
Fast-queue config (`Work__*` fast, `Chat__SummaryTailTokens=20`, `Turn__ConsolidateAbove=2`, `Evolution__CooldownHours=0`): `workqueue.mjs` 11/11 PASS.
Not run / not passing, with reasons:
- `phase3.mjs` needs a **real** model for recall through the summary (the mock only echoes); against the mock the gap checks pass and the recall check fails, as designed. It doesn't touch anything T4 changed.
- `phase7-verify.mjs` needs live provider endpoints; `smoke.mjs` needs Playwright browsers (not installed here) and tests unchanged UI.
Stack, volume and image removed (`down -v --rmi local`), mock stopped, dangling images pruned, build server shut down.

## Decisions / open
- Kept `WorkKind.Extraction`, `MemoryExtraction` purpose and the DB column defaults naming `Extraction` (old rows; avoids a pointless migration).
- Humans' trait add/edit still isn't capped by count/total (T3's note); retired items stay in `BotTraits` for revert.
- `Message.Envelope` was never built; design.md now says so.
