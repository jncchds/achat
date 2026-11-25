# T5 report — end-to-end check, polish, PR description

Branch `task/05-e2e-polish` (based on `worktree-structured-turn-design`, merged once after it moved).

## Built
- `scripts/e2e/real-envelope.mjs`: real-model driver (phases setup, chat, mood, traits, traits2, stop, memory, recall, retry). It classifies each logged response (valid / fenced / reply-first / plain / broken), checks the stream for JSON or `<think>` artifacts, paces turns and retries 429s through "try again". The API key is read from `KEYFILE` and only sent to the preset API.
- Prompt fix (`PromptAssembly.BuildEnvelopeInstructions`): the `memories` and `traits` wording is stricter, after measuring on a real model that it recorded nearly every turn. Test: `Instructions_tell_the_model_what_not_to_remember_or_turn_into_a_trait`.
- Interaction viewer shows envelope **Notes** (`InteractionDetail` gained `notes`, `recalledMemoryIds`).
- README e2e section lists the new script; `pr-description.md` written with the real numbers.

## Real-model run (throwaway stack `achat-e2e` on 5002)
Setup: OpenRouter free models only — `nvidia/nemotron-3-nano-omni-30b-a3b-reasoning:free` (chat + utility), `nvidia/nemotron-3.5-embed-1b:free`; `Chat__HistoryTurns=4`; second run `Turn__MoodMinMinutes=2`. `google/gemma-4-*:free` returned 429 all session. Before the instruction to stop using LM Studio, a first pass ran on local `google/gemma-4-31b-qat` (chat, mood, traits, stop only: embeddings there cost a ~4 min model swap per call).

| Measure | Result |
|---|---|
| Valid envelope / reply first (25 ordinary turns) | nemotron 25/25 and 25/25 (two prompt versions); gemma-31b local 25/25 |
| JSON artifacts in stream / stored reply; thinking leak | none; none (gemma: reasoning on 25/25 turns, all in `reasoning_content`) |
| Memories before → after prompt fix (25 turns) | stored on 23 → 16 turns (26 → 16 rows); trivia like "User asked 12×12" mostly gone, near-duplicates remain |
| Recall far outside the window | 3/3 |
| Mood | ordinary chat: 1 applied change (initial); emotional exchange: 1 change, then held; damper noted a blocked change |
| Traits before → after fix | 3 added in ordinary chat → 0; creator conversation: 3 added in 18 turns, rest dropped by the daily limit (`TraitAddDropped` in Notes) |
| Revert | works (empty list, then restored) |
| Stop mid-stream, try again | works; the partial reply is replaced on retry |

Not run: a second user's conversation against a real model (covered by `traits.mjs` on the mock). No parsing change was needed: no garbled envelope in 50+ real turns.
Screenshots (chat with mood + footers, memories, personality with trait list and version history, interaction detail with Notes), light and dark, sent to the user.

## Tests
`dotnet build` 0 warnings; `dotnet test` 125 passed; `npx tsc -b --noEmit` clean.

## Cleanup
Stack, volume and image removed, builder cache pruned, build server shut down. The OpenRouter key was never written to a file in the repo, a log or a report.

## Open
- Near-duplicate memories ("making soup tonight" twice) are not merged by the dedup threshold; a consolidation or lower threshold could address it.
- Small free models over-propose traits; guardrails hold. Quality of bot-written traits depends on the model.
