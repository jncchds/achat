# Rules for every AChat turn-envelope worker session

This task is one of a series (T1 to T5) that reworks the bot flow around a structured JSON turn
envelope (`docs/design.md` §4.7). An orchestrator session runs them one at a time, and each task
gets its own worker session. Read this file before your brief, and follow it for the whole task.

## Read first
- `docs/design.md` §4.7 (the spec), §2.4 (personality), §5.3–5.6 (memory), §12b (work queue), and `AGENTS.md` (hard rules).
- `docs/orchestration/turn-envelope/plan.md`, `notes/`, and every file in `reports/`.
- Your global `~/.claude/CLAUDE.md`: stay inside `/mnt/git/achat`; use the built-in tools before scripts; run noisy commands through `node ~/.claude/tools/run.mjs`; never `pkill -f`.

## Git
- Work in a new worktree at `/mnt/git/achat/.claude/worktrees/task-NN`, on a new branch `task/NN-<name>`, based on the **local** branch `worktree-structured-turn-design` (not main). For example: `git worktree add -b task/NN-<name> .claude/worktrees/task-NN worktree-structured-turn-design`, then EnterWorktree with that path.
- Commit as you go, and push your branch at the end.
- Don't touch main or `worktree-structured-turn-design`, don't open PRs, don't merge. The orchestrator merges your branch.
- Migrations only with the tool: `~/.dotnet/tools/dotnet-ef migrations add <Name> --project src/AChat.Infrastructure --startup-project src/AChat.Api`. Never write them by hand.
- Co-author trailer on commits: `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.

## Machine limits
- Other projects' containers share the machine; they aren't yours to stop.
- Run one heavy thing at a time; check `free -m` and `df -h /` before Docker builds.
- Afterwards prune the images and build cache you created, delete `bin`/`obj`/`node_modules` you no longer need if disk runs low, and run `dotnet build-server shutdown`.
- The app runs only in Docker (`docker compose up -d --build`); compile-only checks are `dotnet build AChat.slnx`, `dotnet test AChat.slnx`, and `npx tsc -b --noEmit` in `src/achat-ui` (run `npm ci` there first in a fresh worktree). Manual DB access is read-only.
- E2E: see `AGENTS.md` and `scripts/e2e/` (`mockllm.mjs` is an OpenAI-compatible mock; a throwaway instance runs on port 5002).

## Product decisions (from the user)
- Mood is conversation state and must not swing (damped; already implemented in T1).
- The bot's own memory and trait changes apply automatically, with guardrails; trait changes are versioned and revertable.
- Traits apply only in the creator's conversation. Directives and core persona are never reachable from the envelope.
- The UI shell and palette stay as they are (matches the user's home UI design). Changes to existing pages follow the existing style; a brand-new view needs a mockup round with the user first.

## Quality bar
- Every endpoint keeps its access checks; focused diffs; keep files' line endings.
- New logic has tests; `dotnet build`, `dotnet test`, and `npx tsc -b --noEmit` all pass.
- A garbled envelope must never lose the user's reply and must apply no side effects.

## Finishing
1. Write `docs/orchestration/turn-envelope/reports/task-NN.md` and commit it. Cover: branch and commits; what was built; endpoints and pages; test results with numbers; what was verified at runtime vs only compiled; decisions made; what's open.
2. Push the branch.
3. End with a short report, and wake the orchestrator as described below.
4. **Don't remove yourself.** Leave your worktree, branch and session as they are. Don't remove the worktree, delete the branch, exit, stop or archive the session, or run `claude stop` on yourself. The orchestrator cleans up after the merge.

## Waking the orchestrator
Do this when you finish, and also when you stop because you're blocked. The orchestrator is the session named **"achat"** (id `1171dc`, session id `80d2bbe0-29f2-45fe-8fd9-d1214b7dd639`), with its worktree at `/mnt/git/achat/.claude/worktrees/structured-turn-design`.
1. Send your report to it with SendMessage. Find its row with ListAgents by id `1171dc` and use that exact name and ref, or reply to the `from` address of its messages to you.
2. Check that it's alive: `claude agents --json | jq '.[] | select(.id=="1171dc")'`.
   - It counts as not running if it's missing, if its state isn't working, idle or blocked, or if the SendMessage failed.
   - Only in that case, resume it with your report as its next prompt: `~/.claude/skills/start-code-session/start.sh /mnt/git/achat --resume 80d2bbe0-29f2-45fe-8fd9-d1214b7dd639 "Report from <your session name>: <your report>"`.
   - Never resume it while it's running, because that starts a copy.
