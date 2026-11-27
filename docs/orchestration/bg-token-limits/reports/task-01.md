# T1 report — consolidation under token limits

Branch `task/01-consolidation-tokens` (worktree `.claude/worktrees/task-01`), based on `worktree-bg-job-token-limits`.

## Built
- `TokenLimits` (new, `Services/Work/TokenLimits.cs`): context-length error classifier (HTTP 400/413/422 + known texts), `StripThinking` (`<think>…</think>`, unclosed block, lone `</think>`), `Chunk` budgeter (order kept, oversized item alone).
- `EvolutionHandler`: input budget `Evolution:MaxInputTokens` (default 4000; fixed prompt counts; no preset context size exists in `LlmPreset`/`ListModelsAsync`, so it is config only). Traits are consolidated in chunks (oldest first); all chunks must succeed before the list is replaced (no half-applied version). `Evolution:MaxTokens` 1024 → 4096. `finish_reason=length` is detected explicitly (content never parsed). A context-length error or truncation splits the chunk in two and retries once within the attempt; otherwise `WorkOutcome.FailPermanent` → the item is dead-lettered on attempt 1 with the reason in `LastFailure` (admin work panel). A core persona + directives that alone exceed the budget fails permanently with a §4.4 row. Every call, including the shrunk retries, is logged (`HttpStatus` now set too). 5xx and bad JSON stay retryable.
- `WorkOutcome.Permanent` + `WorkQueue.RecordOutcomeAsync` dead-letter immediately (usable by T2 for the summary pass).
- Human trait add/edit: `PersonaTraits.CheckHumanEdit` enforces `Turn:MaxTraits`, `MaxTraitChars`, `MaxTraitsTotalChars` with a 400 message; an edit that does not grow the list/total is always allowed.
- `mockllm.mjs`: `MOCK_MAX_PROMPT_TOKENS` / `MOCK_TRUNCATE=empty|cut` and runtime `POST /_mode`. New `scripts/e2e/consolidation-limits.mjs`. README + design.md §2.4 updated.

## Tests
`dotnet test AChat.slnx`: 145 passed (20 new, `ConsolidationLimitsTests`): classifier, think-stripping, chunking, permanent dead-letter, human caps (unit + controller 400), handler vs fake provider (single call, chunked/bounded prompts, context error → shrink → success, unfixable context error → permanent and nothing lost, truncation → actionable permanent, cut-off content never parsed, oversized core, retryable cases). Reproduce-first: the failing cases (unbounded prompt, truncation reported as parse error, 5 identical retries) were taken from the old code paths; tests were written together with the fix, not committed red.

## Runtime (Docker, throwaway instance on 5002, project `achat-t1`)
`consolidation-limits.mjs`, all checks passed: A 60 traits → chunked (3 calls, none failed); B mock context 600 < budget → 2 rejected calls (HTTP 400, logged) then success; C truncation → Dead after 1/3 attempts, reason names `finish_reason=length` and `Evolution:MaxTokens`, traits untouched; D admin retry succeeds; E human over-length trait → 400. F real model `nvidia/nemotron-3-nano-omni-30b-a3b-reasoning:free` via OpenRouter (key from the orchestrator's file, only sent to the preset API, preset deleted afterwards): 40 traits → 1 trait, 2 calls, 0 failed, `finish_reason=stop`. `npx tsc` not run (no UI change).

## Decisions / open
- `MaxInputTokens` is config, not derived from the model (no context size available). Default 4000 is conservative; small local models may need less.
- Chunked consolidation merges per chunk only; the concatenation is not re-consolidated in the same pass (next cooldown cycle will).
- Human caps reject adds at 20 traits / 2000 chars by default, same as the bot — an admin wanting more must raise `Turn:*`.
- T2: reuse `WorkOutcome.FailPermanent`, `TokenLimits.IsContextLengthError/StripThinking` for `SummaryWorker`.
- The machine restarted mid-run and was heavily loaded by other sessions once (DB connection timeouts in the throwaway instance); reran cleanly.
