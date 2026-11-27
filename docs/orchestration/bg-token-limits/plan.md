# Plan — background jobs that update the bot's personality fail with "token exceeded"

## Problem
The passes that maintain the bot's personality fail with a token-limit error: trait
consolidation (`EvolutionHandler`, `src/AChat.Api/Services/Work/EvolutionWorker.cs`) and,
the same shape, the conversation summary (`SummaryWorker.cs`). They go through the durable
queue (§12b), retry, then dead-letter. Not yet confirmed which error text (context window
exceeded on input vs. output cap hit, `finish_reason=length`); the app was not running when
this was planned, so T1 settles it.

## Hypotheses from reading the code
1. **Input unbounded.** The consolidation brief lists *every* active trait plus core persona
   and directives with no budget; human trait add/edit has no count/total cap (open since
   T3). Small-context local models reject the prompt ("context length exceeded").
2. **Output cap too low for reasoning models.** `Evolution:MaxTokens` is 1024 and
   reasoning models (Qwen3) spend it on thinking, so the JSON array is cut off
   (`finish_reason=length`) and `ParseConsolidation` fails. The summary worker already
   detects this; the evolution worker does not.
3. **Retry is blind.** A token failure is deterministic; retrying the same request 5 times
   cannot succeed.

## Decisions (defaults, change on request)
- Fix in code, not by documenting config: budget the input from the preset's context size,
  and make truncation a first-class, non-retried outcome.
- No new user-facing UI beyond what is needed to show the failure reason.
- Keep §4.4 logging of every call; failures stay visible in the admin work panel.

## Task series
| # | Task | Brief |
|---|---|---|
| T1 | Reproduce with `mockllm.mjs` (context-exceeded + length-truncated modes), write a failing test per cause, fix consolidation: input budget (cap/trim traits fed to the model, shared cap for human edits), output-cap handling incl. reasoning models, classify token errors as non-retryable-as-is | `briefs/task-01.md` |
| T2 | Same treatment for `SummaryWorker` (shared helper), review other passes (memory writer) for the same failure, docs (`design.md` §4.5/§12b, README, RELEASE_NOTES), e2e script, real-model run | `briefs/task-02.md` |
