# Agentic Chat (AChat) — Design Document

> Status: **DRAFT / in progress.** Sections marked `[OPEN]` are unresolved and need a decision
> before implementation starts. Sections marked `[DECIDED]` are locked.

---

## 1. Purpose & Scope

### 1.1 What AChat is

AChat is a self-hosted platform that hosts **virtual friends** — chat bots that have a
persistent *personality* and a persistent *memory* of their relationship with the user.
The emphasis is not on task completion (as with a coding or productivity assistant) but on
continuity: a bot should remember who the user is, what has been discussed before, and
behave consistently over long spans of time.

### 1.2 Deployment context

Self-hosted by a technically capable owner as a **docker compose stack** (AChat + PostgreSQL
with pgvector + any other supporting services). Not a SaaS; no hosted multi-tenant offering.

### 1.3 Non-goals `[PROPOSED]`

- No hosted or cloud offering, no billing, no tenancy beyond per-user data ownership.
- No tool calling or function calling (§4.6).
- No proactive/unprompted messaging (§2.3).
- No group chats — every conversation is one user and one bot (§2.3).
- No voice input or output.
- No image generation, and no image understanding.
- No native mobile apps; responsive web only.
- No federation or interoperability with other bot platforms; no character-card import (§2.4).

### 1.4 MVP definition `[PROPOSED]`

**v1 is Phases 0–5 of §12a**: a self-hosted stack where a user can define a friend, hold a
continuous time-aware conversation with it against an OpenAI-compatible endpoint, have it
remember specifics from far outside the context window, inspect and correct both what it
remembers and every call made on their behalf, and watch its personality shift with the
relationship.

Sharing (Phase 6) and the remaining providers (Phase 7) are deliberately outside v1 — they
widen an already-working product rather than completing it.

`[OPEN]` — confirm this boundary. Pulling sharing into v1 is the most likely amendment.

---

## 2. Domain Model

### 2.1 Core entities

| Entity | Notes | Status |
|---|---|---|
| `User` | Owns everything below. Admin flag. | `[DECIDED]` |
| `LlmPreset` | Per-user. Provider + endpoint + key + models. See §3. | `[DECIDED]` |
| `Bot` (virtual friend) | Per-user. References **exactly one** `LlmPreset` belonging to its creator. | `[DECIDED]` |
| `Conversation` | `[OPEN]` — see §2.3 | `[OPEN]` |
| `Message` | Turn in a conversation. | `[OPEN]` |
| `Memory` | Embedded fact/episode owned by a bot. See §5. | `[DECIDED]` |
| `LlmInteraction` | Audit record of every LLM call, success or failure. See §4.4. | `[DECIDED]` |

### 2.2 Ownership rules `[DECIDED]`

- Every `LlmPreset` and every `Bot` belongs to exactly one `User` — its **owner**.
- A `Bot` uses exactly one `LlmPreset`, and that preset must belong to the bot's owner.
- A user's `LlmPreset`s are private and never visible to anyone else.
- Bots are private by default but **can be shared** with other users via invite codes (§2.5).

Consequences to design for: what happens when a preset referenced by a bot is deleted
(block deletion / cascade / soft-detach)? `[OPEN]`

### 2.5 Bot sharing `[DECIDED — details `[OPEN]`]`

A bot owner can share a bot with other users by issuing an **invite code**. Redeeming a code
grants the invitee access to *talk to* the bot. It does **not** make them a bot admin:
persona, preset, and configuration remain the owner's alone.

This makes `Bot` a shared resource and introduces a join entity — call it `BotAccess`
(bot, user, role, granted-at, granting invite code).

**Roles** `[DECIDED]` — exactly two, held through `BotAccess`:

| Role | Who | Can |
|---|---|---|
| **Bot admin** | The creator, exactly one | Everything a user can, plus: edit personality, choose the preset, issue and revoke invite codes, delete the bot |
| **User** | Everyone who redeems an invite code | Talk to the bot; see and manage *their own* memories and conversation |

The creator is simultaneously the bot admin **and** its default user — they have their own
`(BotId, UserId)` memory scope like anyone else.

**Memory is per-user** `[DECIDED]` — each user has a separate memory scope with the bot. The
memory key is `(BotId, UserId)`. Nothing one user tells the bot can surface in another user's
conversation.

**Personality is creator-owned** `[DECIDED]` — defined and influenced solely by the bot admin.
It is the one thing shared identically across all users of the bot. A user cannot alter how
the bot behaves; only what it knows about them.

This gives a clean split, worth stating explicitly because it drives the whole prompt
assembly in §5.4: **persona is global to the bot, memory is local to the relationship.**

`[OPEN]` — remaining sharing details:

1. **Who pays.** The bot runs on the admin's preset, so an invitee's conversations consume the
   admin's API key and quota. Presumed intended. Do admins get per-user usage visibility,
   quotas, or a kill switch?
2. **Privacy vs. the interaction log.** §4.4 stores full request and response text for every
   call, and those calls run on the admin's preset. If the admin can browse the interaction
   log for their own bot, they can read every invitee's conversation regardless of what the
   chat UI exposes. Either scope the log viewer to system admins only, or state plainly on
   the invite screen that the bot admin can see conversations. Do not imply a confidentiality
   the architecture does not provide.
**Invite codes** `[DECIDED]` — one code per bot, a GUID, assigned once at creation and stable
thereafter. It is a **request token, not a grant**:

1. The admin shares the bot's code.
2. A user enters it. This creates an `AccessRequest` (bot, requesting user, timestamp,
   status) — it does **not** grant access.
3. The admin sees pending requests and approves or rejects each one. Approval creates the
   `BotAccess` row.

The code being permanent is safe precisely because it only opens a request: a leaked code
costs the admin a rejection, not an unwanted user. It also means no expiry logic, no reissue
flow, and no single-use bookkeeping — the approval step carries all the security.

`[OPEN]` — remaining sharing details:

3. Can the admin **regenerate** a bot's code if it leaks widely enough to be a nuisance?
4. **Revocation.** Does revoking access delete that user's conversation and memories, or
   retain them so re-approval restores the relationship? (Retention is friendlier; deletion is
   the stronger privacy answer.)
5. **Re-sharing.** Anyone can pass the code along, since it is just a string — but only the
   admin approves. This seems correct; confirm.
6. **Visibility.** Is a shared bot badged in the invitee's list, or indistinguishable from
   their own?
7. **Deletion.** What happens to invitees when the admin deletes the bot or their account?
8. Does a rejected user get told, and can they request again?

### 2.3 Conversation shape `[DECIDED]`

**One continuous timeline per `(BotId, UserId)`.** No threads, no branching, no separate
conversations to create or name — talking to a friend resumes where you left off, exactly like
a messaging app. This is why `ConversationSummary` (§5.2) is one row per pair.

**It is time-sensitive**: the timeline carries real elapsed time, and the bot is made aware of
pauses between messages rather than seeing an unbroken transcript. See §5.9.

`[DECIDED]` — **v1 is 1:1 only.** One continuous timeline per `(BotId, UserId)`, no threads.
Group chat would change the message model and prompt assembly substantially; it is out of
scope for v1. Message editing, deletion, and regeneration are not in v1 either (a later
phase, if at all).

**Proactive messaging** `[DECIDED — not in v1]` — bots do not message users unprompted.
A bot speaks only in response to something: a user message, or (later) an explicit trigger
such as a reminder the user asked for. There is no background worker deciding to say hello.

This significantly simplifies the backend: generation can be tied to the request/connection
lifetime, and background work does not need to be durable or crash-safe in the way autonomous
initiation would demand. Revisit only if reminders land.

### 2.4 Bot definition and personality `[DECIDED — implemented in Phase 5]`

A bot's identity is **three layers**, composed in order into the persona block of every
prompt (§5.5). The split exists because the three have different authors, different
lifetimes, and different rules about who may change them.

