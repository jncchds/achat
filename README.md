# AChat

AChat is a multi-user platform for self-evolving chatbots with:

- a React web app
- a .NET API + SignalR real-time chat backend
- PostgreSQL + pgvector memory storage
- optional per-bot Telegram integration

---

## What it does today

- **Multi-user bot ownership** with JWT auth and per-user presets
- **LLM abstraction** across Ollama, OpenAI, and Google AI Studio
- **RAG-style memory** via vector embeddings (`pgvector`) for chat context
- **Conversation management** (list/create/rename/delete + history)
- **Background evolution engine**:
  - conversation summarization worker
  - persona evolution worker
  - optional bot-initiated message after evolution
- **Telegram support per bot** with webhook validation, inbound admission limiting, and durable outbound dispatch queue with retry/backoff on Telegram `429`
- **Access control workflows** for non-owner users (approve/deny access requests)
- **Admin user management** endpoints/UI (`/api/admin/users`, `/admin/users`)

---

## Tech stack

| Layer | Technology |
|---|---|
| Backend | .NET 10, ASP.NET Core, SignalR, EF Core 10 |
| Database | PostgreSQL 16 + pgvector |
| Frontend | React 19 + TypeScript + Vite |
| LLM Providers | Ollama, OpenAI, Google AI Studio |
| Telegram | Telegram.Bot |
| Deployment | Docker Compose |

---

## Repository layout

```
achat/
├── src/
│   ├── backend/
│   │   ├── AChat.Core/           # Domain entities + interfaces
│   │   ├── AChat.Infrastructure/ # EF Core, provider impls, Telegram, encryption
│   │   ├── AChat.Api/            # Controllers, SignalR hub, hosted workers
│   │   └── AChat.Worker/         # Legacy compatibility host
│   └── frontend/
│       └── achat-web/            # React app
├── docker-compose.yml
├── docker-compose.override.yml
├── .env.example
├── AGENTS.md
└── README.md
```

