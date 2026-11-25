# Release notes

## Unreleased

- **One structured reply per turn.** Each reply now comes back as a small JSON
  envelope: the reply itself, the bot's **mood**, anything it wants to
  **remember**, and — in its creator's conversation — changes to its own
  **traits**. A reply that isn't valid JSON is still shown as plain text and
  changes nothing.
- **Mood.** Each bot has a mood toward each person, carried into the next turns.
  It is damped: it can change only after several turns and some time have passed.
- **Bot memories.** The bot writes its own memories as it talks (deduplicated and
  recalled as before). The separate background memory-extraction pass is gone;
  existing memories are untouched.
- **Traits as a list.** The evolved layer of a persona is now a list of short
  items the bot adds and retires itself, with limits (a few changes a day, a cap
  on items and length), a cooldown after you edit it, a version history with
  diffs, and revert. A slow consolidation pass merges the list when it grows
  long. Core persona and standing directives can never be changed by the bot.
  An existing prose persona is converted to one item on first read.
- **Removed.** The prose evolution rewrite, `PUT /persona`, and the
  extraction/evolution watermark columns (a migration drops them), plus the
  `Extraction__*` and `Evolution__MinNewMessages`/`MaxTailMessages` settings.
  New settings: `Turn__*` (see the README configuration table);
  `Evolution__MaxTokens` now defaults to 1024.

## v0.1.1 — 2026-08-24

- **Replies survive you leaving the page.** A chat run now executes on the
  server, detached from the browser: navigate away (or drop the network) and
  the reply keeps generating. When you come back, the finished message is in
  the timeline.
- **Re-attach mid-reply.** Reopening the chat (or recovering from a network
  drop) while a reply is in flight re-attaches the client and the remaining
  stream continues flowing.
- **Stop is still explicit.** The Stop button cancels the in-flight reply;
  the partial text is kept and "Try again" is offered.
- **One reply per (bot, user) at a time.** A second send while one is in
  flight is refused, so two open tabs can't double-spend your API key.

## v0.1.0 — 2026-08-23

First release. AChat is a self-hosted platform for *virtual friends* — chat
bots with a persistent personality and a memory of their relationship with
you.

### Chat & continuity
- Create bots with a core persona, speech style, and greeting; chat with
  token-streaming replies.
- **Continuity beyond the context window** — rolling conversation summaries
  and time-aware prompts keep long conversations coherent.
- **Long-term memory** — the bot extracts facts from your conversations and
  recalls them later (semantic search over embeddings). A memory viewer lets
  you pin, edit, or remove memories.
- **Evolving personality** — the bot's traits evolve from sustained
  conversation; directives shape behaviour; every evolution is versioned and
  revertible.

### Sharing
- Share a bot with another user via an invite code and an approval queue.
  Each friend has their own memory of the relationship.

### Models
- Providers: **OpenAI-compatible, Ollama, OpenAI, and Google AI Studio**,
  selectable per bot via presets; API keys are encrypted at rest.
- Preset editor: the model fields suggest models from the provider's live
  model list (free-form entry is still allowed).
- **"Show thinking" toggle per bot** (off by default) — when enabled, the
  model's reasoning blocks are shown above its replies.

### Admin & reliability
- Admin console: create, disable, and delete users; per-bot kill switch and
  message quota; per-user usage.
- **Durable background work** — summaries, memory extraction, and persona
  evolution run on a work queue with retries and a dead letter; failed
  passes can be retried from the admin screen ("Try again" also appears in
  the chat when a follow-up pass fails).
- **Interaction log** — every LLM call (prompt, response, tokens, errors)
  is inspectable from the UI, with configurable retention.
- Runs non-root in Docker; the CI workflow publishes multi-arch (amd64 +
  arm64) images to Docker Hub.

### UI
- Sidebar version pill links to the GitHub repository.
- Accessibility pass.
