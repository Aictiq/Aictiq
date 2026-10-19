# @aictiq/cli

The command-line client for [Aictiq](https://github.com/blagoculjak/aictiq) - project
management for software teams and their AI agents. It talks to the same `/api/v1` surface
the web app uses, prints tables for people and `--json` for scripts, and carries a stdio
MCP server so a coding agent can reach the instance without a bearer token in its
configuration file.

```bash
npm install -g @aictiq/cli
aictiq auth login --url https://aictiq.example.com
aictiq item list -p ACME --filter "state:active assignee:@me"
aictiq item claim ACME-123 && aictiq item comment ACME-123 -m "starting"
aictiq run start ACME-124 --playbook implement && aictiq run logs <runId> --follow
```

## The Aictiq skill for coding harnesses

The CLI ships an `aictiq` skill (`skills/aictiq/SKILL.md`) that tells Claude Code, Codex,
Cursor, Copilot and OpenCode how to reach Aictiq: the `aictiq` MCP tools first, the CLI as a
fallback, and how to set up a missing connection. It is installed into the user-level skills
folder of each harness whose config folder exists (`~/.claude/skills/aictiq/SKILL.md`,
`~/.codex/skills/…`, `~/.cursor/skills/…`, `~/.copilot/skills/…`,
`~/.config/opencode/skills/…`; `CLAUDE_CONFIG_DIR`, `CODEX_HOME`, `COPILOT_HOME` and
`XDG_CONFIG_HOME` are honoured), never into a repository.

```bash
aictiq skills install          # install or update it now, and list the paths
aictiq skills install --force  # also replace a copy you edited by hand
```

It also installs on its own: from the `postinstall` script of `npm install -g` (when npm runs
install scripts), on the first command a new CLI version runs (npm 12 and pnpm skip install
scripts by default), and after a runner self-update. The file's last line records the CLI
version and a hash of its content, so the CLI only replaces a copy it wrote and left
unchanged; an edited copy is left alone with a warning. A failed install is only a warning
and never fails `npm install`. Set `AICTIQ_NO_SKILL_INSTALL=1` to turn the automatic installs
off.

## Runners

`aictiq runner` turns a machine into a worker that executes agent runs dispatched from
Factory → Runners:

```bash
aictiq runner register --url https://aictiq.example.com --token jrn_...
aictiq runner start --parallel 2 --tag gpu --tag docker
```

- `--tag <tag>` (repeatable, or comma-separated) labels the runner for routing. Tags are lower
  case letters, digits, `-`, `_`, `.` or `:`, at most 50 characters, at most 20 of them.
  `aictiq runner install-service` accepts `--tag` too and writes it into the service definition.
- `--idle-exit <minutes>` (1-1440) makes `runner start` exit with code 0 once no run has been
  active for that long. It never exits in the middle of a run.

**Fleet mode** (a paid feature of the hosted plans) lets short-lived machines join on their
own. `aictiq runner enroll --url <url> --token <jfe_...> [--name <name>] [--tag <tag>]` trades
an enrollment token for a new runner and stores it like `register` does, marked ephemeral
(the token is read from stdin or `AICTIQ_ENROLL_TOKEN` when `--token` is omitted). On its idle
exit, `runner start` deregisters every ephemeral runner and removes it from `runner.json`.
A typical ephemeral machine runs:

```bash
aictiq runner enroll --url https://app.aictiq.com --token "$JFE_TOKEN" --tag gpu && aictiq runner start --idle-exit 10
```

Full documentation, including exit codes, the configuration precedence and the MCP bridge:
[`docs/cli.md`](../docs/cli.md).

Licensed AGPL-3.0-only.