| Layer | Author | Changes | Purpose |
|---|---|---|---|
| **Core** | Bot admin, at creation | Only by explicit edit | The anchor identity — who this friend fundamentally is |
| **Directives** | Bot admin, explicitly | Added, edited, removed at will | Deliberate correction: "be more blunt", "never bring up my ex" |
| **Evolved traits** | The system, from creator conversations | Rewritten periodically by a background worker | Drift: how the friendship has actually shaped them |

**Core** — the seed, written once when the bot is created. Name, avatar, and a freeform
description of who they are: background, temperament, how they speak. Freeform prose rather
than structured trait fields, because prose is what actually steers a model well and because
structured fields invite filling in boxes that never affect output. A short "speech style"
hint and an optional greeting are worth having as separate fields since they are used
differently — the greeting is a message, not a prompt fragment.

**Directives** — discrete, individually editable statements, stored as rows rather than one
blob so each can be dated, toggled, and deleted. This is answer (b): the way the admin tells
the bot something that sways it. Directives are injected verbatim and always win over evolved
traits when they conflict, because they represent a deliberate decision.

`[DECIDED]` (Phase 5) — **explicit UI first**, as recommended. Directives are added,
edited, toggled, and removed from the bot's personality page (`/bots/:id/settings`),
stored as rows (`BotDirectives`), each dated. Toggled-off directives are kept (re-enableable)
but not injected. In-conversation capture is **not in v1** — automatic promotion that
misfires is worse than a form.

**Evolved traits** — answer (c). A background worker maintains a short machine-written
document describing how the bot has changed: the shared jokes, the shorthand, the tone the
relationship has settled into.

- Runs on a **slow cadence** — far slower than memory extraction. Triggered by accumulated
  creator exchanges or elapsed time, not by every turn. Personality that shifts hourly is
  noise, not character.
- Reads **only the creator's conversations**, per requirement. Other users' interactions never
  influence it, even though they see the result — the bot's personality is the creator's, and
  invitees meet whoever the creator has shaped.
- Runs on the **utility model** (§3) and is logged as a §4.4 interaction with purpose
  `PersonaEvolution`.
- Is **bounded**: the worker rewrites only this layer. It cannot touch core or directives, and
  it is prompted with both as constraints, so evolution refines within the identity rather
  than replacing it.

**Versioning is required, not optional.** Every rewrite of the evolved layer is stored as a
version with a timestamp and the interaction that produced it. The admin can read the history,
diff it, and revert. A personality that drifts somewhere unwanted with no undo is a bad
failure mode — and slow, cumulative drift is exactly the kind you notice late.

The admin can also edit the evolved layer directly; an edited version is flagged so the next
evolution pass treats it as the new baseline rather than overwriting a human correction.

