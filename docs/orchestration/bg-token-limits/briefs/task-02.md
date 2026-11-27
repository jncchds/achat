# T2 — Summary pass, consolidation output quality, other passes, docs

Worktree `/mnt/git/achat/.claude/worktrees/task-02`, branch `task/02-summary-tokens`.
Read: `plan.md`, `reports/task-01.md` (reuse `TokenLimits`, `WorkOutcome.FailPermanent`, the mock modes
and `scripts/e2e/consolidation-limits.mjs`), `src/AChat.Api/Services/Work/SummaryWorker.cs`,
`EvolutionWorker.cs`, `MemoryWriter.cs`, `ChatService.cs` / `PromptAssembly.cs` (chat-turn budget).

## Goal
No background pass fails on token limits for a reason a retry can't fix, and consolidation keeps the
bot's personality instead of flattening it.

## Scope
1. **Summary pass.** Bound its input the same way (existing summary + tail against
   `Chat:SummaryMaxInputTokens` or a shared budget; when the tail is too long, summarise in steps,
   oldest first). Use `TokenLimits.IsContextLengthError` and `StripThinking`; context-length error or
   `finish_reason=length` → shrink once, else `FailPermanent` with an actionable reason. Check whether
   `Chat:SummaryMaxTokens` (1024) is enough for reasoning models; raise it like T1 did if not.
2. **Consolidation output floor.** In T1's real-model run, 40 traits collapsed into **1**. That erases
   the personality. Add a floor: the result must keep at least a configured minimum
   (`Evolution:MinTraitsAfter`, sensible default e.g. half of `Turn:ConsolidateAbove`, never more than
   the input count). State the target range in the prompt; a result below the floor is a retryable
   failure (logged), not applied. After chunked consolidation, if the merged list is still above
   `Turn:ConsolidateAbove`, do one final pass over the merged list when it fits the budget.
3. **Other passes.** Check `MemoryWriter` and any other utility-model call (embedding batches,
   envelope parsing in the chat turn) for the same unbounded-input / truncation pattern. Fix what is
   cheap and clearly the same bug; list the rest in the report.
4. **Docs.** `design.md` §4.5 / §12b (permanent failures, budgets), README config table,
   `RELEASE_NOTES.md` entry under a new unreleased heading.

## Tests
xUnit for summary budgeting/steps, summary truncation → permanent, consolidation floor (below floor →
not applied, retryable), final re-consolidation pass.

## Runtime check
Throwaway instance on 5002 with the mock (summary over the mock context limit; truncation), then the
real model via OpenRouter free models (key file path in the orchestrator's prompt; same key rules as
T1): a long conversation summarises without failure, and a 40-trait bot consolidates to a list within
the target range. Report the actual before/after trait lists.

## Docs
Report as usual; also write `docs/orchestration/bg-token-limits/pr-description.md` (summary of T1+T2
for the user).
