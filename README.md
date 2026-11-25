# AChat — Agentic Chat

A self-hosted platform that hosts **virtual friends**: chat bots with a persistent
*personality* and a persistent *memory* of their relationship with you. The emphasis is
on continuity, not task completion — a bot remembers who you are, what you talked about
weeks ago, and behaves consistently over long spans of time.

Authoritative design: [`docs/design.md`](docs/design.md).

## Quick start

```bash
docker compose up -d --build
```

- App: http://localhost:5001 (backend + SPA on one port; host port 5001 because 5000
  is used by the abook stack on this machine)
- First-run account: `admin` / `admin` (see `BootstrapAdmin` settings below)
- PostgreSQL 16 + pgvector runs as a sibling container; schema is created by
  migrations automatically on app start.

Point a **preset** at a model endpoint — local Ollama, OpenAI, Google AI Studio, or any
OpenAI-compatible gateway (llama.cpp, vLLM, LM Studio, OpenRouter, …) — create a **bot**
(name + who they are), and talk.

## How a turn works

Every reply is one LLM call that returns a small JSON **turn envelope**
(`docs/design.md` §4.7): `{"reply", "mood", "memories", "traits"}`. The reply is
streamed to you as it is written; the rest is read when the call completes. The bot's
**mood** is conversation state (damped — it can change only after several turns and
some minutes), its **memories** are things it chose to remember (embedded, deduplicated,
recalled by relevance in later turns), and, in its creator's conversation only, its
**traits** are short items about the friendship that join the persona's evolved layer
(guard-railed, versioned and revertable). Core persona and standing directives are never
reachable from the envelope. A reply that isn't valid JSON is still delivered as plain
text and has no side effects. Two slow background passes remain on the durable work
queue: rolling summaries, and consolidation of the trait list when it grows long.

## Configuration

All settings live in `appsettings.json` and are overridable by environment variable
with the `ACHAT_` prefix and .NET nested-key convention (`ACHAT_Section__Key`):

