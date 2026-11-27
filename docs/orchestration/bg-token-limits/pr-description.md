# Background personality jobs no longer fail on token limits

## Problem
The background passes that maintain a bot's personality, trait consolidation and the rolling conversation summary, sent unbounded prompts. Their output caps were too low for reasoning models, and they retried token errors that can never succeed. On small-context or reasoning models they failed with "token exceeded" five times and dead-lettered. Then, in the first real-model run after the input fix, consolidation merged **40 traits into 1**, which erased the personality.

## What changed
**Consolidation (T1 + T2)**
- **Input budget.** `Evolution:MaxInputTokens` (4000) covers the instructions, core persona, directives and traits. A longer list is consolidated in chunks, oldest first. Nothing is written until every chunk succeeds, so a trait version is never half-applied.
- **Output cap** `Evolution:MaxTokens` 1024 → 4096. `finish_reason=length` is detected and the cut-off content is never parsed.
- **Token errors are deterministic.** A context-length rejection or a truncation splits the input in two and retries once. If that fails, the work item is dead-lettered on that attempt (`WorkOutcome.FailPermanent`), with a reason naming the setting to change. Other failures keep the normal backoff.
- **Output floor.** The prompt asks for between `Evolution:MinTraitsAfter` (default half of `Turn:ConsolidateAbove`, so 6) and `Turn:ConsolidateAbove` (12) items. A result below the floor is logged and not applied, and the pass retries.
- **Final pass.** Chunk results still above the trigger size get one more merge over the combined list.
- **Human trait add/edit** obeys the bot's caps (count, item length, total). The API returns 400 with the reason.

**Rolling summary (T2)**
- **Input budget** `Chat:SummaryMaxInputTokens` (4000). A long tail is summarised in steps, oldest first. Each step saves the summary and its watermark together, so a later failure loses nothing. At most `Chat:SummaryMaxSteps` (8) steps run per pass.
- **Same deterministic-failure handling** as consolidation. `Chat:SummaryMaxTokens` 1024 → 4096. The requested length is now its own setting, `Chat:SummaryTargetTokens` (800). Inline `<think>` is stripped.

**Other**
- Bot memories are clipped to 1000 characters before embedding.
- The mock LLM gained context-limit and truncation modes for both passes, and now recognises summary requests (it didn't before).
- New e2e scripts: `scripts/e2e/consolidation-limits.mjs` and `scripts/e2e/summary-limits.mjs`.
- Docs: design.md §2.4, §4.5, §5.3, §12b; the README config table; RELEASE_NOTES.

## Verification
- `dotnet test AChat.slnx`: **161 passed** (36 new across T1 + T2). `dotnet build` is green.
- Docker throwaway instance with the mock LLM: both e2e scripts pass every check. That covers chunking and steps within budget, context-length shrink, truncation dead-lettering after one attempt with an actionable reason and data untouched, and admin retry.
- Real model, `nvidia/nemotron-3-nano-omni-30b-a3b-reasoning:free` on OpenRouter:
  - **Consolidation: 40 → 10 traits**, inside the 6–12 target (before this fix: 40 → 1). The full list truncated at 4096 output tokens and the halves succeeded.
  - **Summary:** a 10-turn conversation with 8 planted facts was summarised in 2 steps with no failure, and all 8 facts are in the summary.

## Settings to know
| Setting | Default |
|---|---|
| `Evolution__MaxInputTokens` | 4000 |
| `Evolution__MaxTokens` | 4096 |
| `Evolution__MinTraitsAfter` | half of `Turn__ConsolidateAbove` |
| `Chat__SummaryMaxInputTokens` | 4000 |
| `Chat__SummaryMaxTokens` | 4096 |
| `Chat__SummaryTargetTokens` | 800 |
| `Chat__SummaryMaxSteps` | 8 |

Small local models may need lower `*MaxInputTokens`. The failure reason in the admin work panel says which setting to lower.

## Not in scope
- The chat turn's own prompt is already budgeted (§5.5).
- Retrieval and human-edit embeddings are best-effort. They are unbounded but never block anything.
- No database migration.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

https://claude.ai/code/session_01LEFDEthuQyptxExcmYaHEW
