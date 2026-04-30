# AChat — Agent Guide

This document is the authoritative reference for any AI agent working in this repository.
Keep it up to date whenever architecture, domain models, API surface, project structure, or conventions change.

---

## Project Overview

AChat is a multi-user platform for self-evolving chatbots. Users own bots that adapt their personality over time using three mechanisms:
1. **RAG memory** — pgvector cosine-similarity retrieval of relevant past messages per conversation
2. **Conversation summarization** — background worker compresses old history into rolling summaries
3. **Dynamic persona evolution** — background worker periodically rewrites the bot's personality based on interaction patterns

Bots are accessible via a React web UI and optionally via their own dedicated Telegram bot.

---

## Repository Structure

```
achat/
├── src/
│   ├── backend/
│   │   ├── AChat.sln / AChat.slnx
│   │   ├── AChat.Core/              # Domain models, interfaces (NO infrastructure dependencies)
│   │   │   ├── Entities/            # All EF entity classes + enums
│   │   │   ├── LLM/                 # ILLMChatProvider, ILLMEmbeddingProvider, ILLMProviderFactory, LLMChatRequest
│   │   │   └── Services/            # IEncryptionService
│   │   ├── AChat.Infrastructure/    # EF Core DbContext, LLM provider impls, Telegram.Bot, AesEncryptionService
│   │   │   ├── Data/                # AppDbContext, AppDbContextFactory, Migrations/
│   │   │   ├── LLM/                 # OllamaProvider, OpenAIProvider, GoogleAIStudioProvider, LLMProviderFactory
│   │   │   ├── Security/            # AesEncryptionService
│   │   │   └── Telegram/            # TelegramWebhookService, TelegramHandlerService
│   │   ├── AChat.Api/               # ASP.NET Core 10 Web API + SignalR
│   │   │   ├── Controllers/         # AuthController, PresetsController, BotsController, ConversationsController, AccessController, TelegramController
│   │   │   ├── Hubs/                # ChatHub (SignalR)
│   │   │   └── Models/              # Request/response DTOs
│   │   └── AChat.Worker/            # IHostedService background workers
│   │       ├── SummarizationWorker.cs
│   │       └── PersonaEvolutionWorker.cs
│   └── frontend/
│       └── achat-web/               # React 19 + TypeScript + Vite
├── docker-compose.yml
├── docker-compose.override.yml
├── .env.example
├── AGENTS.md                        # ← this file
└── README.md
```

---

## Domain Models (`AChat.Core/Entities/`)

| Entity | Key Fields |
|---|---|
| `User` | Id, Email?, PasswordHash?, TelegramId (long?), IsStubAccount |
| `LLMProviderPreset` | UserId, Name, Provider (enum), EncryptedApiKey, BaseUrl, ModelName, EmbeddingModel, ParametersJson |
| `Bot` | OwnerId, Name, Age?, Gender (freeform string), CharacterDescription, EvolvingPersonaPrompt, LLMProviderPresetId, EmbeddingPresetId, EncryptedTelegramBotToken |
| `BotConversation` | BotId, UserId, Title, CreatedAt, UpdatedAt, LastMessageAt |
| `BotConversationState` | BotId, UserId, CurrentConversationId, UpdatedAt (tracks active conversation per bot+user) |
| `Message` | BotId, UserId, ConversationId, Role (enum), Content, Embedding (vector), Source (enum: Web/Telegram) |
| `BotMemorySummary` | BotId, UserId, ConversationId, SummaryText, Embedding (vector), MessageRangeStart, MessageRangeEnd |
| `BotPersonaSnapshot` | BotId, SnapshotText — immutable audit log of persona changes |
| `BotAccessList` | BotId, SubjectType (AchatUser/TelegramUser), SubjectId (string), Status (Allowed/Denied) |
| `BotAccessRequest` | BotId, SubjectType, SubjectId, DisplayName?, Status (Pending/Approved/Denied), ResolvedByUserId? |

**Access control applies to both web and Telegram.**  
Bot owner is always implicitly allowed on web. Owner is auto-added to `BotAccessList(Allowed)` when `EncryptedTelegramBotToken` is first set.

**Stub users**: When a Telegram-only user is approved, a `User` with `IsStubAccount=true` is auto-created (TelegramId as identifier, no email/password).

---

## LLM Provider Abstraction