| Key | Default | Description |
|---|---|---|
| `ConnectionStrings__DefaultConnection` | `Host=localhost;Port=5432;Database=achat;Username=achat;Password=achat` | PostgreSQL connection string |
| `BootstrapAdmin__LoginName` | `admin` | First-run / recovery admin login name (§7.2) |
| `BootstrapAdmin__Password` | `admin` | Password to set for that user |
| `BootstrapAdmin__ForceSetPassword` | `true` | Re-apply the password on every start (recovery mode) |
| `Jwt__SigningKey` | dev value — **change it** | HMAC-SHA256 signing key for tokens (§7.1) |
| `Jwt__ExpiryHours` | `168` | Token lifetime |
| `Chat__HistoryTurns` | `20` | Recent-history window (messages) in the prompt |
| `Chat__GapThresholdMinutes` | `360` | Silence above this gets a gap marker in the prompt (§5.9) |
| `Chat__AssumedContextTokens` | `32000` | Assumed context window for prompt budgeting (§5.5) |
| `Chat__BudgetPersonaPercent` / `BudgetMemoryPercent` / `BudgetSummaryPercent` / `BudgetHistoryPercent` | `10 / 15 / 10 / 50` | Prompt budget shares; recent history absorbs the remainder |
| `Chat__SummaryTailTokens` | `800` | Un-summarised tail that triggers a summary rewrite (§5.3) |
| `Chat__SummaryMaxTokens` | `1024` | Output cap for the summary call (covers reasoning-model thinking) |
| `Chat__SummaryTemperature` | `0.3` | Sampling temperature for the summary call |
| `Memory__TopK` | `5` | Max memories retrieved per turn (§5.4) |
| `Memory__ScoreFloor` | `0.35` | Rank score below which a memory is not retrieved |
| `Memory__WeightSimilarity` / `WeightImportance` / `WeightRecency` | `0.6 / 0.2 / 0.2` | Ranking weights (§5.4) |
| `Memory__RecencyHalflifeDays` | `30` | Recency decay half-life |
| `Memory__DedupThreshold` / `SupersedeThreshold` | `0.92 / 0.85` | Candidate-vs-nearest similarity: ≥0.92 drop as duplicate, ≥0.85 supersede (§5.3) |
| `Turn__MaxMemories` | `3` | Most memories the bot may record in one turn's envelope (§4.7) |
| `Turn__MaxTraitChangesPerDay` | `3` | Trait changes (adds + retires) the bot may make per rolling 24 h; creator's turns only (§4.7) |
| `Turn__MaxTraits` | `20` | Active trait items; over it the bot's adds are refused until something is retired |
| `Turn__MaxTraitChars` | `200` | Longest single trait item (longer ones are clipped; also the limit for human adds/edits) |
| `Turn__MaxTraitsTotalChars` | `2000` | Total characters over all active items; same refusal as above |
| `Turn__ConsolidateAbove` | `12` | Active trait items above which the slow consolidation pass is armed (§2.4) |
| `Evolution__CooldownHours` | `12` | Cooldown after a human edit/revert during which the bot's trait changes are dropped, and between consolidation passes (newest version of any source) |
| `Evolution__MaxTokens` | `1024` | Output cap for the consolidation call |
| `Turn__MoodMinTurns` / `Turn__MoodMinMinutes` | `6 / 30` | A bot's mood may change only after this many user turns **and** minutes since its last change (§4.7) |
| `Retention__InteractionDays` | `90` | Prune audit-log interactions older than this many days; `0` = keep forever (§4.4) |
| `Retention__IntervalHours` | `6` | How often the retention worker runs |
| `Work__TickSeconds` | `5` | How often the work queue arms due work and claims items (§12b) |
| `Work__MaxAttempts` | `5` | Failed attempts before a work item dead-letters |
| `Work__BaseDelaySeconds` | `60` | First retry delay; doubles per attempt (±10% jitter) |
| `Work__MaxDelaySeconds` | `3600` | Retry-delay cap |

## Development workflow

The app **always runs in Docker** — `dotnet run` / `vite dev` are not used, not even
in development (the container *is* the environment).

```bash
# Full stack (rebuilds the image)
docker compose up -d --build

# Just the database, for tooling (migrations, inspection)
docker compose up -d postgres

# After every change — must stay green:
dotnet build AChat.slnx
dotnet test AChat.slnx
npx tsc -b --noEmit            # in src/achat-ui

# Migrations are authored with the dotnet-ef tool, never by hand, and are applied
# automatically by the app on start. Example:
ACHAT_ConnectionStrings__DefaultConnection="Host=localhost;Port=5433;Database=achat;Username=achat;Password=achat" \
dotnet ef migrations add MyChange --project src/AChat.Infrastructure --startup-project src/AChat.Api
```

Database rules:

- **Manual database access during development is read-only.** Checking something with
  `SELECT` is fine; issuing anything else by hand is not. All schema changes go through
  migrations; all data changes go through the application.
- Automated tests may write freely to their own disposable database.

## E2E scripts

Verification scripts live in `scripts/e2e/` (run against a live instance, default
`http://localhost:5001`; override with `ACHAT_BASE`):

