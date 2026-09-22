# The `aictiq` CLI

`@aictiq/cli` is the command-line client for a Aictiq instance. It talks to the same
`/api/v1` surface the web app uses, prints tables for people and `--json` for scripts, and
carries a stdio MCP server so a coding agent can reach the instance without a bearer token
in its configuration file.

```bash
npm install -g @aictiq/cli     # or: pnpm add -g @aictiq/cli
aictiq auth login --url https://aictiq.example.com
```

## Authenticating

The CLI authenticates with a **personal access token**, not a password. Create one in the
web app under *Settings → Access tokens*; a token used only for reading needs `read`, one
that creates and updates work needs `write`, and `aictiq mcp` needs `mcp` **and** a token
bound to a single organization.

```bash
aictiq auth login --url https://aictiq.example.com          # prompts for the token
echo "$AICTIQ_TOKEN" | aictiq auth login --url https://…    # or pipe it (CI)
aictiq auth login --url https://… --org acme                # pick a default organization
aictiq auth status
aictiq auth logout
```

The token is stored in `~/.config/aictiq/config.json` (`$XDG_CONFIG_HOME` is honoured),
written `0600` inside a `0700` directory. There is deliberately **no OS keychain
integration**: keytar and its successors ship prebuilt native binaries, and a tool whose
whole job is to hold one bearer token should not widen its install surface that far. If
your policy forbids a token on disk, set `AICTIQ_TOKEN` per invocation and never run
`auth login`.

Configuration precedence is **flags → environment → config file**:

| Setting | Flag | Environment | Config key |
| --- | --- | --- | --- |
| Instance URL | `--url` | `AICTIQ_URL` | `url` |
| Token | `--token` | `AICTIQ_TOKEN` | `token` |
| Organization slug | `--org` | `AICTIQ_ORG` | `org` |

`--org` can be left unset when the token is bound to one organization, or when the account
belongs to exactly one; with several visible organizations the CLI asks rather than guesses.

## Commands

```bash
aictiq project list [--archived]

aictiq item list -p ACME --filter "state:active assignee:@me" [--json]
aictiq item view ACME-123 [--comments] [--links]
aictiq item create -p ACME --type story --title "…" [--parent ACME-100] [--body-file f.md]
aictiq item update ACME-123 --priority high --labels backend,+urgent
aictiq item move ACME-123 --state "In Review"
aictiq item move ACME-123 --sprint <sprintId> [--after ACME-120]
aictiq item claim ACME-123 / aictiq item release ACME-123 / aictiq item heartbeat ACME-123
aictiq item comment ACME-123 -m "starting"
aictiq item subtask ACME-123 --title "Write docs" --estimate 3
aictiq item link ACME-123 --pr https://github.com/o/r/pull/7
aictiq item branch ACME-123            # prints acme-123-short-slug

aictiq attachment get <id> [-o path] # bytes to stdout, or a private local file

aictiq sprint list -t <teamKey> -p ACME     # or -t <teamId>
aictiq sprint view <sprintId> [--taskboard]

aictiq wiki tree -p ACME
aictiq wiki get ACME specs/login
aictiq wiki put ACME specs/login --file page.md

aictiq run start ACME-123 [--playbook <name|id>] [--agent <name|id>]
aictiq run list [-p ACME] [--status running] [--agent <id>] [--item ACME-123] [--json]
aictiq run view <runId>
aictiq run logs <runId> [--follow]
aictiq run cancel <runId>

aictiq mcp                            # stdio MCP server, see below
```

`--labels` takes either a plain list, which **replaces** the item's labels, or `+name` /
`-name` adjustments, which add and remove. Mixing the two is refused rather than guessed at.

`item claim` is a compare-and-swap: it sends the version it read, so two agents racing for
the same item produce one claim and one `409` (exit code 3). Hold a claim by calling
`item heartbeat` periodically - a claim whose heartbeat goes stale is reclaimable.

### Output and exit codes

Every command prints a plain-text table by default and RFC-shaped JSON with `--json`.
Tables use two spaces between columns and never truncate the last one, so `grep` and `cut`
keep working. Errors print the problem's `title` and each field error to stderr.

| Code | Meaning |
| --- | --- |
| 0 | Success |
| 1 | Unexpected failure (network, 5xx) |
| 2 | The request was rejected, or the command line was wrong |
| 3 | Conflict (409) - someone else saved first, or the item is already claimed |
| 4 | Not found (404), including anything outside your access |
| 5 | Not authenticated, or the token lacks the scope |

Code 3 is the one worth acting on: re-read the item and retry with the fresh version.

## `aictiq mcp` - the stdio MCP bridge

Aictiq's MCP server is Streamable HTTP at `/mcp`. `aictiq mcp` is a stdio server that
forwards every list and call to it, for clients that only speak stdio or whose
configuration file you would rather not put a token in.

```json
{
  "mcpServers": {
    "aictiq": { "command": "aictiq", "args": ["mcp"] }
  }
}
```

The bridge holds no catalogue of its own - tools, prompts and resources are whatever the
instance advertises - so a tool added to the API is reachable through an unchanged CLI.
Point a client straight at `/mcp` with an `Authorization` header instead if you prefer;
see [agents.md](agents.md).

The `/mcp` endpoint requires the `mcp` scope **and** a token bound to one organization. A
token missing either is refused with a `403`, and the CLI says which to fix.

