# AChat — agent guide

AChat is a self-hosted platform that hosts *virtual friends*: chat bots with a
persistent personality and memory of their relationship with you. Continuity over
task-completion.

**Read [`docs/design.md`](docs/design.md) first** — it is the authoritative spec.
Sections marked `[DECIDED]` are locked; sections marked `[OPEN]` need a decision
before implementation. The phase plan at the end defines what "done" means per
phase — work in that order, and keep the app in a runnable state at each phase
boundary.

## Hard rules (abook conventions, adopted here)

- **The app always runs in Docker.** `docker compose up -d --build` is the only
  way to run it — `dotnet run` / `vite dev` are not used, not even in development.
  (Compile-only checks are fine: `dotnet build`, `npx tsc -b --noEmit`.)
- **Migrations are authored with the `dotnet ef` tool, never written by hand.**
  The app applies migrations automatically on start.
- **Manual database access during development is read-only.** `SELECT` is fine;
  any other statement by hand is not. Schema changes go through migrations; data
  changes go through the application.
- After every change, keep green: `dotnet build AChat.slnx`, `dotnet test AChat.slnx`,
  `npx tsc -b --noEmit` (in `src/achat-ui`).

## Layout

```
src/AChat.Core/           Domain models + interfaces (no EF, no providers)
src/AChat.Infrastructure/ EF Core (Postgres + pgvector), migrations, LLM strategies, key protection
src/AChat.Api/            ASP.NET Core: controllers, SignalR hub, chat service
src/AChat.Tests/          xUnit tests
src/achat-ui/             React 19 + TypeScript + Vite SPA (plain CSS, Grove palette)
```

- Config: `ACHAT_`-prefixed environment variables override `appsettings.json`
  (e.g. `ACHAT_Jwt__SigningKey`, `ACHAT_ConnectionStrings__DefaultConnection`).
- API keys are encrypted at rest with ASP.NET Core Data Protection (key ring in
  the database) — never logged, never returned to the browser.
- Every LLM call is logged in `LlmInteractions` (§4.4) — the primary debugging
  surface for "why did my friend say that".

## Verification

- E2E scripts live in `scripts/e2e/` (see the README *E2E scripts* section):
  `smoke.mjs` (Playwright, headless), `sigtest.mjs` (SignalR client), per-phase
  scripts (`phase3`–`phase6.mjs`, `phase7-verify.mjs`, `phase8-verify.mjs`,
  `workqueue.mjs`),
  `phase5-mock.mjs` (offline pipeline check), and `mockllm.mjs` (OpenAI-compatible
  mock with a request log). Run against the containerized app at
  `http://localhost:5001` (admin/admin bootstrap); the phase scripts point at a
  throwaway `achat-e2e` instance on `5002` with fast worker triggers.
  `phase7-verify.mjs` exercises the new providers against a live endpoint;
  `phase8-verify.mjs` exercises user management, the kill switch, quota, and usage.
- LLM endpoints: a local one at `http://192.168.2.33:1234/v1` (chat
  `qwen/qwen3.8-27b`, a reasoning model; embeddings
  `text-embedding-nomic-embed-text-v1.5`, 768-dim) — the **"Qwen local"** preset —
  and a reliable hosted one via the **"OpenRouter-NV"** preset (Nemotron
  generation + utility + embedding models). Phase scripts take `PHASE5_PRESET=<id>`
  to pick between them; the mock covers the case where neither is available.
