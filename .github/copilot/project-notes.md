# AChat Project Notes

## Stack
- .NET 10 Core API + React/TypeScript (Vite + MUI), static files served from API
- 3 projects: AChat.Api, AChat.Core, AChat.Infrastructure
- PostgreSQL + pgvector (pgvector/pgvector:pg16) + EF Core
- Semantic Kernel 1.75+ for LLM (OpenAI connector for OpenAI+Ollama, Google connector)
- Telegram.Bot 22.x polling-based
- JWT auth, multi-user (admin/user roles)
- Docker-compose deployment

## Key Patterns
- EF migrations: only via `dotnet ef migrations add <Name> --project src/AChat.Infrastructure --startup-project src/AChat.Api`
- LoggerMessage source gen: `[LoggerMessage]` on `static partial` methods in `partial` classes
- SSE streaming: `text/event-stream` with `IAsyncEnumerable<StreamingChatMessageContent>`
- Bot personality nudge: LLM generates updated personality without clearing history
- Bot personality replace: updates personality + clears all conversation histories
- Rate limiting: in-memory sliding window (global + per-bot Telegram)
- Telegram: single hosted service managing dict of active bot pollers

## Service Registration
When a service class needs to be injected both by interface and by concrete type, register it **once**:
```csharp
services.AddScoped<BotService>();
services.AddScoped<IBotService>(sp => sp.GetRequiredService<BotService>());
```

## Bot Memory & Evolution
- `BotUserMemory` entity: `bot_id`, `user_id`, `facts` (JSONB `string[]`), `updated_at`
- Memory injected as hidden system message per-user per-conversation turn (never shared)
- Memory extraction: SK `BotMemoryPlugin` with `RememberFact(string fact)` function, `FunctionChoiceBehavior.Auto()`
- Evolution: `PersonalityEvolutionJob` `IHostedService` timer (configurable interval, default 24h), uses owner conversations only
- `bots` table columns: `last_evolved_at`, `evolution_interval_hours` (nullable, falls back to global config)
- Nudge: manual early-trigger for evolution job, accepts optional direction hint, bypasses interval check, does NOT clear history
- Manual personality replace still exists (clears history)

## LLM / Semantic Kernel Provider Mapping
| `ProviderType` | SK connector | Notes |
|---|---|---|
| `Ollama` | `AddOpenAIChatCompletion` | Custom `HttpClient` base URL, `apiKey="ollama"` |
| `OpenAI` | `AddOpenAIChatCompletion` | Standard; base URL optional |
| `GoogleAI` | `AddGoogleAIGeminiChatCompletion` | Requires `#pragma warning disable SKEXP0070` |

## Pragma Suppressions
- `#pragma warning disable SKEXP0070` — required for `AddGoogleAIGeminiChatCompletion`
- `#pragma warning disable SKEXP0001` — required for `OpenAIPromptExecutionSettings.FunctionChoiceBehavior`

## API Token Security
Preset API tokens stored in DB are **never returned** in DTOs. `PresetDto` only exposes `HasApiToken: bool`. Token is write-only.

## Chat Streaming Protocol (SSE)
`POST /api/conversations/{id}/chat` → `Content-Type: text/event-stream`
```
data: Hello\n\n
data:  world\n\n
data: [DONE]\n\n
```
Frontend uses Fetch API with `ReadableStream` (not `EventSource` — doesn't support POST).

## Frontend
- API layer: `src/api/` — one file per domain, all using Axios instance from `src/api/client.ts`
- Auth state: `src/store/AuthContext.tsx` — JWT in `localStorage`
- Routing: react-router-dom v7, protected by `<ProtectedRoute>` (optionally `requireAdmin`)
- Data fetching: `@tanstack/react-query` — invalidate queries after mutations
- Build output: `npm run build` → `../src/AChat.Api/wwwroot`

## Docker
```
docker compose up --build   # full stack
docker compose up db -d     # only database (for local backend dev)
```
Env overrides use `__` separator (e.g. `Jwt__Secret`, `ConnectionStrings__DefaultConnection`).
