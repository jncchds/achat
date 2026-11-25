# T3 report — trait list and `traits` field

Branch `task/03-traits` (based on `worktree-structured-turn-design`).

## Built
- `BotTraits` table (Id, BotId, Text, CreatedAt, SourceMessageId, RetiredAt, RetiredByMessageId) + `PersonaVersion.Snapshot` (JSON of active items); migration `AddBotTraits` made with `dotnet ef`. New sources `BotTurn`, `Consolidation`.
- `Services/PersonaTraits.cs`: pure guardrails (`Plan`), render, snapshot, `ShouldConsolidate`, `ParseConsolidation`; DB helpers (apply bot changes, record version, restore for revert, legacy conversion).
- Envelope `traits {add, retire}` parsed leniently; instructions offer it only when `PersonaTraits.CanChange` (creator + `EvolutionEnabled`); prompt shows `[id] text` (ids only for the creator who can retire), order core → evolved → directives unchanged.
- Guardrails: `Turn:MaxTraitChangesPerDay`=3, `Turn:MaxTraits`=20, `Turn:MaxTraitChars`=200 (per item), **`Turn:MaxTraitsTotalChars`=2000** (the brief reused the name MaxTraitChars for the total; deviation), cooldown after human edit/revert via `Evolution:CooldownHours`. Dropped changes → `LlmInteraction.Notes`; applied only for a valid envelope.
- Endpoints (owner only): `GET/POST /persona/traits`, `PUT/DELETE /persona/traits/{tid}`, versions now carry `items`, revert restores the snapshot (ids kept). Old `PUT /persona` (prose) removed. Interaction detail now exposes `notes`.
- Personality page: item list (edit/add/remove), version history with +/− diff, view list, revert. Chat footer "🌱 changed how they see themselves — see personality"; `chat.completed.traitChanges {added, retired}` (creator only).
- Evolution worker → consolidation: arms when active items > `Turn:ConsolidateAbove` (12) and cooldown passed (newest version, any source); no watermarks; one utility call, `PersonaEvolution`, `Consolidation` version; invalid result → `WorkOutcome.Fail` (retry). `Evolution:MinNewMessages`/`MaxTailMessages` removed; `Evolution:MaxTokens` default 512 → 1024. `Bot.EvolutionUpToMessageId` left for T4.
- **Legacy**: first read (`EnsureLegacyAsync`) turns the newest prose version into one trait item and backfills that version's snapshot; older prose versions are read as a single item (id 0) on revert.
- Also fixed a latent UI bug: `patchStreaming` read `streamingKey` inside the state updater, after the handler cleared it, so the completed-turn footers (recalled/remembered/traits) could be lost.

## Tests
`dotnet test`: 124 passed, 0 failed (new: `TraitTests`, envelope trait parsing/offer tests, consolidation arming tests replacing `ShouldEvolve` ones). `dotnet build`, `npx tsc -b --noEmit` green.

## Runtime (throwaway stack `achat-t3` on 5002 + `mockllm.mjs`)
`scripts/e2e/traits.mjs` PASSED (14 checks: creator add → row + BotTurn version + footer payload; id shown in prompt; second user adds nothing and isn't offered the field; human edit → bot trait dropped, noted; revert restores). It needs the default 12 h cooldown — my last re-run happened on an instance I had set to cooldown 0 and failed 3 checks for that reason only. Consolidation verified live (ConsolidateAbove=2, cooldown 0): 4 items → 2, `Consolidation` version. Screenshots taken in light and dark. Stack, volume, image and build cache removed.
Mock changed: `[[trait:TEXT]]`/`[[retire:ID]]` markers produce envelope replies; consolidation request returns a JSON array.

## Open
- `phase5.mjs`/`phase5-mock.mjs` still assert the old prose evolution pass and the `PUT /persona` endpoint — obsolete, for T4 to retire/update.
- Humans' trait add/edit isn't capped by count/total (only per-item length).
- Retired items stay in `BotTraits` forever (needed for revert).
