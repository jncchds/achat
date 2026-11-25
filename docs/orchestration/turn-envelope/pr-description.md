# Structured turn envelope (§4.7)

## Summary
Every chat turn now returns one JSON object — `{"reply", "mood", "memories", "traits"}` — instead of
plain prose plus separate background passes. The reply streams live (only the `reply` string is
shown), and the same call carries the bot's mood, what it wants to remember, and (in its creator's
conversation) changes to its own traits. This replaces the background memory-extraction worker and
the prose "evolution rewrite". A reply that isn't a valid envelope is still delivered as plain text
and applies **no** side effects.

## What changed, by area
- **Chat turn** (`ChatService`, `TurnEnvelope`, `ReplyStreamer`, `PromptAssembly`): envelope
  instructions in the system block; tolerant parse (code fence, trailing commas); an incremental
  reader streams `reply` unescaped; garbled output falls back to the streamed/raw text.
- **Mood**: per-conversation state, damped (changes only after `Turn__MoodMinTurns` user turns *and*
  `Turn__MoodMinMinutes`); shown in the chat toolbar; `GET /api/bots/{id}/mood`.
- **Bot memories**: the envelope's `memories` are embedded, deduplicated/superseded and stored as
  `BotRemember`, capped by `Turn__MaxMemories`; recalled as before. The extraction worker is gone.
- **Traits**: the evolved persona layer is a list (`BotTraits`) the bot adds to and retires from,
  creator conversation only, with guardrails (per-day, count, length, total), a cooldown after human
  edits, versioned snapshots and revert, and a slow consolidation pass above `Turn__ConsolidateAbove`.
  Core persona and directives are unreachable from the envelope. Endpoints under
  `/api/bots/{id}/persona/traits`; `PUT /persona` (prose) removed.
- **UI**: mood in the toolbar, message footers (recalled / remembered / trait change), personality
  page as a trait list with version diffs and revert, interaction viewer shows envelope **Notes**.
- **Interaction log (§4.4)**: `LlmInteractions.Notes` records what the envelope did or ignored.
- **Removed**: extraction worker, prose evolution pass, `ExtractionUpToMessageId` /
  `EvolutionUpToMessageId`, `Extraction__*` and `Evolution__MinNewMessages`/`MaxTailMessages`.
- **Docs/scripts**: design §2.4/§4.7/§5.3/§12b, README, RELEASE_NOTES, e2e scripts rewritten for the
  envelope (`phase4`, `phase5-mock`, `traits`, `workqueue`, …), `real-envelope.mjs` added.

## Configuration
Added: `Turn__MaxMemories` (3), `Turn__MaxTraitChangesPerDay` (3), `Turn__MaxTraits` (20),
`Turn__MaxTraitChars` (200), `Turn__MaxTraitsTotalChars` (2000), `Turn__ConsolidateAbove` (12),
`Turn__MoodMinTurns` (6), `Turn__MoodMinMinutes` (30). Changed default: `Evolution__MaxTokens` 1024.
Removed: `Extraction__*`, `Evolution__MinNewMessages`, `Evolution__MaxTailMessages`.

## Migrations
`AddConversationMoodAndInteractionNotes`, `AddBotTraits`, `DropWatermarks` (all made with `dotnet ef`,
applied automatically on start). Existing prose personas become one legacy trait item on first read;
existing memories are untouched.

## How it was verified
- `dotnet build` 0 warnings; `dotnet test` 124 passed; `npx tsc -b --noEmit` clean.
- Mock-LLM e2e in Docker (T3/T4): `phase5-mock` 13/13, `traits` 14/14, `phase4` 9/9, `phase6` 14/14,
  `phase8-verify` 8/8, `workqueue` 11/11.
- **Real model** (`scripts/e2e/real-envelope.mjs`, throwaway stack on 5002, `Chat__HistoryTurns=4`,
  `Turn__MoodMinMinutes=2` so the mood damper can be exercised on a test timescale; 3 000 max tokens).
  Main run: OpenRouter **free** models only — chat/utility `nvidia/nemotron-3-nano-omni-30b-a3b-reasoning:free`,
  embeddings `nvidia/nemotron-3.5-embed-1b:free` (`google/gemma-4-*:free` answered 429 for the whole
  session). A first pass was also run, before it was ruled out, on a local LM Studio
  `google/gemma-4-31b-qat` (a reasoning model that returns `reasoning_content`).

  | Measure | Result |
  |---|---|
  | Valid envelope, `reply` first, over 25 ordinary turns | nemotron-nano **25/25** (both prompt versions); gemma-4-31b local **25/25**. No fences, no plain text, no broken JSON seen |
  | JSON artifacts in streamed deltas / stored reply | none |
  | Thinking leaked into reply | none (gemma-4-31b: reasoning on 25/25 turns, all in `reasoning_content`; the free nemotron route returns no reasoning field) |
  | Memories, nemotron, **before** prompt fix | stored on 23/25 turns (26 rows): "User asked what 12 times 12 is", "User thanked me", "User says goodnight" |
  | Memories, **after** prompt fix | stored on 16/25 turns (16 rows; model still proposed on 18) — trivia mostly gone, near-duplicates ("making soup tonight" twice) are not merged |
  | Recall far outside the 4-turn window (planted 4 facts, 4 filler turns) | **3/3** (cat's name, wedding city, instrument), memory ids in the footer |
  | Mood, ordinary chat | 1 applied change (initial mood); the model echoed its current mood on 21/25 turns instead of `null` (harmless, no change) |
  | Mood, emotional exchange | 1 change ("deeply saddened"), then held for 5 turns; an earlier quicker shift was `MoodDamped` (noted) |
  | Traits, **before** prompt fix | 3 added during 25 ordinary turns, 10 more proposals dropped by the daily limit |
  | Traits, **after** prompt fix | 0 in 25 ordinary turns; creator conversation: 3 added over 18 turns, 3 more dropped by the daily limit; one near-duplicate of an existing item |
  | Revert | restored the empty list; re-revert restored the 3 items |
  | Stop mid-stream → "try again" | stopped (partial discarded), retry completed a clean reply, deltas clean |

  A garbled envelope never occurred in 50+ real turns, so no parsing change was made.

## Known limits
- Small free models are over-eager to record memories and traits; the prompt wording was tightened
  (see above) but a stronger model is still advisable. The guardrails (3 memories/turn, 3 trait changes/day) held.
- Near-duplicate memories ("making soup tonight" / "planning to make soup tonight") survive the dedup threshold.
- Inline `<think>…</think>` in `content` (reasoning models that don't split it out) is not stripped;
  it would make the envelope invalid and show raw text. Not seen with either model tested.
- No `response_format` / JSON mode: LM Studio rejects `json_object`; with 100% validity measured there is no case for it.
- A single-model local server (LM Studio) swaps models on every chat/embedding switch (~4 min) and breaks the 120 s default timeout.
- Humans' trait adds/edits aren't capped by count/total; retired items stay in `BotTraits` for revert.
- `WorkKind.Extraction`, `InteractionPurpose.MemoryExtraction`, `MemorySource.Extraction` are kept for old rows.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

https://claude.ai/code/session_01LEFDEvpVMnLkqozcJfznsp
