Task T5 (end-to-end check, polish, PR description) of the AChat turn-envelope series. An orchestrator session runs the series one task at a time, with one session per task.

First, read the worker rules and follow them throughout. They live only on the integration branch, so before your worktree exists read them with `git show worktree-structured-turn-design:docs/orchestration/turn-envelope/briefs/common.md` (cwd `/mnt/git/achat`).

- Worktree: `/mnt/git/achat/.claude/worktrees/task-05`
- Branch: `task/05-e2e-polish`, based on the local `worktree-structured-turn-design` branch.
- Read: `docs/design.md` §4.7; reports `task-03.md` and `task-04.md`; `AGENTS.md` (LLM endpoints, e2e scripts); `README.md`.

## Goal
Prove the whole envelope flow against a **real model** in Docker, fix what breaks, and write the PR description. Everything so far was verified against the mock only.

## Scope
1. **Real-model run.** Use the throwaway `achat-e2e` stack on port 5002 (never the 5001 instance). Endpoints (see AGENTS.md): the local "Qwen local" preset at `http://192.168.2.33:1234/v1` (chat `qwen/qwen3.8-27b`, a reasoning model; embeddings `text-embedding-nomic-embed-text-v1.5`) — probe it first, it may be offline — else the "OpenRouter-NV" preset (needs a key from the existing preset data; if you cannot get one, say so and stop that part rather than guessing). Drive real conversations through SignalR (`sigtest.mjs`-style or `phase3.mjs`) and check, with the §4.4 interaction log as the evidence:
   - the model actually returns the envelope (valid JSON, `reply` first) and the reply streams live without JSON artifacts; measure how often it is invalid over ≥ 20 turns, and look at what the invalid ones look like;
   - a reasoning model's thinking does not leak into `reply` or break parsing;
   - `memories`: plant 3 facts across turns, confirm they're stored (`BotRemember`), deduped on repetition, and recalled by name far outside the history window (phase4/phase3 style);
   - `mood`: over ~15 turns of ordinary chat it stays null/unchanged most turns and does not swing; after an emotionally clear exchange it may change at most once within the damping window;
   - `traits` (creator only): over a sustained creator conversation, traits are added rarely and sensibly, never for a second user, guardrails drop the excess, revert works;
   - Stop mid-stream and a "try again" still work with the envelope.
2. **Fix what breaks.** Prompt wording (`PromptAssembly.BuildEnvelopeInstructions`), parsing tolerance (`TurnEnvelope`), JSON mode: if a provider rejects `response_format`, do **not** add it blindly — LM Studio rejects `json_object` — only add an opt-in per provider if you can show it helps with measurements. Keep fixes small and tested; add regression tests for each parsing case you find.
3. **Polish.** Look for leftovers: stale XML-doc comments mentioning extraction/prose evolution, the interaction viewer showing `Notes` clearly, UI copy. No redesign.
4. **PR description.** Write `docs/orchestration/turn-envelope/pr-description.md` (summary, what changed by area, config keys added/removed, migrations, how it was verified incl. the real-model numbers, known limits). End it with the `🤖 Generated with [Claude Code](https://claude.com/claude-code)` line and the session link `https://claude.ai/code/session_01LEFDEvpVMnLkqozcJfznsp`.

## Tests
Build, tests and tsc stay green; any new parsing/prompt logic has tests.

## Runtime check
Scope 1 is the runtime check. Report the real numbers (turns, valid-envelope rate, memories stored/recalled, mood changes, trait changes) in the report. Bring the stack down, remove its volume and images, prune.

## Docs
The report, the PR description, small doc corrections as found.

Screenshots (SendUserFile, display: render): the chat with the mood in the toolbar and the footers, the memories page with a bot-recorded memory, the personality page after real trait changes, in light and dark.

When you're done, follow "Finishing" and "Waking the orchestrator" in common.md.
