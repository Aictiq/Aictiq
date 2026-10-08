# Harness usage limits

**Factory → Runners** and the **Hand to agent** dialog show how much of each harness's 5-hour
and weekly allowance is used, and when it resets. The runner reads these figures from what each
harness already writes during a run. It never makes an extra call to the provider, so the
figures are as fresh as the last run on that runner, and the page shows when they were taken.

The figures belong to the harness account signed in on the runner's machine. Two runners using
the same harness can therefore show different values, and every organization a machine serves
sees the same values for it.

## What each harness reports

Checked on 2026-10-08 against the versions listed.

| Harness | 5-hour | Weekly | Where the runner reads it |
| --- | --- | --- | --- |
| Claude Code (2.1.294) | Yes | Yes | A `rate_limit_event` line in `--output-format stream-json`. `rate_limit_info.unifiedWindows` has `five_hour` and `seven_day`, each with `utilization` (a fraction) and `resetsAt` (Unix seconds). An older event without `unifiedWindows` names one window in `rateLimitType`. |
| Codex CLI (0.160.0) | Yes | Yes | Not in `codex exec --json` output. The session's rollout file, `$CODEX_HOME/sessions/YYYY/MM/DD/rollout-*-<thread id>.jsonl` (default `~/.codex`), has `rate_limits` on each `token_count` event: `primary` (`window_minutes: 300`) and `secondary` (`window_minutes: 10080`), each with `used_percent` and `resets_at` (Unix seconds). The runner reads the last one once Codex exits. |
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
3. Every hello and heartbeat sends the file as `capabilities.usageLimits`. Runners on an older
   CLI leave the field out and keep working.

## Stale figures

A window is shown as stale, not current, once its reset time has passed or the snapshot is
older than the window it describes: 5 hours for the 5-hour window, 7 days for the weekly one.
A fresh run on that runner updates it.