- **Interfaces**: `ILLMChatProvider`, `ILLMEmbeddingProvider`, `ILLMProviderFactory` (in `AChat.Core/LLM/`)
- **Implementations** (in `AChat.Infrastructure/LLM/`): `OllamaProvider`, `OpenAIProvider`, `GoogleAIStudioProvider`
- Factory resolves provider from a `LLMProviderPreset`
- Chat streaming uses `IAsyncEnumerable<string>` piped through SignalR

---

## API Surface (`AChat.Api`)

| Area | Endpoints |
|---|---|
| Auth | `POST /api/auth/register`, `POST /api/auth/login`, `PUT /api/auth/telegram` |
| Presets | `GET/POST /api/presets`, `GET/PUT/DELETE /api/presets/{id}` |
| Bots | `GET/POST /api/bots`, `GET/PUT/DELETE /api/bots/{id}`, `GET /api/bots/{id}/persona-history` |
| Conversations | `GET/POST /api/bots/{botId}/conversations`, `GET /api/bots/{botId}/conversations/{conversationId}/messages` |
| Access | `GET /api/bots/{id}/access-requests`, `POST .../approve`, `POST .../deny`, `GET/DELETE /api/bots/{id}/access-list` |
| Chat | SignalR hub `/hubs/chat` — `SendMessage(botId, content, conversationId?)` → streaming `ReceiveToken(chunk)` |
| Telegram | `POST /api/telegram/webhook/{botId}` — per-bot, validated via secret token header |

All endpoints except register/login require JWT Bearer authentication.

---

## Conventions & Rules

### EF Core Migrations
**NEVER hand-write migration files.** Always use the CLI:
```powershell
cd src/backend
dotnet ef migrations add <MigrationName> --project AChat.Infrastructure/AChat.Infrastructure.csproj --startup-project AChat.Api/AChat.Api.csproj
dotnet ef database update --project AChat.Infrastructure/AChat.Infrastructure.csproj --startup-project AChat.Api/AChat.Api.csproj
```

### Security
- `EncryptedTelegramBotToken` and `EncryptedApiKey` are AES-256 encrypted at rest via `IEncryptionService`
- Keys must be 32 bytes, Base64-encoded, stored in `Encryption:Key` config (env var in production)
- API keys/tokens are **never** returned to clients in plaintext after initial save
- Passwords use PBKDF2-SHA256 with 350,000 iterations and a random 16-byte salt

### Living Documents
- **This file (`AGENTS.md`)**: update whenever architecture, models, API, or conventions change
- **`README.md`**: update whenever setup steps, env vars, Docker config, or major features change

### License
MIT

---

## Frontend (`src/frontend/achat-web`)

- React 19 + TypeScript + Vite
- Key dependencies: `@microsoft/signalr`, `@tanstack/react-query`, `react-router-dom`

**Planned routes:**
- `/login`, `/register`
- `/profile` — link TelegramId
- `/presets` — CRUD LLM provider presets
- `/bots` — list + create
- `/bots/:id/settings` — edit bot (name, age, gender, character, preset, Telegram token)
- `/bots/:id/chat` — real-time SignalR chat
- `/bots/:id/persona` — persona snapshot timeline
- `/bots/:id/access-requests` — approve/deny Telegram access requests
- `/bots/:id/access-list` — manage whitelist

---

## Evolution Engine Thresholds (configurable in `appsettings.json`)

```json
"Evolution": {
  "SummarizationThreshold": 50,
  "SummarizationBatchSize": 30,
  "PersonaEvolutionMessageInterval": 20,
  "RecentMessageWindowSize": 20,
  "RagTopK": 5
}
```

---

## Telegram Integration

- Each `Bot` has its own optional Telegram bot token
- Webhook: `POST /api/telegram/webhook/{botId}` — registered/updated via Telegram `setWebhook` API dynamically when token is saved
- **Unknown sender** → reply "I don't know you, go away" + create `BotAccessRequest(Pending)`
- **Denied sender** → silently drop message, no reply
- **Approved sender** → route to chat engine; history stored with `Source=Telegram` and scoped by `ConversationId`
- Commands:
  - `/new` (also `/newconversation`) → creates and activates a new conversation
  - `/conversations` (also `/continue`) → shows inline buttons to choose an existing conversation
- Response style: send `typing...` action while LLM generates, then send complete message
- Owner receives inline `[Approve] [Deny]` keyboard message on new access requests