```bash
# SignalR test client (used by the phase scripts)
node scripts/e2e/sigtest.mjs <token> <botId> "message"

# Full browser smoke test (Playwright)
node scripts/e2e/smoke.mjs

# Phase-specific E2E — start a throwaway instance with fast worker triggers first
# (a JWT signing key >256 bits and the bootstrap admin are required):
docker rm -f achat-e2e
docker run -d --name achat-e2e --network achat_default -p 5002:8080 \
  -e ACHAT_ConnectionStrings__DefaultConnection="Host=postgres;Port=5432;Database=achat;Username=achat;Password=achat" \
  -e ACHAT_Jwt__SigningKey="e2e-acht-signing-key-0123456789abcdef0123456789abcdef" \
  -e ACHAT_BootstrapAdmin__LoginName=admin -e ACHAT_BootstrapAdmin__Password=admin \
  achat-achat
# The scripts below use the mock LLM (`node scripts/e2e/mockllm.mjs`, port 18999) unless noted
ACHAT_BASE=http://localhost:5002 node scripts/e2e/phase5-mock.mjs  # turn envelope: prompt order, bot memories + trait in one turn, garbled reply is harmless
ACHAT_BASE=http://localhost:5002 node scripts/e2e/traits.mjs       # trait list: creator-only, cooldown, revert (needs the default 12 h cooldown)
ACHAT_BASE=http://localhost:5002 node scripts/e2e/phase4.mjs       # bot memories → recall in the prompt → viewer edit/pin/forget → embedding lock
ACHAT_BASE=http://localhost:5002 node scripts/e2e/phase6.mjs       # sharing: request → approve → separate scope (messages and memories)
ACHAT_BASE=http://localhost:5002 node scripts/e2e/phase8-verify.mjs  # disable/reset, kill switch, quota, usage
ACHAT_BASE=http://localhost:5002 node scripts/e2e/phase3.mjs       # needs a REAL model (recall via summary, gap awareness); HistoryTurns=4, SummaryTailTokens=50, GapThresholdMinutes=1
ACHAT_BASE=http://localhost:5002 node scripts/e2e/phase7-verify.mjs  # new providers against a live endpoint
BASE=http://localhost:5002 node scripts/e2e/workqueue.mjs          # dead endpoint → summary + consolidation dead-letter → try-again → admin retry
```

`workqueue.mjs` needs a fast queue and low thresholds on the throwaway instance (it also
needs the mock): `-e ACHAT_Work__TickSeconds=2 -e ACHAT_Work__MaxAttempts=3
-e ACHAT_Work__BaseDelaySeconds=2 -e ACHAT_Work__MaxDelaySeconds=8
-e ACHAT_Chat__SummaryTailTokens=20 -e ACHAT_Turn__ConsolidateAbove=2
-e ACHAT_Evolution__CooldownHours=0`. The script flips a preset's endpoint dead, then to
the mock. `traits.mjs` must run **without** `Evolution__CooldownHours=0`.

Other scripts: `detached-chat.mjs` and `hub-reattach.mjs` (a run survives the page
leaving / re-attaches mid-reply), `interactions-check.mjs`, `theme-check.mjs`.

The mock LLM (`node scripts/e2e/mockllm.mjs [port]`, default 18999) serves
`/v1/models`, `/v1/chat/completions` (purpose-aware replies) and `/v1/embeddings`
(deterministic 768-dim vectors), and logs every request to `mock-requests.jsonl` —
phase scripts assert prompt structure against that log. It is how the pipeline is
verified when no real endpoint is available.

## Project layout

```
src/
  AChat.Core/            Domain models + interfaces (no EF, no providers)
  AChat.Infrastructure/  AppDbContext, migrations, LLM provider strategies, key protection
  AChat.Api/             Controllers, SignalR hub, chat service, Program.cs
  AChat.Tests/           Tests
  achat-ui/              React 19 + TypeScript + Vite SPA (plain CSS, Grove palette)
```

## Status

- **Phase 0 — Skeleton** ✅ solution, Docker stack, EF + pgvector migrations on start,
  `ACHAT_` config, JWT auth + bootstrap admin, Grove-themed React shell
- **Phase 1 — One bot, one model, one conversation** ✅ OpenAICompatible provider,
  preset CRUD with encrypted keys, load models / test connection, bot creation with
  core persona, one continuous conversation per (bot, user), SignalR streaming with
  stop, every LLM call logged (§4.4)
- **Phase 2 — Interaction log viewer** ✅ filterable list + full request/reasoning/
  response detail
- **Phase 3 — Continuity without embeddings** ✅ user timezone (browser capture +
  manual override), rolling summary worker (utility model, watermark, logged),
  token-budgeted prompt assembly, gap markers — verified: a bot recalls facts planted
  outside its recent window and reacts to a multi-hour silence
