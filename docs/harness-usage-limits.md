# Harness usage limits

**Factory → Runners** and the **Hand to agent** dialog show how much of each harness's 5-hour
and weekly allowance is used, and when it resets. The page shows when each figure was taken.

- **Codex**: the runner reads the newest Codex session file on the machine every minute,
  including sessions you run yourself. No provider call.
- **Claude Code**: updated from what Claude Code writes during a run. You can opt in to a poll
  of Anthropic's usage endpoint every 5 minutes; see [Live Claude usage](#live-claude-usage).

The quota belongs to the harness account, not the machine. Everything signed in to that account
(other runners, your laptop, claude.ai) uses it, and a fresh reading on any runner is valid for
the account. Two runners signed in to different accounts show different values, and every
organization a machine serves sees the same values for it.

## What each harness reports

Checked on 2026-10-08 against the versions listed.

| Harness | 5-hour | Weekly | Where the runner reads it |
| --- | --- | --- | --- |
| Claude Code (2.1.294) | Yes | Yes | A `rate_limit_event` line in `--output-format stream-json`. `rate_limit_info.unifiedWindows` has `five_hour` and `seven_day`, each with `utilization` (a fraction) and `resetsAt` (Unix seconds). An older event without `unifiedWindows` names one window in `rateLimitType`. |
| Codex CLI (0.160.0) | Yes | Yes | Not in `codex exec --json` output. The session's rollout file, `$CODEX_HOME/sessions/YYYY/MM/DD/rollout-*-<thread id>.jsonl` (default `~/.codex`), has `rate_limits` on each `token_count` event: `primary` (`window_minutes: 300`) and `secondary` (`window_minutes: 10080`), each with `used_percent` and `resets_at` (Unix seconds). The runner reads the last one once Codex exits, and from the newest rollout every minute. |
| GitHub Copilot CLI | No | No | Its usage file reports premium requests, a monthly allowance with no 5-hour or weekly window. |
| Cursor CLI (2026.10.01) | No | No | `stream-json` reports token counts only. |
| OpenCode (1.18.35) | No | No | It talks to many providers; none reports a plan window through OpenCode. |

A harness that reports nothing is listed under **Usage not available**. Aictiq does not estimate a value.

API-key sign-ins (`ANTHROPIC_API_KEY`, an OpenAI API key) have no 5-hour or weekly window, so
those harnesses report nothing either.

## How it travels

1. During a run, the harness adapter picks up the windows (`cli/src/runner/harness/claude.ts`,
   `codex.ts`). A later report replaces an earlier one; a window a report leaves out keeps its
   last value.
2. When the run ends, the runner writes the snapshot to `harness-limits.json` beside
   `runner.json`. That file is the machine's, so a restart keeps the last values.
3. Between runs, `aictiq runner start` refreshes the file before each heartbeat, at most once
   per machine however many organizations it serves (`cli/src/runner/usagePoll.ts`):
   - **Codex**, at most every 60 seconds: the newest `rollout-*.jsonl` under
     `$CODEX_HOME/sessions/` (newest day directories first, the last 7 days). Only the last
     256 KB of the file is read. The reading's time is the `token_count` event's own
     `timestamp`, not the time it was read.
   - **Claude**, every 5 minutes, only when the poll is on (below).

   An older reading never replaces a newer one, whichever way it arrived.
4. Every hello and heartbeat sends the file as `capabilities.usageLimits`, and whether the
   Claude poll is on as `capabilities.claudeUsagePoll`. Runners on an older CLI leave the fields
   out and keep working.

## Live Claude usage

Claude Code only reports usage while it runs, so without the poll a runner's Claude bars only
move after a run on that runner. **Factory → Runners** says so under each runner that offers
Claude and has the poll off.

Turn it on once per machine, as the user the runner runs as:

```bash
aictiq runner usage --claude-oauth on    # off turns it back off
aictiq runner usage                      # the setting and the stored figures
```

The setting is stored as `"claudeUsagePoll": true` in `runner.json`. A running runner picks it
up at its next heartbeat. Turning it on prints this notice:

> The runner will read Claude Code's OAuth access token on this machine and send it only to
> `api.anthropic.com` to read your 5-hour and weekly usage. The token never leaves this machine
> for any other host, is not stored by Aictiq, and is not sent to the Aictiq server. Only the
> percentages and reset times are.

What the poll does:

- Every 5 minutes it reads the token fresh: the macOS Keychain item `Claude Code-credentials`,
  or `.credentials.json` in `$CLAUDE_CONFIG_DIR` (default `~/.claude`). It keeps nothing in memory
  between polls.
- It calls `GET https://api.anthropic.com/api/oauth/usage` with `Authorization: Bearer <token>`
  and `anthropic-beta: oauth-2025-04-20`. The response has `five_hour` and `seven_day`, each
  `{ utilization, resets_at }` or null. `utilization` is already a percentage here, unlike the
  fraction in stream-json.
- It **never refreshes the token**. Refreshing rotates the refresh token and could sign Claude
  Code out. If the token has expired, or Anthropic answers 401 or 403, the poll is skipped and
  logged once. The next use of Claude Code renews the token, and the poll picks it up.
- A 429 or a network error backs off, up to an hour. Any failure (no OAuth sign-in, an API-key
  sign-in, an unexpected response) leaves the stored figures alone and never affects runs or
  the heartbeat.
- The endpoint is undocumented. If its response changes shape, the runner logs the new keys
  once, never their values, and the after-run reading keeps working.

With the poll off (the default), the runner reads nothing under `~/.claude` for credentials and
calls nothing at `api.anthropic.com` outside a run.

## Stale figures

A window is shown as stale, not current, once its reset time has passed or the snapshot is
older than the window it describes: 5 hours for the 5-hour window, 7 days for the weekly one.
A fresh run, a Codex session on that machine, or the Claude poll updates it.
