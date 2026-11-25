Task T3 (trait list and `traits`) of the AChat turn-envelope series. An orchestrator session runs the series one task at a time, with one session per task.

First, read the worker rules and follow them throughout. They live only on the integration branch, so before your worktree exists read them with `git show worktree-structured-turn-design:docs/orchestration/turn-envelope/briefs/common.md` (cwd `/mnt/git/achat`).

- Worktree: `/mnt/git/achat/.claude/worktrees/task-03`
- Branch: `task/03-traits`, based on the local `worktree-structured-turn-design` branch.
- Read: `docs/design.md` §4.7 and §2.4; `src/AChat.Api/Services/{ChatService,PromptAssembly,TurnEnvelope,MemoryWriter}.cs`; `src/AChat.Api/Services/Work/EvolutionWorker.cs`; `PersonaVersion`/`Bot` models; the personality page (`src/achat-ui/src/pages/*Settings*`) and its controller endpoints. T1/T2 are already merged — their reports are the commit messages (`bf4ce22`, `5ef0c57`).

## Goal
The bot can adjust its own evolved personality from inside a turn: the evolved layer becomes a list of short trait items, the envelope gains `traits: {add: [..], retire: [ids]}`, and every change is a versioned, revertable `PersonaVersion`. The old prose-rewrite evolution pass becomes a slow consolidation of that list.

## Scope
1. **Trait items.** New table (e.g. `BotTraits`: Id, BotId, Text ≤ `Turn:MaxTraitChars` (200), CreatedAt, SourceMessageId, RetiredAt null = active). Migration with `dotnet ef`. Existing prose evolved content must not be lost: the migration/first read turns the current prose version into one legacy trait item (document how in the report).
2. **Versioning stays.** Every change (bot turn, human edit, revert, consolidation) creates a `PersonaVersion` carrying a snapshot of the active items (JSON) plus the rendered text; new sources `BotTurn` and `Consolidation`. Revert restores the active set from a version's snapshot, and the revert itself is a new version. History/diff keep working.
3. **Prompt.** The evolved block renders active items with their ids (e.g. `[12] teases them about their cooking`) so the bot can retire by id. Core → evolved → directives order is unchanged.
4. **Envelope `traits`.** Extend `TurnEnvelope` parsing (lenient: non-int ids, empty strings and duplicates skipped) and the instruction text (`BuildEnvelopeInstructions`): traits are rare, slow, about the friendship, short, and can be retired. The field is offered **only** when the user is the bot's creator (`bot.OwnerId == userId`) **and** `Bot.EvolutionEnabled`; otherwise it is not in the instructions and is ignored if returned.
5. **Guardrails** (config, README table, defaults): `Turn:MaxTraitChangesPerDay` = 3 (adds + retires per bot, rolling 24 h); `Turn:MaxTraits` = 20 active items and `Turn:MaxTraitChars` total cap = 2000 characters (over the cap, adds are refused until something is retired); trait changes are also dropped during `Evolution:CooldownHours` after a human edit or revert (a correction must not be overwritten next turn). Dropped changes go in `LlmInteraction.Notes`, never queued. Applied only for a complete, valid envelope; a retire of an unknown/inactive id is ignored.
6. **Personality page.** The evolved section becomes a list: edit text, add, remove (retire) items, still with version history and revert. A human change creates a `UserEdited` version. Follow the existing page style; no mockup round needed since it is the same page.
7. **Creator footer.** `chat.completed` gains `traitChanges {added: [..], retired: [..]}` (creator only); the chat shows a small line like the existing "noted N things" one ("🌱 changed how they see themselves — see personality") linking to the personality page.
8. **Evolution worker → consolidation.** Rework `EvolutionProducer`/`EvolutionHandler`: no message watermarks. Arm only when active items > `Turn:ConsolidateAbove` (default 12) and the cooldown has passed (reuse `Evolution:CooldownHours`, newest version of any source). One utility-model call rewrites the list into fewer, merged, de-duplicated items (never touches core or directives; core + directives passed as constraints); logged as `PersonaEvolution`; stored as a `Consolidation` version; result must be non-empty JSON of strings, otherwise the pass fails (§12b retry). Leave `Bot.EvolutionUpToMessageId` in place (T4 removes it).

## Tests
- `TurnEnvelope` traits parsing (lenient cases), prompt offers `traits` only to the creator with evolution on.
- Guardrail logic as pure functions where possible (daily cap, size cap, cooldown, unknown id, creator-only), plus DB-backed tests in the existing style (see `Phase8Tests.cs`/`SharingTests.cs`) for apply + version + revert round trip.
- Consolidation arming and result validation.
- All existing tests still pass.

## Runtime check
Bring up a throwaway instance on port 5002 with `mockllm.mjs` (see `scripts/e2e/phase5-mock.mjs` for the pattern): a creator turn whose mock reply adds a trait → a `BotTrait` row, a `BotTurn` version and the footer; a second user's turn with the same reply adds nothing; a human edit then an immediate bot trait is dropped (cooldown) and noted; revert restores the list. Add this as `scripts/e2e/traits.mjs` and mention it in the AGENTS.md script list. Bring the stack down and clean up afterwards.

## Docs
`docs/design.md` §2.4 (evolved layer is now a list; consolidation) and §4.7 (mark step 3 implemented, note any deviation). README config rows. The report.

Screenshots (SendUserFile, display: render): the personality page with trait items and history, and the chat footer line, in light and dark.

When you're done, follow "Finishing" and "Waking the orchestrator" in common.md.
