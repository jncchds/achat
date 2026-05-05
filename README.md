# AChat

A multi-user LLM chat platform with bot personality evolution, Telegram integration, and multiple provider support.

## Features

- **Multi-user** with JWT authentication (Admin / User roles)
- **Bots with evolving personalities** — background job periodically refines bot personality based on interactions with their owner
- **Multiple LLM providers** — Ollama, OpenAI API, Google AI (via Semantic Kernel)
- **Per-user LLM presets** — each user manages their own provider configurations
- **Bot access control** — owners approve/reject access requests from other users
- **Telegram integration** — bots can be exposed over Telegram
- **SSE streaming chat** — responses are streamed token-by-token
- **Inline memory extraction** — bots remember facts about users they talk to
- **LLM usage tracking** — per-user and admin-wide usage logs

## Tech Stack

| Layer | Technology |
|-------|-----------|
| Backend | .NET 10 / ASP.NET Core 10 |
| LLM abstraction | Semantic Kernel 1.75.0 |
| ORM | Entity Framework Core 10 + Npgsql + pgvector |
| Database | PostgreSQL 16 with pgvector |
| Frontend | React 19 + TypeScript + Vite + MUI |
| Auth | JWT Bearer |
| Telegram | Telegram.Bot 22.x |
| Deployment | Docker Compose |

## Quick Start (Docker)

```bash
cp .env.example .env
# Edit .env with your secrets (especially JWT_SECRET and INITIAL_PASSWORD)
docker compose up --build
```

The app will be available at **http://localhost:8080**.

Default admin credentials: `admin` / `changeme` (override via `INITIAL_USER` / `INITIAL_PASSWORD` in `.env`).

## Development Setup

### Prerequisites
- .NET 10 SDK
- Node.js 22+
- PostgreSQL 16 with pgvector extension (or run `docker compose up db`)

### Run the database only

```bash
docker compose up db -d
```

### Run the backend

```bash
cd src/AChat.Api
dotnet run
```

The API listens on `http://localhost:5000`.

### Run the frontend (dev mode with hot reload)

```bash
cd frontend
npm install
npm run dev
```

The frontend dev server starts at `http://localhost:5173` and proxies `/api` to `http://localhost:5000`.

## Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `POSTGRES_PASSWORD` | `achat` | PostgreSQL password |
| `JWT_SECRET` | `CHANGE_ME_...` | JWT signing secret (≥32 chars) |
| `INITIAL_USER` | `admin` | Initial admin username |
| `INITIAL_PASSWORD` | `changeme` | Initial admin password |

All app settings can be overridden via environment variables using `__` as the separator, e.g.:
- `Jwt__ExpiryHours=48`
- `BotEvolution__IntervalHours=12`
- `Telegram__DefaultUnknownUserReply=Access denied`

## Project Structure

```
AChat.sln
├── src/
│   ├── AChat.Api/           # ASP.NET Core host, controllers, middleware, Program.cs
│   ├── AChat.Core/          # Entities, DTOs, interfaces, options enums (no dependencies)
│   └── AChat.Infrastructure/# EF DbContext, services, SK factory, Telegram, background jobs
└── frontend/                # React + Vite frontend (builds to src/AChat.Api/wwwroot)
```

## Adding a New EF Migration

```bash
dotnet ef migrations add <MigrationName> \
  --project src/AChat.Infrastructure \
  --startup-project src/AChat.Api
```

Migrations are applied automatically on application startup.

## Bot Personality Evolution

Bots evolve their personality automatically via a background job that:
1. Runs every hour (checks each bot's configured interval)
2. Requires a minimum number of owner messages since last evolution (default: 10)
3. Analyzes recent conversation history to organically update the personality

Owners can also trigger evolution manually via **Settings → Trigger Evolution Now**, optionally providing a direction hint (e.g. "be more concise").