## `aictiq run` - dispatching and watching factory runs

A run hands a work item to an agent under one of the project's playbooks (phase 10). The
`run` group is the terminal's view of the same thing the item page offers, with the same
rules: the caller must be a project Member, a **factory operator** (Owners and Admins
always are; a Member has the flag or not), and the project must not be archived.
For the end-to-end setup, playbook, and outcome workflow, see the
[factory guide](factory.md).

```bash
aictiq run start ACME-123                          # the project's default playbook
aictiq run start ACME-123 --playbook review --agent worker
aictiq run list -p ACME --status running
aictiq run view <runId>
aictiq run logs <runId> --follow
aictiq run cancel <runId>
```

`start` takes a playbook by name (matched case-insensitively among the project's playbooks,
the project being the item key's prefix) or by id, and an agent by display name or id. A
name that matches several is refused with the candidate ids rather than guessed at. It
prints the run's id, status, agent and playbook; `--json` prints the whole run. An item that
is already claimed, or already has a live run, answers `409` - exit code 3 - and a caller
who may not operate the factory gets `403 factory-not-permitted` (exit code 5).

`list` is newest first and filters by project, status (`queued`, `assigned`, `running`,
`succeeded`, `failed`, `cancelled`, `timedOut`), agent id and item key; `view` shows one run
with its timings, outcome, pull request, exit code, cost and token counts. Whoever can see
the item can see its runs.

`logs` prints the run's output from the beginning, `stderr` lines prefixed `[stderr]` and
harness events `[event]`. With `--follow` it polls `GET /runs/{id}/log?after=<last seq>`
every 2 s (the hub is not used from the CLI) and stops once the run is in a terminal state
and one final page has been drained. `(log truncated)` marks a log the server capped. The log
- like the prompt snapshot and the failure reason - needs the factory-operator flag; a
member without it gets `403`.

`cancel` asks the run to stop: the runner ends the harness and the run records `cancelled`
whatever the harness reports. Cancelling a run that has already finished is a `409`, exit
code 3.

## `aictiq runner` - executing factory runs

A runner is a machine that executes agent runs dispatched from the Factory (phase 10). It
authenticates with a `jrn_` runner secret from **Factory → Runners**, stored in its own
`~/.config/aictiq/runner.json` (0600) - never in `config.json`, because a runner's
credential is not a person's.
See the [factory guide](factory.md#3-register-and-start-the-runner) for the complete VPS,
`runner.json`, repository mapping, and systemd setup.

```bash
aictiq runner register --url https://aictiq.example.com --token jrn_… [--name factory-vps]
aictiq runner map ACME ~/src/aictiq    # project key → local clone, for "local" repositories
aictiq runner map ACME --remove
aictiq runner status                  # registration, detected harnesses, mapped repositories
aictiq runner start [--parallel 2] [--keep-workspaces] [--workspace-root <dir>]
aictiq runner install-service [--parallel 2] # prints a systemd user unit
```

`start` detects `claude`, `codex` and `opencode` on `PATH` and reports them, so the instance
only hands over runs the machine can execute. Each run gets
`~/.local/share/aictiq/runner/<run-id>/`: `repo/` is a git worktree of the mapped clone (or
a shallow clone of the project's GitHub repository) on the run's branch, and the prompt and
MCP configuration sit beside it, outside anything the agent could commit. The harness runs
there as the user who started the runner, with `AICTIQ_URL`, `AICTIQ_TOKEN` (the run's own
agent token, revoked when the run ends) and `AICTIQ_ITEM` in its environment. There is no
sandbox: **one runner is one trust domain**.

Committed item attachments (including comment attachments) are downloaded with the run's
short-lived agent token into `<run-dir>/attachments/`, outside `repo/`. `prompt.md` names
each local file, source, type, size and original download URL; image attachments are called
out for inspection. Defaults are 25 files and 25 MiB per run, configurable in `runner.json`
as `attachments.maxCount` and `attachments.maxBytes`; skipped or failed downloads are events,
never failed runs. Files are `0600`, directories are `0700`, and are removed with the workspace.

The runner streams the log (tokens redacted), heartbeats the run and the item, stops the
harness on a cancel or at the run's time limit (SIGTERM to its process group, SIGKILL ten
seconds later), and reports the outcome with the last pull request URL the harness printed
(or `gh pr view` of the branch). The first SIGINT/SIGTERM stops claiming and waits for runs
in flight; a second cancels them. A disabled, deleted or rotated runner exits with code 5.

## Development

```bash
cd cli
pnpm install
pnpm lint && pnpm typecheck && pnpm test && pnpm build
```

`src/api/schema.d.ts` is generated from the API's OpenAPI document by `openapi-typescript`,
and both it and the source document (`cli/openapi/v1.json`) are committed - CI has no API
to point at, and a contract change should be a readable diff. Regenerate after changing
any endpoint:

```bash
dotnet run --project backend/src/AppHost          # in another terminal
cd cli && AICTIQ_URL=http://localhost:5177 pnpm gen:api
pnpm gen:api --offline                            # types only, from the saved document
```

The document describes paths, parameters and **request** bodies; the API's minimal-API
endpoints return untyped `IResult`, so response shapes are declared by hand in
`src/api/views.ts` mirroring the `*View` records in `backend/src/Modules/**/Endpoints`.
When the document gains response schemas, `views.ts` should be replaced with
`components["schemas"][…]`.