---

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js 20+](https://nodejs.org/)
- [Docker + Docker Compose](https://docs.docker.com/compose/)
- `dotnet-ef` CLI: `dotnet tool install --global dotnet-ef`

---

## Quick start (recommended): Docker Compose

1. Create `.env` from `.env.example`.
2. Ensure these **Compose-required** values are set:
   - `POSTGRES_PASSWORD`
   - `JWT_SECRET`
   - `ENCRYPTION_KEY` (Base64 32-byte AES key)
   - `TELEGRAM_WEBHOOK_BASE_URL`
   - `ADMIN_EMAIL`
   - `ADMIN_PASSWORD`
3. Run:

```bash
docker compose up --build
```

### Services and ports

- `db`: PostgreSQL on `localhost:5432`
- `api`: ASP.NET Core on `localhost:8080`

The API container also serves the built frontend static files from `wwwroot`, so the app is available from the API host.

---

## Local development (split mode)

Run DB/API and frontend separately for fast iteration.

### 1) Start DB + API (Docker)

```bash
docker compose up db api --build
```

API will be on `http://localhost:8080`.

### 2) Run frontend dev server

```bash
cd src/frontend/achat-web
npm install
npm run dev
```

Frontend: `http://localhost:5173`

Vite proxies `/api` and `/hubs` to `http://localhost:8080` by default.

### Optional: run API directly with `dotnet run`

If running `AChat.Api` outside Docker, default local URL from `launchSettings.json` is `http://localhost:5145`.
In that case, either:

- set `VITE_API_URL=http://localhost:5145` for frontend dev, or
- run the API on `8080` to match Vite proxy defaults.

---

## Authentication and user lifecycle

- Login endpoint is implemented: `POST /api/auth/login`
- Telegram linking endpoint is implemented: `PUT /api/auth/telegram`
- Admin-only user management is implemented:
  - `GET /api/admin/users`
  - `POST /api/admin/users`
  - `DELETE /api/admin/users/{id}`
- On startup, API can seed an initial admin from `Admin:Email` and `Admin:Password` **if no non-stub users exist**.
- Optional startup override: set `Admin:ForceUpdateFirstUser=true` to force-update the first non-stub user from `Admin:Email`/`Admin:Password` on startup.

> Note: The frontend includes a `/register` page and client call to `POST /api/auth/register`, but that backend endpoint is not currently implemented.

---

## API surface (current)

All endpoints require JWT auth unless noted otherwise.

### Auth

- `POST /api/auth/login`
- `PUT /api/auth/telegram`

### Admin (policy: `AdminOnly`)

- `GET /api/admin/users`
- `POST /api/admin/users`
- `DELETE /api/admin/users/{id}`

### Presets

- `GET /api/presets`
- `GET /api/presets/{id}`
- `POST /api/presets`
- `PUT /api/presets/{id}`
- `DELETE /api/presets/{id}`

### Bots

- `GET /api/bots`
- `GET /api/bots/{id}`
- `POST /api/bots`
- `PUT /api/bots/{id}`
- `DELETE /api/bots/{id}`
- `POST /api/bots/randomize-persona`
- `POST /api/bots/{id}/persona-push`
- `DELETE /api/bots/{id}/persona-push`
- `GET /api/bots/{id}/persona-history`

### Conversations

- `GET /api/bots/{botId}/conversations`
- `POST /api/bots/{botId}/conversations`
- `PUT /api/bots/{botId}/conversations/{conversationId}`
- `DELETE /api/bots/{botId}/conversations/{conversationId}`
- `GET /api/bots/{botId}/conversations/{conversationId}/messages`

### Access control

- `GET /api/bots/{botId}/access-requests`
- `POST /api/bots/{botId}/access-requests/{requestId}/approve`
- `POST /api/bots/{botId}/access-requests/{requestId}/deny`
- `GET /api/bots/{botId}/access-list`
- `DELETE /api/bots/{botId}/access-list/{entryId}`

### Telegram (unauthenticated webhook endpoint)

- `POST /api/telegram/webhook/{botId}`

---

## SignalR contract

Hub: `/hubs/chat`

Client -> server:

- `SendMessage(botId, content, conversationId?)`

Server -> client:

- `ConversationResolved`
- `ReceiveToken`
- `ReceiveMessageComplete`
- `Error`
- `BotInitiatedMessageStart`

---

## Telegram behavior (implemented)

- Per-bot token and webhook registration via Telegram API when token is saved
- Webhook request validation via `X-Telegram-Bot-Api-Secret-Token`
- Inbound global rate limiting (admission)
- Outbound durable queue in DB with:
  - global + per-bot throttling
  - retry/backoff for `429`
- Unknown sender flow:
  - bot replies: `I don't know you, go away`
  - pending access request is created
  - owner receives inline `Approve / Deny` callback keyboard message
- Commands:
  - `/new` (`/newconversation`, `/new_conversation`)
  - `/conversations` (`/continue`)

---

## Configuration / environment variables

### Core required

| Variable | Purpose |
|---|---|
| `POSTGRES_USER` | PostgreSQL username |
| `POSTGRES_PASSWORD` | PostgreSQL password |
| `JWT_SECRET` | JWT signing key (min 32 chars recommended) |
| `ENCRYPTION_KEY` | Base64-encoded 32-byte AES key |
| `ADMIN_EMAIL` | Seed admin email |
| `ADMIN_PASSWORD` | Seed admin password |

### Common optional

| Variable | Default |
|---|---|
| `JWT_ISSUER` | `achat` |
| `JWT_AUDIENCE` | `achat` |
| `JWT_EXPIRES_IN_MINUTES` | `60` |
| `ADMIN_FORCE_UPDATE_FIRST_USER` | `false` |

### Telegram-related

| Variable | Purpose |
|---|---|
| `TELEGRAM_WEBHOOK_BASE_URL` | Public HTTPS base URL used when registering webhooks |
| `TELEGRAM_RATE_LIMIT_ENABLED` | `true` |
| `TELEGRAM_RATE_LIMIT_GLOBAL_INBOUND_PER_SECOND` | `60` |
| `TELEGRAM_RATE_LIMIT_GLOBAL_INBOUND_BURST` | `120` |
| `TELEGRAM_RATE_LIMIT_GLOBAL_OUTBOUND_PER_SECOND` | `30` |
| `TELEGRAM_RATE_LIMIT_GLOBAL_OUTBOUND_BURST` | `60` |
| `TELEGRAM_RATE_LIMIT_PER_BOT_OUTBOUND_PER_SECOND` | `20` |
| `TELEGRAM_RATE_LIMIT_PER_BOT_OUTBOUND_BURST` | `30` |
| `TELEGRAM_RATE_LIMIT_QUEUE_CAPACITY` | `5000` |
| `TELEGRAM_RATE_LIMIT_DISPATCHER_IDLE_DELAY_MS` | `25` |
| `TELEGRAM_RATE_LIMIT_MAX_RETRY_ATTEMPTS` | `5` |
| `TELEGRAM_RATE_LIMIT_DEFAULT_RETRY_AFTER_SECONDS` | `2` |

### Evolution engine

| Variable | Default |
|---|---|
| `SUMMARIZATION_THRESHOLD` | `50` |
| `SUMMARIZATION_BATCH_SIZE` | `30` |
| `PERSONA_EVOLUTION_MESSAGE_INTERVAL` | `20` |
| `RECENT_MESSAGE_WINDOW_SIZE` | `20` |
| `RAG_TOP_K` | `5` |

---

## Database migrations

Do not hand-write migration files.

Create migration:

```powershell
cd src/backend
dotnet ef migrations add <MigrationName> --project AChat.Infrastructure/AChat.Infrastructure.csproj --startup-project AChat.Api/AChat.Api.csproj
```

Apply migration:

```powershell
cd src/backend
dotnet ef database update --project AChat.Infrastructure/AChat.Infrastructure.csproj --startup-project AChat.Api/AChat.Api.csproj
```

The API also applies migrations automatically on startup.

---

## Security notes

- LLM API keys and Telegram bot tokens are encrypted at rest via AES (`IEncryptionService`)
- Password hashing uses PBKDF2-SHA256 with 350,000 iterations and random salt
- API keys/tokens are not returned to clients in plaintext after save

---

## Frontend routes

- `/login`
- `/register` (UI present; backend register endpoint not implemented)
- `/bots`
- `/bots/new`
- `/bots/:id/settings`
- `/bots/:id/chat`
- `/bots/:id/persona`
- `/bots/:id/access-requests`
- `/bots/:id/access-list`
- `/presets`
- `/profile`
- `/admin/users`

---

## License

MIT
