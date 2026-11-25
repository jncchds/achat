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
- **Real model: PENDING.** `scripts/e2e/real-envelope.mjs` is written (25 ordinary turns with 3 planted
  facts and a repeat, recall far outside the window, an emotional exchange for mood, a creator
  conversation for traits, Stop + follow-up) but could not run: the LM Studio host at
  `192.168.2.33:1234` lists its models yet fails to load any of them ("Model is unloaded", "Operation
  canceled", "Engine protocol startup was aborted"), and no OpenRouter key was available. Fill in
  once run:

  | Measure | Result |
  |---|---|
  | Turns / valid envelope / reply first | _pending_ |
  | Invalid shapes seen | _pending_ |
  | Thinking leaked into reply | _pending_ |
  | Memories planted → stored → deduped → recalled | _pending_ |
  | Mood changes over ordinary chat / after emotional exchange | _pending_ |
  | Trait changes (creator) / guardrail drops / revert | _pending_ |

## Known limits
- Real-model behaviour (validity rate, prompt wording, mood/trait restraint) is unmeasured.
- Inline `<think>…</think>` in `content` (reasoning models that don't split it out) is not stripped;
  it would make the envelope invalid and show raw text. Decide after the real run.
- No `response_format` / JSON mode: LM Studio rejects `json_object`; add per provider only with data.
- Humans' trait adds/edits aren't capped by count/total; retired items stay in `BotTraits` for revert.
- `WorkKind.Extraction`, `InteractionPurpose.MemoryExtraction`, `MemorySource.Extraction` are kept for old rows.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

https://claude.ai/code/session_01LEFDEvpVMnLkqozcJfznsp
