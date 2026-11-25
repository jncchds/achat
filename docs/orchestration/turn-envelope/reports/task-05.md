# T5 report — end-to-end check, polish, PR description (partial: real-model run pending)

Branch `task/05-e2e-polish` (based on `worktree-structured-turn-design`).

## Done
- `scripts/e2e/real-envelope.mjs`: real-model driver (phases setup/chat/recall/mood/traits/stop). It classifies each interaction's `ResponseContent` (valid / fenced / reply-first / plain / broken JSON), checks the stream for JSON or `<think>` artifacts, and checks recall, mood and trait counts. Only `setup` and the first (failed) turns have run.
- Polish: the interaction viewer now shows **Notes** (`InteractionDetail` carries `notes`, `recalledMemoryIds`). Other stale comments and UI copy were already cleaned in T4 (grep: only intentional legacy names remain).
- `pr-description.md` written; the real-model table is left `pending`.

## Blocked: real-model run
- LM Studio (192.168.2.33:1234) lists models but every one is `not-loaded`: qwen3.8-27b → "Model is unloaded.", gemma-4-12b → "Operation canceled", qwen3.6-35b-a3b → "Engine protocol startup was aborted"; embeddings fail too. The app showed these as failed turns (failure handling works: no side effects, error shown).
- No OpenRouter-NV key reachable (no 5001 stack or volume, none in the env).
- Not run: valid-envelope rate, thinking leakage, memories, mood, traits, stop/try-again, screenshots.
- No parsing or prompt changes: nothing measured to justify them.

## Tests
`dotnet build` 0 warnings; `dotnet test` 124 passed; `npx tsc -b --noEmit` clean.

## Cleanup
Stack `achat-e2e` (port 5002), its volume and image removed; build server shut down.

## To resume
Load qwen/qwen3.8-27b and `text-embedding-nomic-embed-text-v1.5`, bring up a throwaway stack on 5002, then from `scripts/e2e` (`npm ci` first):
`ACHAT_BASE=http://localhost:5002 OUT=out.json node real-envelope.mjs`. Then fill the PR table and take the screenshots.
