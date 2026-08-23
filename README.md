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
| `Extraction__MinNewMessages` | `8` | New messages since the watermark that trigger an extraction pass (§5.3) |
| `Extraction__IdleMinutes` | `10` | …or conversation idle for this long |
| `Extraction__MaxCandidates` | `10` | Cap on candidates per pass |
| `Extraction__MaxTailMessages` | `40` | Tail size sent to the extractor |
| `Extraction__MaxTokens` | `1024` | Output cap for the extraction call (covers reasoning-model thinking) |
| `Evolution__MinNewMessages` | `40` | New creator messages since the last pass that trigger an evolution rewrite (§2.4) |
| `Evolution__CooldownHours` | `12` | Cooldown between rewrites — a human edit or revert also resets it |
| `Evolution__MaxTailMessages` | `60` | Conversation tail sent to the evolver |
| `Evolution__MaxTokens` | `512` | Output cap for the evolution call |

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
  -e ACHAT_Evolution__MinNewMessages=6 -e ACHAT_Evolution__CooldownHours=0 \
  -e ACHAT_Extraction__MinNewMessages=2 -e ACHAT_Extraction__IdleMinutes=0 \
  achat-achat
ACHAT_BASE=http://localhost:5002 node scripts/e2e/phase6.mjs   # sharing: request → approve → separate scope
ACHAT_BASE=http://localhost:5002 node scripts/e2e/phase5.mjs   # PHASE5_PRESET=<id> picks the preset
ACHAT_BASE=http://localhost:5002 node scripts/e2e/phase5-mock.mjs  # offline (mock on 18999)
ACHAT_BASE=http://localhost:5002 node scripts/e2e/phase4.mjs
ACHAT_BASE=http://localhost:5002 node scripts/e2e/phase3.mjs
```

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
- **Phase 4 — Long-term memory** ✅ background extraction (utility model, watermark,
  dedup/supersede), cosine retrieval ranked by similarity + importance + recency with
  relative ages, recalled ids surfaced in chat and in the interaction log, memory
  viewer (list / search / edit / pin / forget / forget-all), embedding-model lock
  (§5.7) — verified: facts planted outside the recent window are recalled by name, and
  a viewer edit flows into the bot's next answer
- **Phase 5 — Evolving personality** ✅ three-layer persona (core → evolved traits →
  directives, directives win), versioned evolved layer (history, edit, revert), slow
  background evolution over creator conversations only (utility model, watermark,
  cooldown, bounded by core + directives), per-bot evolution switch — verified: after
  sustained conversation the bot's evolved traits changed, the next prompt carried the
  layers in order, a standing directive was obeyed, and revert restored an earlier
  version
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
- **Phase 8 — Operational maturity**: see the delivery plan in `docs/design.md` §12a
