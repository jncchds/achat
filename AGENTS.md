# AGENTS.md — Architecture & Coding Guide for AChat

## Solution Structure

```
src/
  AChat.Core/          — Pure domain: entities, DTOs, enums, options, service interfaces
  AChat.Infrastructure/— Implementation: EF DbContext, all service impls, SK, Telegram, jobs
  AChat.Api/           — ASP.NET Core host: controllers, middleware, Program.cs, DI wiring
frontend/              — React/TypeScript/Vite SPA (MUI)
```

`AChat.Core` has **no project references** — everything else depends on it.  
`AChat.Infrastructure` references `AChat.Core`.  
`AChat.Api` references both.

## Key Conventions

### Logging — LoggerMessage source generation
All services that log must be `partial` classes using `[LoggerMessage]` attribute on `static partial` methods:
```csharp
public partial class MyService(...) : IMyService
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Did {Thing}")]
    private static partial void LogDidThing(ILogger logger, string thing);
}
```

### Service Registration
When a service class needs to be injected both by interface and by concrete type (e.g. for background jobs), register it **once** to avoid double instantiation:
```csharp
services.AddScoped<BotService>();
services.AddScoped<IBotService>(sp => sp.GetRequiredService<BotService>());
```

### EF Migrations
**Always** create migrations via the ef CLI tool — never hand-edit migration files:
```bash
dotnet ef migrations add <Name> --project src/AChat.Infrastructure --startup-project src/AChat.Api
```

### Options Pattern
All configuration sections use `IOptions<T>` with a `Section` constant:
```csharp
// In Core/Options/MyOptions.cs
public class MyOptions { public const string Section = "My"; ... }

// In Program.cs or ServiceExtensions.cs
services.Configure<MyOptions>(config.GetSection(MyOptions.Section));
```

### API Token Security
Preset API tokens are stored in the database but **never returned** in DTOs. The `PresetDto` only exposes `HasApiToken: bool`. The token is write-only (pass a new value to update it, omit to keep existing).

### Pragma Suppressions
- `#pragma warning disable SKEXP0070` — required for `AddGoogleAIGeminiChatCompletion`
- `#pragma warning disable SKEXP0001` — required for `OpenAIPromptExecutionSettings.FunctionChoiceBehavior`

## LLM / Semantic Kernel

### Provider mapping in `SemanticKernelFactory`
| ProviderType | SK connector | Notes |
|---|---|---|
| `Ollama` | `AddOpenAIChatCompletion` | Custom `HttpClient` base URL, `apiKey="ollama"` |
| `OpenAI` | `AddOpenAIChatCompletion` | Standard; base URL optional |
| `GoogleAI` | `AddGoogleAIGeminiChatCompletion` | Requires SKEXP0070 pragma |

### Memory extraction (BotMemoryPlugin)
`ChatService.StreamAsync` registers a `BotMemoryPlugin` with `KernelFunction("remember_fact")` on every request. With `FunctionChoiceBehavior.Auto()` the model calls this function to persist facts about the user inline during the response generation.

### Bot personality evolution
`BotService.RunEvolutionAsync(bot, direction?, ct)` is the core method, called by:
- `PersonalityEvolutionJob` (background, `direction=null` = organic)
- `BotService.NudgeEvolutionAsync` (manual trigger, optional direction hint)

## Chat Streaming Protocol (SSE)

`POST /api/conversations/{id}/chat` with `Content-Type: application/json` body `{ "content": "..." }`

Response: `Content-Type: text/event-stream`

```
data: Hello\n\n
data:  world\n\n
data: [DONE]\n\n
```

Frontend uses the Fetch API with a `ReadableStream` reader (not `EventSource`, since `EventSource` doesn't support POST).

## Telegram Integration

`TelegramHostedService` maintains a dictionary of `(botId → ITelegramBotClient)` and syncs every 30s based on `Bot.TelegramToken`. Unknown users trigger a `BotAccessRequest` and receive `Bot.UnknownUserReply`. Rate limiting is an in-memory sliding window (`TelegramRateLimiter`).

## Frontend

- **API layer**: `src/api/` — one file per domain, all using the Axios instance from `src/api/client.ts`
- **Auth state**: `src/store/AuthContext.tsx` — JWT stored in `localStorage`
- **Routing**: react-router-dom v7, protected by `<ProtectedRoute>` (optionally `requireAdmin`)
- **Data fetching**: `@tanstack/react-query` — invalidate queries after mutations
- **Build output**: `npm run build` writes to `../src/AChat.Api/wwwroot` (configured in `vite.config.ts`)

## Docker

```
docker compose up --build   # full stack
docker compose up db -d     # only database (for local backend dev)
```

Env overrides use `__` separator (e.g. `Jwt__Secret`, `ConnectionStrings__DefaultConnection`). See `.env.example`.
