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

Point a **preset** at any OpenAI-compatible endpoint (v1 provider), create a **bot**
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
- **Phase 2 — Interaction log viewer** — API exists; UI next
- Phases 3–8: see the delivery plan in `docs/design.md` §12a
