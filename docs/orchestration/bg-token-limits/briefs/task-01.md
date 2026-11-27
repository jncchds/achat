# T1 — Consolidation: reproduce and fix token-limit failures

Worktree `/mnt/git/achat/.claude/worktrees/task-01`, branch `task/01-consolidation-tokens`.
Read: `plan.md`; `src/AChat.Api/Services/Work/EvolutionWorker.cs`, `PersonaTraits.cs`, `WorkQueue*.cs`,
`SummaryWorker.cs` (it already handles `finish_reason=length`; reuse its approach), the OpenAI-compatible
provider's error handling, `scripts/e2e/mockllm.mjs`, `scripts/e2e/traits.mjs`.

## Goal
Consolidation completes (or fails *usefully*) for any trait list size and any model, including
small-context and reasoning models.

## Scope
1. **Reproduce first.** Extend `mockllm.mjs` with two modes: reject prompts over N tokens with an
   OpenAI-style context-length error (HTTP 400), and answer with `finish_reason=length` and empty or
   cut-off content. Add failing tests (or an e2e script) for each before fixing.
2. **Input budget.** The consolidation prompt must be bounded: compute a token budget (config
   `Evolution:MaxInputTokens`, default conservative; use the preset's context size if the model/preset
   exposes one — check `LlmPreset`/`ListModelsAsync`), and when the traits do not fit, consolidate in
   chunks (oldest/lowest-value first) or trim, never send an unbounded prompt. Core persona and
   directives in the prompt count against the budget.
3. **Cap human edits.** Close the open T3 item: human trait add/edit enforces the same count and
   total-chars caps as the envelope (`Turn:MaxTraitChars`, `Turn:MaxTraitsTotalChars`), with a clear 400.
4. **Output cap.** Raise/derive `Evolution:MaxTokens` so thinking models have room; detect
   `finish_reason=length` explicitly (as SummaryWorker does) and fail with an actionable message,
   not "not a non-empty JSON array". Strip inline `<think>…</think>` before parsing.
5. **Retry classification.** A provider context-length error or a truncation is deterministic. Do not
   burn all `Work:MaxAttempts` on an identical request: shrink the input and retry once within the
   attempt, else dead-letter immediately with the reason visible in the admin work panel.
6. Keep §4.4 logging for every call, including the shrunk retry.

## Tests
xUnit for the budgeter/chunker, think-stripping, error classification, the human-edit caps, and the
handler against a fake provider (context error → shrink → success; truncation → actionable failure).

## Runtime check
`mockllm.mjs` modes against a throwaway instance on 5002 via an e2e script (extend `traits.mjs` or add
`consolidation-limits.mjs`): a 60-trait bot consolidates under a small mock context limit.

## Real model (user approved: run the app and test the jobs via OpenRouter)
Free OpenRouter models only (e.g. `nvidia/nemotron-3-nano-omni-30b-a3b-reasoning:free`, embeddings
`nvidia/nemotron-3.5-embed-1b:free`). The key is NOT in the repo or env. If the orchestrator has
passed a key file path in your prompt, read it only to set the preset through the API, never write it
into the repo, a log, or a report, and delete any copy you make. If there is no key, finish everything
mock-based and say clearly the real-model run was not done.

## Docs
Report; update design.md §2.4 / §12b notes only where behaviour changed.
