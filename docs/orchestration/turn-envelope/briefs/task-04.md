Task T4 (cleanup) of the AChat turn-envelope series. An orchestrator session runs the series one task at a time, with one session per task.

First, read the worker rules and follow them throughout. They live only on the integration branch, so before your worktree exists read them with `git show worktree-structured-turn-design:docs/orchestration/turn-envelope/briefs/common.md` (cwd `/mnt/git/achat`).

- Worktree: `/mnt/git/achat/.claude/worktrees/task-04`
- Branch: `task/04-cleanup`, based on the local `worktree-structured-turn-design` branch.
- Read: `docs/design.md` §2.4, §4.7, §5.3, §12b; `docs/orchestration/turn-envelope/reports/task-03.md` (T3's report, incl. what it left open); `AGENTS.md`; `README.md` (the E2E scripts section and the phase list).

## Goal
Remove everything the envelope made dead, and make docs, scripts and the design spec tell the truth. No new features.

## Scope
1. **Dead schema/code.** Drop `Conversation.ExtractionUpToMessageId` and `Bot.EvolutionUpToMessageId` (models, `AppDbContext` config, and any remaining reads) with one `dotnet ef` migration. Remove other dead code from steps 1–3 you can prove unused (e.g. leftover extraction helpers/config keys, `Evolution:*` keys no longer read, `InteractionPurpose.MemoryExtraction` must stay — old interaction rows use it; keep `WorkKind.Extraction` too). Show with grep that nothing else referenced them.
2. **E2E scripts.** `scripts/e2e/workqueue.mjs`, `phase4.mjs`, `phase5.mjs`, `phase5-mock.mjs`, `phase6.mjs` (and anything else in `scripts/e2e/` that touches extraction, `PUT /persona`, prose evolution, or `Extraction__*` env vars) are obsolete. For each: rewrite it against the envelope flow (the mock in `mockllm.mjs` already returns envelopes — see T3's `traits.mjs` for the pattern), or, if its scenario no longer exists, delete it and say why. `workqueue.mjs` keeps what is still real (summary and evolution/consolidation dead-lettering and admin retry; chat "try again"). Run each remaining script against a throwaway stack on port 5002 with `mockllm.mjs`; they should pass. Update the script list in `AGENTS.md` and the README *E2E scripts* section to match.
3. **README.** Feature list and phase notes (lines mentioning background extraction and the old evolution pass), config table (every key still read, none that aren't), a short "How a turn works" paragraph describing the envelope. Keep release notes untouched except add an *Unreleased* entry in `RELEASE_NOTES.md` describing the rework (envelope, mood, bot memories, trait list, extraction removed).
4. **Design doc.** Bring `docs/design.md` up to date: §2.4 (evolved layer = list, consolidation, no prose rewrite pass, no message watermarks), §5.3 (extraction retired — fold the retirement note into a short historical subsection rather than leaving the old text as live spec), §12b (work kinds now Summary + Consolidation), §4.7 (all steps implemented, note deviations: `Turn:MaxTraitsTotalChars`, `Evolution:MaxTokens` 1024), §13 resolved list. Remove contradictions; keep the document's tone and `[DECIDED]` conventions.

## Tests
`dotnet build`, `dotnet test`, `npx tsc -b --noEmit` all green; delete tests only for code you deleted. Every e2e script you kept or rewrote passes (report the numbers).

## Runtime check
As in scope 2: one throwaway stack on 5002 with the mock, run all kept scripts, then bring the stack down, remove its volume and images, and prune.

## Docs
The report, plus the doc edits above. No UI changes, no screenshots.

When you're done, follow "Finishing" and "Waking the orchestrator" in common.md.
