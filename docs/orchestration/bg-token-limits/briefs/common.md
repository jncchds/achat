# Rules for every bg-token-limits worker session

This task is one of a short series (see `plan.md`) fixing background personality jobs that
fail with token-limit errors. An orchestrator session runs them one at a time; each task gets
its own worker session. Read this file before your brief and follow it for the whole task.

## Read first
- `docs/orchestration/bg-token-limits/plan.md`, `notes/`, every file in `reports/`.
- `docs/design.md` §4.4, §4.5, §4.7, §2.4, §12b, and `AGENTS.md` (hard rules).
- `~/.claude/CLAUDE.md`: stay inside `/mnt/git/achat`; built-in tools first; noisy commands through `node ~/.claude/tools/run.mjs`; never `pkill -f`.

## Git
- Work in a new worktree `/mnt/git/achat/.claude/worktrees/task-NN` on `task/NN-<name>`, based on the **local** branch `worktree-bg-job-token-limits`: `git worktree add -b task/NN-<name> .claude/worktrees/task-NN worktree-bg-job-token-limits`, then EnterWorktree with that path.
- Commit as you go, push the branch at the end. Don't touch main or the integration branch, no PRs, no merging.
- Migrations only with `~/.dotnet/tools/dotnet-ef migrations add <Name> --project src/AChat.Infrastructure --startup-project src/AChat.Api`. Never by hand.
- Commit trailers: `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.

## Machine limits
- One heavy thing at a time; `free -m` and `df -h /` before Docker builds; afterwards prune images/build cache you created and run `dotnet build-server shutdown`. Never stop other projects' containers.
- App runs only in Docker; compile checks: `dotnet build AChat.slnx`, `dotnet test AChat.slnx`, `npx tsc -b --noEmit` in `src/achat-ui` (`npm ci` first in a fresh worktree). Manual DB access is read-only.
- E2E: `scripts/e2e/` (`mockllm.mjs`, throwaway instance on port 5002). Real-model runs: OpenRouter free models only if a key is available; otherwise say it was not run.

## Quality bar
- Focused diffs, keep line endings, tests for new logic, all green. A failed pass must never lose data or leave a half-applied trait version.

## Finishing
1. Write `docs/orchestration/bg-token-limits/reports/task-NN.md` and commit: branch and commits, what was built, tests (numbers), what was verified at runtime vs compiled, decisions, what's open.
2. Push the branch.
3. End with a short report and wake the orchestrator (below).
4. **Don't remove yourself.** Leave worktree, branch and session; no exit/stop/archive/`claude stop`.

## Waking the orchestrator
Do this when you finish and when you stop blocked. The orchestrator is the session named **"achat"** (id `378d05cf`, session id `378d05cf-486d-4bba-8448-6832863ea4db`), worktree `/mnt/git/achat/.claude/worktrees/bg-job-token-limits`.
1. SendMessage your report to it (find the row with ListAgents by id `378d05cf`, use that exact name/ref).
2. Check it's alive: `claude agents --json | jq '.[] | select(.id=="378d05cf")'`. If missing, not working/idle/blocked, or the send failed, resume it: `~/.claude/skills/start-code-session/start.sh /mnt/git/achat --resume 378d05cf-486d-4bba-8448-6832863ea4db "Report from <your session name>: <report>"`. Never resume it while running.