`[DECIDED]` (Phase 5) — implemented as `PersonaVersions` (one row per version, newest by Id
is current; older versions are never mutated) with sources `Initial` (empty baseline created
with the bot), `Evolution` (worker rewrite, carries the producing interaction's id),
`UserEdited` (admin edit), and `Revert` (admin revert to an earlier version — the revert
itself is visible in the history). The version history is readable and reversible from the
personality page.

`[DECIDED]` (Phase 5) — the worker (`PersonaEvolutionService`) sweeps every 30s but only
acts when **both** hold: ≥ `Evolution:MinNewMessages` (default 40) new **creator** messages
since the last completed pass (watermark `Bot.EvolutionUpToMessageId`), and the cooldown
`Evolution:CooldownHours` (default 12) has elapsed, measured from the newest version of
**any** source — so a human edit or revert also resets it, protecting a correction from an
immediate overwrite. It rewrites only the evolved layer, prompted with core + directives as
fixed constraints, on the utility model, logged as `PersonaEvolution` (§4.4). A pass that
returns the baseline unchanged stores nothing. The watermark advances only on success.
Evolution can be switched off per bot (`Bot.EvolutionEnabled`, toggle on the personality
page) for someone who wants a fixed character.

Resolved `[OPEN]`s — generation parameters stay on the **preset** in v1 (bot-level override
revisited in Phase 8); import/export is **not in v1**; the per-bot evolution switch is
**in**, as above.

Verified end-to-end (real model): after a sustained conversation, the evolved-traits layer
was rewritten from the creator's own material, the next prompt carried the layers in order
(core → evolved → directives), the bot obeyed a standing directive, and the admin read the
history, edited, and reverted.

---

## 3. LLM Presets `[DECIDED]`

Per-user configuration objects describing how to reach a model.

Fields:

| Field | Notes |
|---|---|
| Provider type | `Ollama`, `OpenAI`, `OpenAICompatible`, `GoogleAIStudio` — all four exist as strategy classes in abook and carry over |
| Base endpoint | Blank permitted for providers with a well-known default (e.g. OpenAI) |
| API key | Blank permitted for providers that don't need one (e.g. local Ollama). Stored encrypted — see §7.3 |
| Generation model | The chat/completion model — the bot's actual voice |
| Utility model | A second **generation** model for background chores: memory extraction and summarisation (§5.3). Usually smaller and cheaper than the generation model. Defaults to the generation model when blank |
| Embedding model | Text → vector, for memory search (§5.4). **Locked once a bot uses the preset** (§5.7) |

The three models do different jobs and are not interchangeable: the embedding model produces
vectors and cannot generate text at all; the generation model writes the bot's replies and is
chosen for quality of voice; the utility model generates too, but only for machine-read
structured output nobody sees, so it can be a much cheaper model. Separating utility from
generation matters because extraction runs on every exchange, on the bot admin's key (§5.9.1),
and paying flagship prices to turn chat into JSON is waste.

The preset editor UI includes a **"Load models"** action that queries the provider's model
listing endpoint using the entered endpoint + key and populates the model dropdowns.

`[DECIDED]` — v1 preset fields: **temperature**, **max output tokens**, and **request
timeout** (all editable in the preset editor, defaulted server-side). Context window size,
embedding dimensionality (deferred to §5.4, where it is detected on first embed), and rate
limiting are out of scope for v1.

`[DECIDED]` — validation: a **"Test connection"** action probes the endpoint's model listing
using the preset's endpoint + key (implemented; UI button on the preset editor). A provider
that becomes unreachable mid-conversation surfaces as a **failed LLM interaction** (§4.4)
with the HTTP status and error payload — the user sees a failure notice in chat, the log
carries the detail. No automatic retry in v1.

---

## 4. LLM Integration Layer

### 4.1 Abstraction `[DECIDED]`

Bots never talk to a provider SDK directly. A provider abstraction sits between them —
one interface, one implementation per provider type. All provider-specific request/response
shaping, error mapping, and streaming-event normalisation lives behind it.

`[DECIDED]` — interface surface (implemented as `AChat.Core.Interfaces.ILlmProvider`):

- `ChatAsync(request, CancellationToken)` → non-streaming completion
- `StreamChatAsync(request, CancellationToken)` → `IAsyncEnumerable<Chunk>`
- `EmbedAsync(texts, CancellationToken)` → vectors
- `ListModelsAsync(CancellationToken)` → model catalogue for the "Load models" button

Plus a factory (`ILlmProviderFactory`) that builds a provider from a preset's stored
configuration, and a typed `LlmProviderException` carrying HTTP status + error payload.

### 4.2 Streaming `[DECIDED]`

- Streaming is used **server-side** wherever the provider supports it.
- If the client supports streaming, tokens are relayed to it as they arrive.
- **SignalR** is the web transport.

`[DECIDED]` (v1):

- **Event schema**: `chat.started {botId, messageId}` (sent as soon as the user's turn is
  persisted, before streaming begins), `chat.delta {delta}`, `chat.reasoning {delta}`,
  `chat.completed {assistantMessageId, content, complete, promptTokens, completionTokens,
  totalTokens}`, `chat.failed {message}`. Hub methods: `SendAsync(botId, content)`,
  `StopAsync()`.
- **Generation is tied to the connection** (v1 has no proactive messaging, so the
  continue-server-side variant is not needed). A mid-generation disconnect cancels the run.
- **Cancellation semantics**: Stop (or a disconnect before the model finished) cancels the
  stream; the partial is persisted as an assistant message with `IsComplete = false`, and
  the interaction is logged as cancelled ("Stopped by the user."). If the model had already
  finished and only the tail was cut off, the call is counted as **completed**, not stopped.

### 4.3 Reasoning models `[DECIDED — v1]`

Reasoning/thinking content is explicitly stored (§4.4). v1: it is **shown to the user,
collapsed by default** (an expandable "thinking" block under the reply), and **not fed
back** into subsequent turns (prompts carry persona + history only). Whether reasoning
should inform later turns is deferred to Phase 3 (prompt assembly).

### 4.4 Interaction audit log `[DECIDED]`

**Every** LLM interaction is persisted, including failures. Recorded fields:

- Full request (messages as sent, parameters)
- Reasoning content, if any
- Response content
- Token counts (prompt / completion / total)
- Endpoint and model used
- `[OPEN]` additions: timestamps + latency, provider type, preset id, bot id, conversation
  id, user id, purpose (chat / embedding / memory-extraction / summarisation), HTTP status,
  error message and provider error payload, retry count, estimated cost, finish reason.

Plus a **UI to browse individual interactions** — filterable by bot, user, and outcome, with
the request/reasoning/response readable in full. This is the primary debugging tool for
"why did my friend say that", alongside the memory viewer (§5.3).

abook's `TokenUsageRecord` is the closest existing model and covers roughly the second half
of that field list (counts, endpoint, model, failed flag, failure reason, `CreatedAt`, soft
delete). The content fields and the viewer are new — see §11a delta 2.

`[OPEN]` — this table will dominate database size, far more so than abook's since it stores
message text. Needs a retention/pruning policy, and a decision on whether request bodies are
stored verbatim or deduplicated. Also: an interaction log containing full conversation text
is a privacy surface — it must be admin-scoped, and it interacts directly with the shared-bot
privacy question in §2.5.3.

### 4.5 Retries & failure handling `[OPEN]`

Retry policy, backoff, timeouts, and what the user sees when generation fails.

### 4.6 Tool use / function calling `[DECIDED — out of scope for v1]`

No tool calling in v1. The "agentic" character of AChat comes from autonomous memory
formation and retrieval, not from external actions. Reminders are the most likely first tool
if this is ever revisited.

Design consequence: the provider abstraction (§4.1) needs no tool-call round-trip, and the
message model needs no `ToolCall` / `ToolResult` entities. Keep the `Message` role enum open
enough that adding them later is additive rather than a migration of existing rows.

**Candidate tools, in likely order, when this is revisited:**

1. **`remember(...)`** — let the bot deliberately note something for later, instead of relying
   only on passive extraction (§5.3). Attractive because the bot knows in the moment what
   matters ("I should remember she asked me not to bring this up again") in a way a
   after-the-fact extraction pass over transcript text does not. It complements extraction
   rather than replacing it: passive extraction catches what the bot did not think to record.
2. **Reminders** — the one outward action a friend plausibly needs, and the only reason a bot
   would ever message first (§2.3).

Both are worth keeping in view while designing §5, since `remember(...)` writes to exactly the
same `Memory` table extraction does. The schema should not assume extraction is the only
writer.

---

## 5. Personality & Memory `[PROPOSED]`

The product's differentiator. Everything below is a concrete proposal to argue with, not a
settled decision.

**Governing split** (from §2.5): *persona is global to the bot and owned by its admin; memory
is local to the `(BotId, UserId)` relationship.*

### 5.1 Scope `[DECIDED]`

- **Per bot.** Each bot has its own memory. Nothing is shared across a user's bots — a friend
  knows only what it was told.
- **Per user.** Each user of a shared bot has a separate memory scope.

Every memory row therefore carries `(BotId, UserId)`, and every retrieval filters on both
before ranking. This doubles as the pgvector scan-narrowing filter (§6).

### 5.2 Three layers `[DECIDED — all three layers implemented (Phases 3–4)]`

A bot's knowledge of a user is assembled from three sources with different lifetimes:

| Layer | Entity | Lifetime | Role in the prompt |
|---|---|---|---|
| **Recent history** | `Message` | Verbatim, always retained | The last N turns, in full |
| **Rolling summary** | `ConversationSummary` | One row per `(bot, user)`, rewritten | Narrative continuity for everything older than the recent window |
| **Long-term memory** | `Memory` | Discrete, embedded, searchable | Facts recalled by relevance regardless of age |

Summary and memory serve different jobs and are both needed: the summary keeps the *shape* of
the relationship ("you have been talking about her thesis for weeks"), while memories recall
*specifics* that similarity search can surface from months back ("her sister is named Ann").

### 5.3 Memory formation `[DECIDED — implemented in Phase 4]`

A background hosted service — `MemoryExtractionService` — reads new messages and writes
memories. It is **not** in the request path; a turn never waits on extraction.

- **Trigger**: after a turn completes, enqueue the `(BotId, UserId)` pair. The worker debounces
  — it runs when either *M* new messages have accumulated or the conversation has been idle
  for *T* minutes, so a fast back-and-forth produces one extraction pass rather than twenty.
- **Method**: one LLM call over the new messages, using the bot's preset, prompted for
  structured JSON — a list of candidate memories, each with `content`, `kind`, and
  `importance`. Instructed to record durable facts and meaningful events, not pleasantries.
- **Embedding**: each accepted candidate is embedded with the preset's embedding model.
- **Deduplication**: before insert, similarity-search existing memories in scope. Above a
  high threshold (~0.92) the candidate is dropped or merged into the existing row rather than
  stored twice; otherwise inserted.
- **Every extraction call is logged to §4.4** with purpose `MemoryExtraction`, like any other
  LLM interaction.

`ConversationSummary` is refreshed by the same worker when the un-summarised tail exceeds a
token threshold: it takes the previous summary plus the newly-aged-out messages and rewrites
the summary in place.

`[DECIDED]` (Phase 3) — the worker is a periodic sweep (every 15s) over conversations whose
un-summarised tail (messages after the summary's watermark, `CoversUpToMessageId`) exceeds
`Chat:SummaryTailTokens`. It rewrites the single summary row in place and advances the
watermark; a failed pass leaves the watermark untouched so the same tail retries on the
next sweep. The summary prompt requires recording every personal detail the user has shared
(names, dates, plans, preferences) — the bot must be able to answer questions about any of
them later. A pass that ends `finish_reason=length` is treated as a failure: a truncated
summary must never replace a good one. Verified end-to-end: a bot recalled facts planted
far outside its recent-history window, and reacted correctly to a multi-hour silence.

`[DECIDED]` (Phase 4) — extraction runs in the same 15s sweep, on a separate watermark
(`Conversation.ExtractionUpToMessageId`) so the two passes are independent. Trigger:
`Extraction:MinNewMessages` (default 8) new messages since the watermark, or the
conversation idle for `Extraction:IdleMinutes` (default 10). One LLM call over the tail
(`Extraction:MaxTailMessages`, default 40) on the **utility model** (`LlmPreset.UtilityModel`,
falling back to the generation model), prompted for a JSON array of at most
`Extraction:MaxCandidates` (default 10) candidates, each `{content, kind, importance}` —
kind one of Fact / Preference / Event / Relationship, importance 1–5. The model is
instructed to return `[]` for small talk; an empty array is a normal outcome. Candidates
are embedded with the preset's embedding model, then each is compared to the nearest
existing memory in scope: similarity ≥ `Memory:DedupThreshold` (0.92) → drop (duplicate);
≥ `Memory:SupersedeThreshold` (0.85) → supersede (insert new, link the old row's
`SupersededByMemoryId`); otherwise insert. A `UserEdited` neighbour is never superseded —
it is dropped instead, so a human correction always wins. The watermark advances only
after a pass fully succeeds; a failed pass leaves it untouched for retry. Every pass is
logged to §4.4 with purpose `MemoryExtraction`.

Verified end-to-end: planted facts (dog, preference, upcoming job) buried under filler
turns outside the 4-message recent window were recalled by name in a later question, the
interaction log recorded the exact recalled memory ids, and a viewer edit to a memory
(adding a detail) flowed into the bot's next answer.

### 5.4 Retrieval `[DECIDED — implemented in Phase 4]`

On each turn, before calling the model:

1. Embed the incoming user message (optionally with the previous turn for context).
2. Cosine search (§6) scoped to `(BotId, UserId)` and to rows whose `EmbeddingModel` matches
   the preset's current one (§5.7).
3. Rank and take the top *K* above a score floor.
4. Always include pinned memories regardless of score.

**Ranking** is not similarity alone. Score combines three signals, weights configurable in
`appsettings.json`:

```
score = w_sim · similarity
      + w_imp · (importance / 5)
      + w_rec · exp(-age_days / halflife)
```

Recency matters because a friend should favour what is currently true; importance lets
extraction mark "she got the job" above "she likes oat milk". Defaults need tuning against
real conversations — treat the initial numbers as guesses.

`[DECIDED]` (Phase 4) — implemented as `MemoryRetriever` (raw SQL cosine search scoped to
`(BotId, UserId)`, active and non-superseded rows, `EmbeddingModel` matching the preset's
current one, then ranked in C# with the formula above). Defaults:
`Memory:TopK` = 5, `Memory:ScoreFloor` = 0.35, weights 0.6 / 0.2 / 0.2, half-life
`Memory:RecencyHalflifeDays` = 30. Pinned memories are always kept (they bypass the floor
but are still ordered first). The selected memories are rendered into the prompt block with
relative ages (§5.9), the ids are reported in the `chat.completed` hub event and stored on
the interaction log row (`RecalledMemoryIds`) so the user can see exactly which memories
shaped an answer. `LastRecalledAt` / `RecallCount` update on every use. A failed retrieval
(offline endpoint, missing embedding model) degrades gracefully: the turn proceeds
without memories and a warning is logged — a friend who occasionally forgets is better than
one who stops talking.

### 5.5 Prompt assembly `[DECIDED — implemented in Phases 3–4]`

```
┌ system ────────────────────────────────────┐
│ 1. Persona (§2.4)   — core, then evolved traits, then directives last
│ 2. Current time     — wall clock in the user's timezone (§5.9)
│ 3. Retrieved memories — top-K, rendered with relative ages
│ 4. Rolling summary  — the relationship so far, anchored to last interaction
└────────────────────────────────────────────┘
  5. Recent messages  — last N turns verbatim, oldest first, with gap markers
  6. The user's new message
```

Directives come **last** within the persona block so the most recent explicit instruction sits
closest to the generation and wins ties against evolved traits.

**Budget.** Each block gets a share of the context window, declared in config rather than
hardcoded, with the recent-history block absorbing whatever the others do not use. Starting
point: persona 10%, memories 15%, summary 10%, recent history 50%, reserved for the response
15%. Blocks are filled in priority order and truncated at their ceiling — persona is never
truncated, memories drop lowest-scored first, history drops oldest first.

`[DECIDED]` (Phase 3) — v1 has no context-window detection, so the budget is computed
against an assumed window (`Chat:AssumedContextTokens`, default 32 000) with the shares
`Chat:BudgetPersonaPercent` / `BudgetSummaryPercent` / `BudgetHistoryPercent` (default
10/10/50). Token counts are estimated as characters/4 until a real tokenizer is available.
The summary is truncated from its beginning (its most recent content is kept); history
drops oldest first; persona is never truncated.

`[DECIDED]` (Phase 4) — the memory block is implemented: retrieved memories render as
bullet lines, each with its relative age ("about 3 days ago") so the bot can frame old facts
as old. Memory lines that do not fit the `Memory` share are skipped (never truncated
mid-line); pinned memories are kept first. The full block is dropped if nothing fits.

### 5.6 Lifecycle and user control `[DECIDED — implemented in Phase 4]`

- **Superseding**: when extraction produces a memory that contradicts an existing one, the old
  row is marked `SupersededByMemoryId` rather than deleted — the history stays inspectable.
- **No automatic forgetting** in v1. Memories are not deleted by age; recency handles
  relevance through ranking instead. Simpler, and irreversible deletion of a friend's memory
  is a bad default.
- **A memory viewer is mandatory, not optional.** Per bot, the user can list, search, edit,
  pin, and delete their own memories, plus "forget everything". This is the only way to answer
  "why did my friend say that", and the only remedy when extraction records something wrong.
  Edited rows are flagged so extraction never silently overwrites a human correction.
- A user's memories are visible only to that user — including to the bot admin (§2.5.2).

`[DECIDED]` (Phase 4) — all of the above is implemented:

- Supersede, not delete: extraction links the old row via `SupersededByMemoryId`; the
  viewer renders those rows in a separate *History* section (faded, read-only, no
  supersede cycle possible from there).
- No automatic forgetting — the only deletion path is the user's explicit forget.
- Viewer (per bot): list (newest first), search (case-insensitive on content), edit
  (re-embeds the row with the preset's embedding model when the content changes, sets
  `Source = UserEdited`), pin/unpin, forget (soft delete: `IsDeleted = true`, still
  inspectable in History), and *Forget everything* for the bot.
- Manual add is **not** in v1 — the user edits what extraction produced rather than
  authoring facts from scratch. Simpler, and it keeps the extraction-vs-correction
  boundary clean (see §5.10, q2).
- Every endpoint is scoped to the caller's `(BotId, UserId)`; other users' rows for the
  same bot are never returned.

### 5.7 Embedding model stability `[DECIDED]`

Embeddings from different models are not comparable. If a bot's embedding model changes,
every stored memory becomes unsearchable — silently, with retrieval returning nothing useful
and the bot appearing to have amnesia with no error raised anywhere.

**Primary rule: the embedding model of a preset cannot be changed while any bot uses that
preset.** The field is locked in the preset editor, with the blocking bots named. To change
it, the admin detaches or deletes those bots first.

That closes the main hole cheaply, but two paths remain open and still need handling:

- **Reassigning a bot to a different preset** whose embedding model differs. Either block this
  the same way, or accept it and re-embed. Re-embedding is the friendlier option — losing a
  friend's memories to change providers is a harsh penalty. `[OPEN]`
- **Changing a preset's endpoint** while keeping the model name. Same name at a different
  provider is not guaranteed to produce comparable vectors. Low risk, worth a warning rather
  than a block.

**Therefore keep `EmbeddingModel` and `EmbeddingDimension` on every memory row anyway.** They
are cheap, they make the mismatch detectable instead of silent, and they are what any
re-embedding path needs. Retrieval filters on a match; a non-empty mismatch surfaces as a
visible "re-indexing required" state rather than an empty result set. The columns must exist
from the first migration — adding them after rows exist means re-embedding blind.

### 5.8 Schema sketch `[DECIDED — implemented in Phase 4]`

```
Memory
  Id, BotId, UserId                     -- scope, composite index
  Kind                                  -- Fact | Preference | Event | Relationship
  Content            text
  Embedding          vector             -- dimensionless, see §6
  EmbeddingModel     text
  EmbeddingDimension int
  Importance         int                -- 1..5, set by extraction
  IsPinned           bool
  Source                                -- Extraction | UserEdited | BotRemember (§4.6)
  SourceMessageId    int?               -- provenance, for the viewer
  SupersededByMemoryId int?
  CreatedAt, LastRecalledAt, RecallCount
  IsDeleted

ConversationSummary
  Id, BotId, UserId                     -- unique together
  Content            text
  CoversUpToMessageId int
  TokenCount, UpdatedAt
```

`[DECIDED]` (Phase 4) — the implemented schema matches the sketch exactly (migration
`AddMemoryExtractionWatermarkAndRecall` added `Conversation.ExtractionUpToMessageId` and
`LlmInteraction.RecalledMemoryIds` on top). `EmbeddingDimension` is stored on every row
so a model mismatch is detectable, per §5.7. `Kind` and `Source` are stored as integers
(EF's default enum mapping) — raw SQL projections must read them as int.

### 5.9 Temporal awareness `[DECIDED]`

A conversation with a friend is time-sensitive: the bot should know that three days passed
since you last spoke, that it is now late at night, that something it remembers happened
weeks ago rather than yesterday. Without this the bot reads as picking up mid-sentence after
an absence, which breaks the illusion faster than almost anything else.

Concretely, four places carry time into the prompt:

1. **Current time** in the system block — the wall-clock time and date at the moment of the
   request, in the *user's* timezone.
2. **Gap markers** in the recent-history block. When the interval between two consecutive
   messages exceeds a threshold, render a marker between them (`— 3 days later —`) rather than
   leaving them adjacent. Only gaps above the threshold are marked, so ordinary exchanges stay
   clean.
3. **Relative ages on memories.** Retrieved memories render with their age ("about three weeks
   ago"), not as timeless assertions, so the bot can say "didn't you mention that a while
   back?" instead of treating a month-old fact as fresh news.
4. **Last-interaction time on the summary**, so the summary block itself is anchored.

Consequences:

- **Every `Message` and `Memory` needs a trustworthy `CreatedAt`.** Store UTC, render in the
  user's zone.
- **A user timezone setting is required.** Without it, "it is late at night" is wrong for
  anyone not co-located with the server. `[DECIDED]` — **both**: captured from the browser
  (`Intl.DateTimeFormat().resolvedOptions().timeZone`) at login and stored on the user,
  with a manual override in the app (sidebar → ⏰, IANA name validated server-side).
  Prompts render "now" in the user's zone.
- Rendering relative time is a **presentation concern of prompt assembly** (§5.5), not stored
  data — the same memory renders as "yesterday" or "last year" depending on when it is
  recalled.

`[DECIDED]` (v1) — **six hours** by default, configurable (`Chat:GapThresholdMinutes`), so
ordinary same-day exchanges stay clean. Absences are told **both ways**: a human-scale
marker ("— 3 days later —") is prefixed to the message after the silence, and the new
message itself carries it when the user returns to a quiet timeline — the bot reads the
absence directly rather than guessing it. Markers ride on the following message rather
than being standalone system messages: several production backends (vLLM/SGLang chat
templates) reject system messages mid-conversation.

### 5.10 Open questions

1. `[DECIDED]` (Phase 4) initial values — K = 5, score floor 0.35, M = 8 new messages,
   T = 10 min idle, weights 0.6 / 0.2 / 0.2, half-life 30 days. All config keys, all
   revisitable without code changes.
2. `[DECIDED]` (Phase 4) — edit only; no manual add in v1 (§5.6).
3. `[DECIDED]` (v1) — persona is static text; no memory placeholders. Memories are
   assembled at prompt time (§5.5), so there is nothing to reference from the persona.
4. `[OPEN]` — no onboarding step in v1. The first message goes to a fresh, memory-less
   friend; extraction starts recording from turn one.
5. `[OPEN]` — the summary is not user-visible or editable in v1. It is an internal
   continuity device; memories (§5.6) are the user-facing record. Revisit if users ask.

---

## 6. Persistence `[DECIDED]`

- **Entity Framework Core** over **PostgreSQL** with the **pgvector** extension.
- Embeddings stored in a **variable-length** vector column, so presets with different
  embedding models/dimensions can coexist.
- **Migrations are applied automatically on application start.** The database is never
  upgraded by hand.
- Migrations are authored during development with the `dotnet ef` tool. They are never
  hand-written or hand-applied.
- **Manual database access during development is read-only.** Connecting to the database to
  check something is fine; issuing anything other than `SELECT` by hand is not. All schema
  changes go through migrations and all data changes go through the application. This is a
  rule about human/agent interaction with the database, not a constraint on the test suite —
  automated tests may write freely to their own disposable database.

**Precedent from abook** — the same approach is already in production there:

- `HasColumnType("vector")` with no dimension, `options.UseNpgsql(cs, o => o.UseVector())`,
  and `Pgvector.EntityFrameworkCore` referenced from both `Infrastructure` and `Api`.
- Similarity search is **raw SQL**, not LINQ: `ORDER BY "Embedding" <=> {query}::vector
  LIMIT {topK}`, with `1 - (embedding <=> query)` projected as the score, and a `WHERE`
  clause scoping to the owning entity before ranking.
- The vector service takes `IDbContextFactory<AppDbContext>` and opens a short-lived context
  per call, so concurrent retrievals never share a context.

AChat's equivalent scopes on `(BotId, UserId)` instead of `BookId` — see §2.5.

`[OPEN]`:

- Variable-length vectors cannot use a standard pgvector index (ivfflat/hnsw require fixed
  dimensions), and abook indeed has **no vector index** — every search is a sequential scan
  over the scoped rows. That is fine at abook's volumes. AChat's memory table grows per
  message rather than per chapter, so the same choice may not hold. Options: partial indexes
  per dimension actually in use, partitioning by dimension, or accepting the scan because the
  `(BotId, UserId)` filter keeps the candidate set small. **Resolve before the memory feature
  is built** — measure at a realistic row count rather than guessing.
- Concurrency: applying migrations on start with multiple app replicas needs an advisory
  lock. Is multi-replica a requirement at all?
- Soft delete vs. hard delete, audit columns (created/updated/by).
- Backup/restore guidance for the compose stack.

---

## 7. Authentication, Authorization & Secrets

### 7.1 Authentication `[DECIDED]`

JWT bearer tokens.

`[DECIDED]` (v1): **168-hour** lifetime (`Jwt:ExpiryHours`), no refresh tokens (re-login
when expired), signing key from config / `ACHAT_Jwt__SigningKey` (rotation = change the
value, all active sessions re-authenticate). Token stored client-side in **localStorage**.
SignalR connections authenticate with the same JWT: browsers pass it as the `access_token`
query parameter (WS/SSE can't set headers) and the JWT handler reads it in
`OnMessageReceived`.

### 7.2 Bootstrap admin `[DECIDED]`

Three configuration settings govern first-run and recovery:

| Setting | Effect |
|---|---|
| First user login | Login name of the bootstrap admin |
| First user password | Password to set |
| Force set password | Boolean |

On startup:

- If no user exists with that login → create it and mark it **admin**.
- If the user exists **and** force-set-password is `true` → overwrite that user's password
  and mark them **admin**.
- Otherwise → no action.

`[DECIDED]` (v1): **Argon2id** hashing (PHC string, `Konscious.Security.Cryptography.Argon2`),
no password policy, the bootstrap password **may remain in config** (recovery use case),
and **no self-service registration** — users are created by an admin (Admin → Users).

### 7.3 API key storage `[DECIDED]`

Provider API keys are stored **securely** in the database, never returned to the browser in
plaintext, and never logged.

**This is a genuine delta from abook**, which stores provider API keys in plaintext and
passes them straight to `ApiKeyCredential`. AChat's "stored securely" requirement is new
work with no existing implementation to lift.

**Mechanism** `[DECIDED]` — **ASP.NET Core Data Protection**, the standard .NET answer, with
the key ring persisted to PostgreSQL via
`Microsoft.AspNetCore.DataProtection.EntityFrameworkCore`:

```csharp
services.AddDataProtection()
        .PersistKeysToDbContext<AppDbContext>()
        .SetApplicationName("achat");
```

Persisting to the database rather than the filesystem is what makes this work in a container:
no extra volume to mount, no key ring lost on `docker compose down`, and the keys travel with
a database backup. `SetApplicationName` is required — without it the purpose string derives
from the entry assembly and a rename silently invalidates every stored key.

Rules that follow:

- Keys are encrypted on write with an `IDataProtector` scoped to a fixed purpose string.
- The API never returns a key to the browser. Preset responses carry a boolean
  `hasApiKey` and, at most, a masked tail (`sk-…4f2a`).
- An empty submitted key on update means "leave unchanged", not "clear it" — clearing needs
  an explicit action.
- Keys are never logged, and are redacted from the §4.4 interaction log's stored request.

`[OPEN]` — key rotation policy, and behaviour when the key ring is lost (every stored API key
becomes undecryptable; the app must fail gracefully and prompt for re-entry rather than
crash on startup).

### 7.4 Authorization `[OPEN]`

What admins can do beyond existing (see all users? manage users? see others' bots?), and
whether there are roles beyond admin/user.

---

## 8. Backend Architecture `[DECIDED]`

- ASP.NET Core, .NET 10.
- **Controllers** (not minimal APIs).
- Split into multiple projects if the size warrants it.
- **Background workers are hosted services inside the ASP.NET project**, not a separate
  process or container.

**Project split** — mirror abook's, which is proven for this exact stack:

| Project | Contents |
|---|---|
| `AChat.Core` | Domain models and interfaces (`IBotRepository`, `ILlmProviderFactory`, `IVectorStoreService`, …) |
| `AChat.Infrastructure` | EF `AppDbContext`, repositories, LLM provider factory + strategies, pgvector service, migrations |
| `AChat.Api` | Controllers, SignalR hub, `Program.cs`, hosted services |
| `AChat.Tests` | Tests |
| `src/achat-ui` | React 19 + TypeScript + Vite SPA |

abook additionally has `ABook.Agents` for its orchestration layer. AChat's equivalent — the
memory extraction / summarisation / proactive-messaging logic — may warrant `AChat.Bots` or
may be small enough to live in `Infrastructure`. `[OPEN]`

`[OPEN]`:
- Which background workers exist. Known candidates: memory extraction, conversation
  summarisation, proactive messaging (if adopted), audit-log pruning.
- How background work is scheduled and made crash-safe (in-process timers vs. a
  database-backed job queue). If proactive messaging is in scope this needs to be durable.
- Error model (`ProblemDetails`), validation, logging (structured; Serilog?), health
  endpoint, OpenAPI generation, graceful shutdown.

---

## 9. Frontend `[PARTIALLY DECIDED]`

- React + TypeScript, built with **Vite**.
- **Vite is never run directly, not even in development — the app always runs in Docker.**
  Implication: the development loop is a container rebuild (or a bind-mounted watch inside
  the container). This needs an explicit, documented workflow so iteration isn't painful.
- Visual design follows the author's other project **abook**, with a **different colour
  scheme**.

### 9.1 abook design language `[CAPTURED]`

Observed from the live deployment (`abook.chekanova.pp.ua`, v0.1.22). AChat should adopt
this system wholesale and change only the palette.

**Approach**: hand-written plain CSS, ~33 KB in one bundle. No Tailwind, no component
library. Semantic class names (`app-layout`, `app-sidebar`, `sidebar-btn`, `btn`,
`version-pill`, `beta-badge`). CSS custom properties on `:root` carry the whole theme, so
re-skinning is a matter of swapping ~12 variables.

**Tokens (abook's own values — AChat replaces these):**

| Variable | abook value | Role |
|---|---|---|
| `--bg` | `#0f172a` | App background |
| `--surface` | `#1e293b` | Sidebar, cards, panels |
| `--border` | `#334155` | Hairlines |
| `--text` | `#e2e8f0` | Primary text |
| `--text-muted` | `#94a3b8` | Secondary text |
| `--input-bg` | `#0f172a` | Field background |
| `--accent` | `#6366f1` | Primary action, active nav |
| `--accent-hover` | `#818cf8` | Hover state |
| `--accent-faint` | `#6366f11f` | Tinted fills, active-row background |
| `--danger` | `#ef4444` | Destructive |
| `--success` | `#22c55e` | Positive |
| `--warning` | `#f59e0b` | Caution |

**Typography**: `Inter, system-ui, sans-serif`, 16px root. UI text runs small and dense —
the dominant sizes are `.85rem`, `.82rem`, `.78rem`, `.875rem`; body copy at `1rem` is rare.

**Spacing**: `rem`-based, informal scale clustered on `.4 / .5 / .75 / 1rem`.

**Shape**: `6px` radius on buttons and inputs, `4px` on small elements, `999px`/`10px` pills
for badges, `50%` for avatars.

**Depth**: flat. No box-shadows anywhere — hierarchy comes from `--bg` vs `--surface` plus
`--border` hairlines.

**Motion**: uniform `.15s` transitions on background/colour/border, `.2s` on width
(sidebar collapse).

**Layout**: fixed left sidebar, 220px expanded, collapsible to an icon rail, with a pinned
top block (brand + version pill + status badge), a scrolling nav area, and a pinned bottom
block. Main content fills the remainder. No top header bar.

**Themes**: a `prefers-color-scheme: light` block exists; only 3 media queries total, so
responsive coverage is minimal.

### 9.1b abook screen patterns `[CAPTURED]`

Observed logged in. Source is available at `github.com/jncchds/abook` — the definitive
reference for the shell, and worth reading rather than re-deriving.

**Shell**, top to bottom in the 220px sidebar:

1. Hamburger collapse toggle, wordmark, version pill (links to the repo), stage badge (`ALPHA`).
2. Primary nav — icon + label rows: Books, Users, Presets, Settings, Library. Active row is
   `--accent-faint` background with `--accent` text.
3. An uppercase muted section label (`BOOKS`), then a scrolling list of entities: icon +
   title on line one, truncated description on line two.
4. Pinned bottom: `Sign Out (username)` and `Theme: system` — a cycling tri-state toggle.

**Page pattern**: large page title with the primary action as an accent button set inline
beside it (`+ New Preset`), an optional muted explanatory paragraph, then content.

**List pattern**: one bordered card containing hairline-separated rows. Each row has a bold
name, a muted metadata line using ` · ` separators, an optional second muted line, and
right-aligned `Edit` / `Delete` buttons — Delete outlined in `--danger`.

**Card pattern** (Books): title, right-aligned muted count, a row of pills (accent-tinted for
category, neutral for language, neutral for status), description, then an accent
`Open →` button with a small red `×` beneath it.

**Direct mappings to AChat**: Presets is nearly the same feature already (abook shows
Ollama / OpenAI / GoogleAIStudio, model, endpoint, and a separate `embed:` model per preset).
Books → Bots. Users → Admin. `Theme: system` confirms the tri-state light/dark/system toggle
already exists. abook's Library may already model shared/other-user content — worth checking
against AChat's invite-code sharing before designing it fresh.

### 9.1a Navigation `[DECIDED]`

- **Both light and dark themes from the beginning.** Every colour goes through a variable;
  no hardcoded hex in component CSS. Theme follows the OS by default with a manual override
  persisted per user.
- **Each page has its own navigation.** The left panel is contextual, not a single global
  nav — its contents change with the current section.
- **Pinned top block**: Presets, Bots, Admin.
- **Below the pinned block**: the last *N* bots ordered by most recent interaction with the
  current user, as the primary way back into a conversation.
- **On a bot screen**: the left panel additionally exposes a *bot admin* tab (visible to the
  bot's owner only — see §2.5).

`[OPEN]`:

- The value of *N*, and whether the list is "recent" only or has a "see all bots" affordance.
- What the pinned bottom block holds (abook has one) — current user, theme toggle, logout?
- Whether the shared shell keeps abook's collapse-to-icon-rail behaviour given the list is
  now avatars + names + previews rather than plain nav items.
- Does the recent-bots list update live over SignalR, or only on navigation?

### 9.1c Theming mechanism `[DECIDED — inherited from abook]`

abook's scheme is copied wholesale, and it is exactly what "light and dark from the
beginning" needs:

- Accent and semantic colours live on bare `:root` and are **mode-invariant** — one value
  serves both themes.
- Only six neutrals flip: `--bg`, `--surface`, `--border`, `--text`, `--text-muted`,
  `--input-bg`. They are declared three times — under `[data-theme="dark"], :root:not([data-theme="light"])`,
  under `[data-theme="light"]`, and under `@media (prefers-color-scheme: light)` — so an
  explicit choice wins in both directions and the unset default follows the OS.
- `useTheme` is a tri-state hook (`light` / `dark` / `system`) writing
  `document.documentElement.dataset.theme`, persisted in `localStorage` (key removed for
  `system`), with a `matchMedia` listener so OS changes take effect live.

Consequence: defining AChat's scheme means choosing **six dark neutrals, six light neutrals,
and one accent triple**. Everything else carries over unchanged.

### 9.2 AChat colour scheme `[DECIDED — Grove]`

**Grove** (zinc neutrals + teal accent) is the chosen scheme, picked for having no collision
with the semantic colours. Same variable names and roles as abook, so the stylesheet is a
drop-in. Alternatives kept below for the record.

| | Grove (zinc + teal) | Ember (stone + orange) | Bloom (plum + rose) |
|---|---|---|---|
| `--accent` | `#14b8a6` | `#f97316` | `#f43f5e` |
| `--accent-hover` | `#2dd4bf` | `#fb923c` | `#fb7185` |
| dark `--bg` | `#18181b` | `#1c1917` | `#1a1620` |
| dark `--surface` | `#27272a` | `#292524` | `#262030` |
| dark `--border` | `#3f3f46` | `#44403c` | `#3d3450` |
| dark `--text` | `#e4e4e7` | `#e7e5e4` | `#e9e4f0` |
| dark `--text-muted` | `#a1a1aa` | `#a8a29e` | `#a89db8` |
| light `--bg` | `#fafafa` | `#fafaf9` | `#faf8fc` |
| light `--surface` | `#ffffff` | `#ffffff` | `#ffffff` |
| light `--border` | `#e4e4e7` | `#e7e5e4` | `#e9e2f0` |
| light `--text` | `#18181b` | `#1c1917` | `#1a1620` |
| light `--text-muted` | `#52525b` | `#57534e` | `#6b5f7d` |

Rejected alternatives: **Ember** was warmest and the best thematic fit, but orange sits close
to `--warning`. **Bloom** was most distinctive, but rose neighbours `--danger`, making
destructive and primary buttons read alike.

**Accent contrast — resolved.** abook gets away with a mode-invariant accent because indigo
`#6366f1` carries white text on both themes. Teal `#14b8a6` is too light for white text in
light mode, so `--accent` is the one variable that breaks the mode-invariant rule:

```
:root                          { --accent: #0d9488; --accent-hover: #14b8a6;
                                 --accent-faint: rgba(20,184,166,0.12); }
[data-theme="dark"],
:root:not([data-theme="light"]) { --accent: #14b8a6; --accent-hover: #2dd4bf; }
```

Text on accent fills is white in both modes. `--danger` / `--success` / `--warning` stay
mode-invariant at abook's values (`#ef4444` / `#22c55e` / `#f59e0b`).

### 9.3 Remaining frontend questions `[OPEN]`

- Dark/light theme support — carry over abook's light block or ship dark-only?
- State management, routing, API client generation (OpenAPI → TS types recommended),
  markdown rendering, streaming render strategy.
- Screen inventory: login, chat, bot list/editor, preset list/editor, admin, settings.
- Sidebar contents for AChat — the natural mapping is a friends list (avatar + name +
  last-message preview) rather than abook's flat nav.

---

## 10. Serving Model & Container

`[DECIDED]`:

- Single container serving **both backend and frontend on one port**.
- ASP.NET Core serves the built frontend as static files, with SPA fallback for client
  routes (excluding `/api/*` and the SignalR hub path).
- Shipped as part of a docker compose stack alongside PostgreSQL/pgvector.

**Container shape — copy abook's, which already satisfies every constraint:**

Three-stage Dockerfile: `node:20-alpine` builds the SPA to `./dist` (with `npm ci` on
`package*.json` first for layer caching); `mcr.microsoft.com/dotnet/sdk:10.0` restores from
copied `.csproj` files then publishes; `mcr.microsoft.com/dotnet/aspnet:10.0` receives the
publish output and copies the SPA `dist` into `./wwwroot`. `EXPOSE 8080`,
`ASPNETCORE_URLS=http://+:8080`.

Compose: app published as `5000:8080` alongside `pgvector/pgvector:pg16` with a
`pg_isready` healthcheck, `depends_on: condition: service_healthy`, a named volume for
Postgres data, `restart: unless-stopped`, and `extra_hosts: host.docker.internal:host-gateway`
so a preset can reach an Ollama running on the host.

**Development workflow** — abook's `CLAUDE.md` already codifies the Docker-only rule:
`docker-compose up postgres -d` for the database alone, `docker-compose up -d` for the full
stack. Note that abook still keeps a `vite build` output path pointing at `Api/wwwroot` for
local builds, overridden to `./dist` in the Dockerfile.

`[OPEN]`:

- Non-root user and image size target (abook's Dockerfile runs as root and sets neither).
- Health check on the app container itself (only Postgres has one in abook).
- Volumes, graceful shutdown, log destination.
- Whether AChat keeps abook's `VERSION`-file-injected-into-Vite versioning scheme and the
  version pill in the sidebar. Recommended — it is cheap and the pill is already designed.

---

## 11. Configuration `[DECIDED]`

- **All** configuration lives in `appsettings.json`.
- Every setting is overridable by environment variable using the **`ACHAT_`** prefix.

`[OPEN]`:

- The complete settings table (key, type, default, description) — this doubles as user
  documentation.
- Nested-key convention for env vars (.NET default is `ACHAT_Section__Key`); confirm this
  is acceptable or whether a friendlier mapping is wanted.
- Startup validation: fail fast with a clear message on missing/invalid required settings.

---

## 11a. What AChat Inherits From abook

Source at `C:\code\abook` (`github.com/jncchds/abook`). Recommended posture: **copy the CSS
and shell components, mirror the architecture, do not fork the repo** — the domain is
different enough that carrying books along costs more than it saves.

**Lift near-verbatim:**

| Asset | Path in abook | Note |
|---|---|---|
| Theme variables + all component CSS | `src/abook-ui/src/index.css` (~43 KB, single file) | Swap the palette (§9.2); everything else stands |
| `useTheme` tri-state hook | `src/abook-ui/src/hooks/useTheme.ts` | Unchanged |
| `Sidebar` + `SidebarBtn` / `SidebarDivider` / `SidebarSection` | `src/abook-ui/src/components/Sidebar.tsx` | Generic already — takes `children` and `bottomChildren` |
| Per-section layout pattern | `src/abook-ui/src/layouts/` | `BookListLayout` / `BookLayout` / `LibraryLayout` is precisely "each page has its own navigation" |
| pgvector service | `src/ABook.Infrastructure/VectorStore/PgvectorVectorStoreService.cs` | Re-scope from `BookId` to `(BotId, UserId)` |
| LLM provider factory + strategies | `src/ABook.Infrastructure/Llm/` | `Ollama`, `OpenAI`, `OpenAICompatible`, `GoogleAIStudio` strategies already exist |
| Presets page and editor | `src/abook-ui/src/pages/Presets.tsx`, `src/config/providers.ts` | Nearly AChat's §3 spec already |
| Dockerfile + docker-compose | repo root | See §10 |
| Project layout | `src/ABook.{Core,Infrastructure,Agents,Api}` | See §8 |

**Deltas — where AChat diverges and there is no code to lift:**

1. **JWT auth.** abook uses cookie sessions (`authLogout`, `useAuth`); AChat specifies JWT
   (§7.1). New work end to end, including SignalR authentication.
2. **Full LLM interaction logging, and a UI for it.** abook's `TokenUsageRecord` captures
   `PromptTokens`, `CompletionTokens`, `Endpoint`, `ModelName`, `Failed`, `FailureReason`,
   `CreatedAt` and a soft-delete flag — but **never the request, reasoning, or response
   text**, and `TokenStatsPage` shows aggregates only, not individual interactions. AChat
   §4.4 requires all three content fields plus a browsable interaction viewer. Treat
   `TokenUsageRecord` as the skeleton to extend, not a finished feature.
3. **API key encryption.** abook stores keys in plaintext (§7.3).
4. **Bot sharing / invite codes.** No analogue — abook's Library is public book reading, not
   granted per-user access. `[OPEN]` — verify this by reading `LibraryLayout` and
   `PublicBookReader` before designing §2.5 from scratch.
5. **Per-user memory on a shared entity.** abook's embeddings are scoped by book only.
6. **The chat surface itself.** abook's `ChatPage` is a small book-scoped chat, not a
   streaming conversation UI with bubbles, history, and reconnect.

**Conventions worth carrying over** (from abook's `CLAUDE.md`): `VERSION` at repo root as
single source of truth injected into Vite as `__APP_VERSION__`; a `RELEASE_NOTES.md` entry
per change; `AGENTS.md` as the authoritative design doc kept in sync with schema changes;
`dotnet build` + `npx tsc --noEmit` after every change; never hand-write migrations; never
run outside Docker.

`[OPEN]` — abook keeps both `CLAUDE.md` (workflow) and `AGENTS.md` (90 KB authoritative
design doc). Decide whether this document becomes AChat's `AGENTS.md` on the same footing.

## 12. Cross-Cutting Concerns `[OPEN]`

- **Security**: prompt injection via bot personas or remembered content, SSRF via
  user-supplied provider endpoints (a user can point a preset at any URL — this is
  intentional for Ollama but is a real risk), CSP, rate limiting, secrets never logged.
- **Observability**: structured logging, metrics, tracing across the LLM call path.
- **Performance targets**: concurrent conversations, time-to-first-token, memory retrieval
  latency.
- **Testing**: unit, integration (WebApplicationFactory + Testcontainers for Postgres), E2E.
  Note the constraint that tests may only issue `SELECT`s against the database — this needs
  clarifying (§13).
- **Accessibility**, **i18n**.

---

## 12a. Delivery Plan

Phases are ordered so that each one ends with something runnable, and so that the tools for
debugging the hard parts exist before the hard parts do. Narrow first, widen later: one
provider, one persona layer, one memory layer at a time.

### Phase 0 — Skeleton

Solution structure (§8), Dockerfile and compose stack (§10), EF Core + pgvector with
migrations applied on start (§6), configuration with `ACHAT_` overrides (§11), JWT auth and
the bootstrap-admin flow (§7.1–7.2), the React shell — Grove palette, tri-state theme,
per-section layouts (§9). No LLM involvement whatsoever.

*Done when*: `docker compose up` serves a themed app on one port, you can log in as the
bootstrap admin, and the database is created by migration.

### Phase 1 — One bot, one model, one conversation

**Only the `OpenAICompatible` provider type.** Preset CRUD with encrypted keys (§7.3), load
models, test connection. Bot creation with the **core persona layer only**. One continuous
conversation per `(bot, user)` (§2.3), streaming over SignalR (§4.2). Recent-history window
with a fixed turn count — no summary, no memory, no embeddings.

Streaming belongs here rather than later: it shapes the hub contract and the entire chat
component, and retrofitting it means writing the chat UI twice.

*Done when*: you can create a bot and hold a streaming conversation with it that remembers
nothing beyond the visible window.

### Phase 2 — The interaction log

Full `LlmInteraction` capture and the browsable viewer (§4.4).

Deliberately early. Everything after this is prompt engineering against a black box, and this
is the window into it — what was actually sent, what came back, how many tokens, what failed.
Building it now costs a phase; building it later costs every phase in between.

*Done when*: every call, successful or failed, is inspectable in full from the UI.

### Phase 3 — Continuity without embeddings

Utility model on presets (§3). Rolling summary maintained by a background worker (§5.2–5.3).
Token-budgeted prompt assembly (§5.5). Temporal awareness in full — user timezone, current
time, gap markers, summary anchoring (§5.9).

No vectors yet. A summarising, time-aware bot is already recognisably the product, and this
phase proves the prompt-assembly machinery before adding retrieval on top.

*Done when*: a bot holds continuity across a conversation far longer than its context window,
and reacts appropriately to a three-day gap.

### Phase 4 — Long-term memory

Embedding model on presets, pgvector storage, the extraction worker, ranked retrieval, and
the memory viewer/editor (§5.3–5.6, §5.8). Embedding-model locking (§5.7).

The viewer ships **in this phase, not after it** — extraction will record wrong things, and
without the viewer there is no way to see that or fix it.

*Done when*: a bot recalls a specific fact from far outside its context window, and the user
can see and correct exactly which memories were recalled.

### Phase 5 — Evolving personality

Directives and the evolved-traits layer with versioning and revert (§2.4). The persona block
becomes three layers instead of one.

*Done when*: a bot's evolved traits visibly shift after sustained conversation with its
creator, the admin can read the version history, and revert works.

### Phase 6 — Sharing

`BotAccess`, per-bot invite GUID, access requests and the admin approval queue (§2.5).
Per-user memory scoping is already in place from Phase 4 — this phase adds the second user
that makes it matter, plus the authorization rules and the privacy decisions around the
Phase 2 log viewer.

*Done when*: a second user can request access, be approved, and hold a conversation with a
separate memory scope that the first user cannot see.

### Phase 7 — The rest of the providers

Add `Ollama`, `OpenAI`, and `GoogleAIStudio` strategies (§3, §4.1). Straightforward by this
point: the abstraction has been exercised by a real product for six phases, so its shape is
known to be right rather than guessed.

*Done when*: a bot runs unchanged against a local Ollama and a hosted provider.

### Phase 8 — Operational maturity

Admin user management, usage visibility and quotas for bot admins, interaction-log retention
and pruning (§4.4), retry and failure-handling policy (§4.5), health checks, non-root
container, accessibility pass, backup guidance.

### Later, explicitly not v1

Tool calling — `remember(...)` first, then reminders (§4.6). Proactive messaging (§2.3).
Group chats. Character-card import. Anything in §1.3.

`[OPEN]` — is this ordering right for how you want to work? The main alternative is pulling
sharing (Phase 6) much earlier, if having a second user is important to how you test.

## 13. Open Questions — Consolidated

**Resolved so far:** tool calling out of v1 (§4.6) · proactive messaging out of v1 (§2.3) ·
memory per `(BotId, UserId)` (§5.1) · bot roles and creator-owned personality (§2.5) ·
Data Protection for API keys (§7.3) · manual DB access read-only, tests may write (§6) ·
Grove palette (§9.2) · four providers including GoogleAIStudio (§3) · project layout and
container shape inherited from abook (§8, §10, §11a) · the §2.4 personality model — three
layers, creator-only evolution, versioned, per-bot switch — implemented in Phase 5 ·
generation parameters on the preset in v1 (§2.4) · user timezone captured at login **and**
set explicitly (§5.9) · §5 memory initial values as config (§5.10) · `AGENTS.md` written;
this document stays the spec (§11a).

**Still open, roughly by weight:**

1. Review the §12a phase ordering (Phases 0–5 are done; 6–8 remain).
2. Remaining sharing details: cost visibility, revocation semantics, code regeneration, and
   who may read the interaction log for a shared bot. (§2.5)
3. Message editing, deletion, and regeneration; group chats. (§2.3)
4. Onboarding — what a bot knows at the first message of a new relationship. (§5.10.4)
5. Bot reassignment to a preset with a different embedding model: block, or re-embed? (§5.7)
6. Whether variable-length embeddings need an indexing strategy at AChat's row counts. (§6)
7. Tuning the ranking/extraction/evolution constants against real conversations — all
   currently first guesses, all in config.
