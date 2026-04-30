# AChat

A multi-user platform for self-evolving chatbots accessible via web and Telegram.

## License

MIT

---

## Tech Stack

| Layer | Technology |
|---|---|
| Backend | .NET 10, ASP.NET Core, SignalR, EF Core 10 |
| Database | PostgreSQL 16 + pgvector |
| Frontend | React 19 + TypeScript + Vite |
| LLM Providers | Ollama, OpenAI, Google AI Studio |
| Telegram | Telegram.Bot (per-bot webhook) |
| Deployment | Docker Compose |

---

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js 20+](https://nodejs.org/)
- [Docker + Docker Compose](https://docs.docker.com/compose/)
- `dotnet-ef` CLI tool: `dotnet tool install --global dotnet-ef`

---

## Quick Start (Local Dev)

### 1. Clone and configure

```bash
git clone <repo-url>
cd achat
cp .env.example .env
```

Edit `.env` — at minimum set `JWT_SECRET` and `ENCRYPTION_KEY`.

To generate a valid 32-byte AES key:
```powershell
[Convert]::ToBase64String((1..32 | ForEach-Object { [byte](Get-Random -Max 256) }))
```

### 2. Start PostgreSQL

```bash
docker compose up postgres -d
```

### 3. Apply database migrations

```powershell
cd src/backend
dotnet ef database update --project AChat.Infrastructure/AChat.Infrastructure.csproj --startup-project AChat.Api/AChat.Api.csproj
```

### 4. Run the API

```powershell
cd src/backend/AChat.Api
dotnet run
```

API available at `http://localhost:5000`. Swagger UI at `http://localhost:5000/swagger`.

### 5. Run the frontend

```powershell
cd src/frontend/achat-web
npm install
npm run dev
```

Frontend available at `http://localhost:5173`.

---

## Environment Variables

All variables can be set in `.env` (Docker Compose) or `appsettings.json` / environment variables for the API.

| Variable | Description | Required |
|---|---|---|
| `ConnectionStrings__DefaultConnection` | PostgreSQL connection string | Yes |
| `Jwt__Secret` | JWT signing secret, minimum 32 characters | Yes |
| `Jwt__Issuer` | JWT issuer (default: `AChat`) | No |
| `Jwt__Audience` | JWT audience (default: `AChat`) | No |
| `Jwt__ExpiresInMinutes` | Token lifetime in minutes (default: `1440`) | No |
| `Encryption__Key` | AES-256 key as Base64-encoded 32 bytes | Yes |
| `Evolution__SummarizationThreshold` | Messages before summarization triggers (default: `50`) | No |
| `Evolution__PersonaEvolutionMessageInterval` | Messages between persona updates (default: `20`) | No |

---

## Project Structure

```
achat/
├── src/
│   ├── backend/
│   │   ├── AChat.Core/          # Domain models, interfaces (no infrastructure deps)
│   │   ├── AChat.Infrastructure/# EF Core, LLM providers, Telegram, encryption
│   │   ├── AChat.Api/           # Web API + SignalR hubs + controllers
│   │   └── AChat.Worker/        # Background workers (summarization, persona evolution)
│   └── frontend/
│       └── achat-web/           # React 19 + TypeScript + Vite
├── docker-compose.yml
├── .env.example
├── AGENTS.md                    # Architecture reference for AI agents
└── README.md
```

---

## Adding EF Migrations

**Always use the CLI — never hand-write migration files.**

```powershell
cd src/backend
dotnet ef migrations add <MigrationName> `
  --project AChat.Infrastructure/AChat.Infrastructure.csproj `
  --startup-project AChat.Api/AChat.Api.csproj
```

---

## Docker Compose (Full Stack)

```bash
docker compose up --build
```

Services:
- `postgres` — PostgreSQL 16 + pgvector on port `5432`
- `api` — AChat API on port `5000`
- `web` — React app served by nginx on port `80`

---

## Features

- **Multi-user**: each user has their own bots and LLM provider presets
- **Self-evolving bots**: RAG memory retrieval + conversation summarization + dynamic persona rewriting
- **LLM providers**: Ollama (local), OpenAI, Google AI Studio — configurable per bot
- **Telegram integration**: each bot can have its own Telegram token with a per-bot access whitelist
- **Conversation threads**: chat is split into per-user conversations with titles updated from the latest discussed topic
- **Continuation flow**: web UI shows all conversations and supports starting a new one; Telegram supports `/conversations` (or `/continue`) selection and `/new`
- **Access control**: bot owner approves/denies web and Telegram users; unknown senders get rejected and queued for approval
- **Full history**: all messages (web + Telegram) stored server-side, scoped by bot+user+conversation

---

## Conversation APIs

| Endpoint | Description |
|---|---|
| `GET /api/bots/{botId}/conversations` | List current user's conversations for a bot |
| `POST /api/bots/{botId}/conversations` | Start a new conversation |
| `GET /api/bots/{botId}/conversations/{conversationId}/messages` | Load message history for a conversation |

SignalR chat now accepts an optional conversation id:

- `SendMessage(botId, content, conversationId?)`