- **Phase 4 — Long-term memory** ✅ the bot records memories itself in its turn
  envelope (originally a background extraction pass, retired in §4.7), with
  dedup/supersede, cosine retrieval ranked by similarity + importance + recency with
  relative ages, recalled ids surfaced in chat and in the interaction log, memory
  viewer (list / search / edit / pin / forget / forget-all), embedding-model lock
  (§5.7) — verified: facts the bot remembered are recalled in later prompts, and a
  viewer edit flows into the bot's next answer
- **Phase 5 — Evolving personality** ✅ three-layer persona (core → evolved traits →
  directives, directives win). The evolved layer is a **list of short trait items**
  the bot adds and retires itself in its creator's conversation (rate-limited, capped,
  cooldown after human edits), versioned with history, edit, diff and revert; a slow
  consolidation pass merges the list when it grows past a threshold; per-bot
  evolution switch. The original prose-rewrite evolution pass was replaced by this in
  §4.7 — verified: a trait added in a turn shows up in the next prompt in order
  core → traits → directives, a second user cannot change it, and revert restores it
- **Phase 6 — Sharing** ✅ bot sharing: per-bot invite code, access requests + admin
  approval queue, per-user memory scope, shared bots badged, revoke (relationship
  retained), code regeneration, admin creates users — verified: a second user requests
  access, is approved, holds a conversation, and the first user cannot see the second
  user's messages or memories
- **Phase 7 — Remaining providers** ✅ Ollama, OpenAI, and Google AI Studio added
  alongside OpenAICompatible. All four are OpenAI-shaped and share one implementation
  (plain HTTP + System.Text.Json, no SDKs), differing only in default base URL and key
  handling (Ollama is keyless) — verified: each new provider lists models and a bot
  chats end-to-end against a live OpenAI-compatible endpoint
- **Phase 8 — Operational maturity** ✅ admin user management (disable / reset /
  delete, with self-lockout and audit-log guards), per-bot **kill switch** + **usage
  quota** + **per-user usage visibility** (who is spending the creator's key),
  interaction-log **retention** (background pruning, `Retention:InteractionDays`),
  **retry with backoff** for transient LLM failures (§4.5), non-root container,
  accessibility pass, backup guidance — verified: a disabled user is refused at login,
  a paused bot stops answering, a quota-capped bot stops at the cap, and the usage
  view breaks spend down by user
- **Durable work pipeline (§12b)** ✅ summarisation and persona consolidation run
  through a durable background-work queue: a failed pass retries with **backoff**
  and an **attempt cap**, then **dead-letters** into the admin panel instead of
  hammering the provider; admins can **retry** or **discard** any item. A failed chat
  turn is no longer a dead end — **"Try again"** re-runs the last unanswered turn —
  verified: a dead endpoint dead-letters summarisation and consolidation (3/3
  attempts, visible failure), "try again" succeeds once the endpoint is fixed, an
  admin retry re-arms both passes, and the queue clears
- **Turn envelope (§4.7)** ✅ one structured reply per turn: mood (damped), bot-written
  memories, and bot-written trait changes, replacing the separate extraction and
  prose-evolution passes — see *How a turn works*

## Backing up

Everything that matters — conversations, memories, personas, presets, the audit log,
**and the Data Protection key ring that decrypts your API keys** — lives in the
PostgreSQL database. There is no separate secret file or volume to back up. So a
backup is a database dump:

```sh
docker compose exec -T postgres pg_dump -U achat achat > achat-backup-$(date +%F).sql
```

Restore by dropping and re-creating the schema, then applying the dump (or restore
the dump to a fresh database and point `ACHAT_ConnectionStrings__DefaultConnection`
at it). **Back up before upgrading or migrating** — a restore that loses the key ring
makes stored API keys undecryptable and you'd have to re-enter them.
