# The AI software factory

Aictiq's factory hands a work item to a coding agent and runs it on a machine your
organization controls. The expected result is a branch and a pull request; deployment stays
in your own delivery pipeline.

There are five parts:

| Term | Meaning |
| --- | --- |
| **Agent** | The bot identity that claims the item, comments, commits, and opens the pull request. A person owns every agent. |
| **Runner** | The `aictiq runner` process on a VPS, laptop, or CI host. It starts a supported coding harness. |
| **Run** | One attempt to complete one item as one agent with one playbook. |
| **Playbook** | Reusable project instructions stored on a versioned wiki page, plus a harness, time limit, and success/failure states. |
| **Rule** | An optional trigger that starts a playbook when an item enters a workflow state, optionally only when it has a label. |

The examples below use a Linux VPS and a runner-local checkout. Owners and organization
Admins can operate the factory. A Member needs **Can start AI work** enabled; project Admin
access is needed to configure a project's repository and playbooks.

The app walks the same sequence: open **Get started** from the account menu or the command
palette and it tracks each part below - agent, repository, runner, playbook, handoff,
review - against what the API can actually confirm. Use it to see where you are; use this
page for the commands and the reasoning.

## 1. Prepare the project and agent

Before touching the VPS:

1. Create or enable an agent under **Settings → Agents**, and give it access to the project.
2. Open **Project settings → Factory**. Choose **Runner-local checkout**, set the default
   branch, and optionally choose a default agent. A runner uses the path hint when the path
   lies inside one of its repository roots (`aictiq runner root`); otherwise it needs its own
   project-to-path mapping (`aictiq runner map`).
3. Alternatively, choose **GitHub binding** after connecting a repository under the
   project's **Integrations** settings. A run then receives a short-lived GitHub App
   installation credential for its clone and push.

An item must be visible to the selected agent and must not already be claimed. The database
allows at most one queued, assigned, or running run for an item.

## 2. Prepare a dedicated VPS

A runner has **no sandbox**. The harness can execute commands, read files available to its
operating-system user, and use that user's harness and git credentials. Use a machine (or VM)
dedicated to one trusted organization, and run every setup command below as the same
unprivileged account that will run the service. Do not put unrelated organizations' secrets
or repositories on it.

Install Node.js, Git, the GitHub CLI (`gh`), and Aictiq's CLI:

```bash
npm install -g @aictiq/cli
aictiq --version
```

Install at least one supported harness and sign in as the account that should pay for and own
its runs:

| Harness | Install and authenticate |
| --- | --- |
| Claude Code | Follow [Anthropic's setup guide](https://docs.anthropic.com/en/docs/claude-code/getting-started), then run `claude` and complete sign-in. |
| Codex | Follow [OpenAI's Codex CLI guide](https://developers.openai.com/codex/cli), then run `codex login` (use device authentication if the VPS has no browser). |
| OpenCode | Follow [OpenCode's installation guide](https://opencode.ai/docs), then run `opencode auth login` or use `/connect` in its TUI. |

Verify the selected program is on `PATH` for this account:

```bash
claude --version     # or: codex --version / opencode --version
```

For a runner-local project, clone it once and authenticate Git and `gh` so this same account
can push a branch and open a pull request:

```bash
mkdir -p ~/src
git clone git@github.com:YOUR-ORG/YOUR-REPOSITORY.git ~/src/YOUR-REPOSITORY
gh auth login
```

Do not run the long-lived runner from inside this checkout. It creates a separate worktree
for every run.

## 3. Register and start the runner

Open **Factory → Runners**, choose **Register runner**, name the machine, and copy the `jrn_`
secret. It is shown once. On the VPS:

```bash
aictiq runner register --url https://aictiq.example.com --token jrn_…
aictiq runner root ~/src
aictiq runner status
```

`register` verifies the secret before saving it. `root` and `map` matter only for projects
whose repository source is **Runner-local checkout**. With a root, the runner uses each
project's **Path hint** from the web UI when that path lies inside the root, so a new project
needs no runner change: clone it under the root and set its path hint. Use
`aictiq runner map ACME /path/to/clone` instead when a clone lives outside every root or its
path differs from the hint; a mapping always wins over the hint. `status` should show a valid
registration, the chosen harness, and every required repository or root.

The hint comes from the instance, so the runner only trusts it inside roots named on this
machine. It resolves `~/`, `..` and symlinks before that check, and refuses a hinted directory
whose enclosing git repository is outside the root. The runner reads `runner.json` again for
each run, so new roots and mappings apply without a restart.

### `runner.json` reference

The default path is `~/.config/aictiq/runner.json`. `AICTIQ_CONFIG_HOME` or
`XDG_CONFIG_HOME` changes the base directory. Aictiq creates the directory as `0700` and the
file as `0600`:

```json
{
  "url": "https://aictiq.example.com",
  "token": "jrn_…",
  "name": "factory-vps-1",
  "workspaces": {
    "ACME": "/home/aictiq/src/aictiq",
    "WEB": "/home/aictiq/src/web"
  },
  "repoRoots": ["/home/aictiq/src"],
  "attachments": {
    "maxCount": 25,
    "maxBytes": 26214400
  }
}
```

`url` and `token` are required. `name` is a local label. `workspaces` maps uppercase project
keys to existing git clones. `repoRoots` lists directories under which a project's path hint
is used when it has no `workspaces` entry. Prefer `aictiq runner register`, `aictiq runner map`
and `aictiq runner root` over editing the file; re-registering after a secret rotation
preserves existing mappings and roots. Never
copy this file to a repository or a different organization.

`attachments` limits how much committed item and comment evidence a run downloads beside its
checkout (25 files / 25 MiB by default; zero disables it). The runner records skipped or failed
files as run-log events and never writes these files into `repo/`.

Test interactively first:

```bash
aictiq runner start
```

Then stop it and install it as a systemd user service. `install-service` prints a unit; it
does not write or enable it:

```bash
mkdir -p ~/.config/systemd/user
aictiq runner install-service > ~/.config/systemd/user/aictiq-runner.service
systemctl --user daemon-reload
systemctl --user enable --now aictiq-runner
loginctl enable-linger "$USER"
systemctl --user status aictiq-runner
```

Generate the unit from a shell whose `PATH` finds Node.js, `aictiq`, and every harness: that
path is embedded in the unit. Use `journalctl --user -u aictiq-runner -f` for its local log.
The service finishes runs in flight on its first stop signal; a second signal cancels them.

For more concurrency, generate the unit with `aictiq runner install-service --parallel 2`.
One process supports 1–16 concurrent runs, but the whole process is still one trust domain.

## 4. Write the first playbook

Open **Factory → Playbooks**, find the project, and choose **Create starter**. Aictiq creates
an **Implement** playbook and its editable wiki page. The starter covers reading the full
item, branch and commit naming, claim heartbeats, one editable progress comment, tests, the
pull request, and clean release on an incomplete attempt.

Edit the wiki page for the repository rather than repeating instructions in tickets. A good
playbook states:

- a concrete definition of done, including the expected pull request;
- the exact lint, type-check, unit, integration, and build commands that apply;
- repository conventions, relevant architecture documents, and files that must not change;
- how to validate the result beyond automated tests;
- when to stop and report a blocker instead of guessing; and
- **do not deploy**-the run produces reviewable code, and the delivery pipeline deploys it.

For example:

```markdown
## Definition of done

- Implement only the accepted ticket scope and add regression coverage.
- Run `pnpm lint && pnpm typecheck && pnpm test && pnpm build`.
- Preserve unrelated work and follow `CLAUDE.md` and `docs/security.md`.
- Push the item branch, open a pull request, and link it to the item.
- Do not deploy, merge the pull request, or change production configuration.
```

Choose the harness installed on the runner, a time limit, and the states to use on success
and failure. Make the playbook the project default if CLI/MCP callers should be able to omit
its name. The wiki page is the prompt text, and its wiki permissions are the prompt's access
control: a person who cannot read the page cannot manually start a run from it. Keep secrets
out of playbooks and review their revision history like code.

## 5. Start a run and read its result

Open an unclaimed item and choose **Hand to agent**, then select the playbook and agent. The
equivalent CLI command is:

```bash
aictiq run start ACME-123 --playbook Implement --agent worker
aictiq run logs RUN_ID --follow
```

An orchestrating agent can instead use the MCP tools `start_run`, `get_run`, and `list_runs`.
See [the CLI reference](cli.md) and [the agent guide](agents.md#the-tools).

A run moves through `queued` → `assigned` → `running` and then `succeeded`, `failed`,
`cancelled`, or `timed out`. The run detail page shows runner events, stdout, and stderr as
they arrive. The runner redacts the runner and per-run tokens, but a harness can print other
credentials it reads, so treat logs as sensitive.

The agent must not transition the item itself. When the run becomes terminal, Aictiq links
the reported pull request, leaves one outcome comment, releases the claim, and attempts the
playbook's success or failure transition. If the workflow does not permit that transition,
the run still finishes and the skipped transition is recorded in item history.

Anyone who can see the item can see the run's status and linked pull request. Only a factory
operator can start or cancel runs or read the prompt snapshot, log, and failure reason.

## 6. Add rules when manual runs are reliable

Under **Factory → Rules**, a project Admin can express: “when an item enters this state,
optionally carrying this label, run this playbook as this agent.” Rules use the same dispatch
checks as a manual run. If an item is claimed, already has a live run, or otherwise cannot be
started, the firing is recorded as skipped; it does not steal the item or loop until it wins.

Keep the manual workflow until the playbook is predictable. Then use a narrow trigger state
and label, inspect the rule's firing history, and leave the rule disabled while editing its
workflow or playbook.

## 7. Invite a stakeholder without factory access

Open **Settings → Members → Invite people**, choose **Stakeholder**, select one project, and
send or copy the invitation link. This preset creates an organization Member with project
Member access and factory operation disabled.

A stakeholder can see the project board, create items, comment, and follow a run's visible
status and pull request. They cannot open the Factory area, start or cancel runs, or read a
run's prompt, failure reason, or log. Use the **Team** invitation instead when a colleague
should operate the factory; only a Member's **Can start AI work** flag is optional-Owners and
Admins always can, and Guests never can.

## Retention

Run records stay as item history, with outcome summaries, failure reasons, prompt
snapshots, playbook revision references and linked pull requests. Only the raw log goes:
log chunks for finished runs are deleted in a background sweep.

On a **self-hosted** instance the window is 30 days by default; configure
`Retention:RunLogDays` and `Retention:SweepIntervalMinutes` on the Workers service to
change it. Nothing overrides an operator's choice there.

On the **hosted** service the organization's plan supplies the window - 90 days on the
Hosted offer and its evaluation. Automation asks `IPlanAllowances` per organization
rather than reading a billing table, and a plan with no opinion falls back to the
configured `Retention:RunLogDays`. **A pruned log cannot be recovered by purchasing a
subscription later**: the chunks are deleted, and buying Hosted afterwards does not bring
them back. Everything else about the run survives.

A run log is also capped at 8 MiB by default (`Automation:MaxLogBytes`), independently of
retention; a capped log remains visibly marked as truncated.

Runner workspaces live under `~/.local/share/aictiq/runner/<run-id>/` by default and are
removed after each run. `aictiq runner start --keep-workspaces` is a debugging option, not a
retention policy; clean retained checkouts yourself because they contain repository data.
Attachment files live in the same run directory under `attachments/`, outside the checkout;
they are provisioned with the per-run agent token and are never added to its branch.

## Troubleshooting

| Symptom or failure | Meaning | Fix |
| --- | --- | --- |
| `harness-unavailable` | The run asked for Claude Code, Codex, or OpenCode, but that executable did not work on the runner's service `PATH`. | Run `aictiq runner status` as the service user. Install and sign in to the playbook's harness, regenerate the systemd unit from the correct shell, then start a new run. |
| `no-local-repository` | A Runner-local project has no mapping on this runner and its path hint is not inside a repository root, or the path is not a git repository. | Clone the repository under a root (`aictiq runner root /parent/dir`) and set the project's path hint to it, or run `aictiq runner map PROJECT_KEY /absolute/path`. Confirm with `aictiq runner status`. |
| `runner-lost` | The assigned runner stopped heartbeating (five minutes by default). Aictiq failed the run, revoked its token, and released the item. | Check `journalctl --user -u aictiq-runner`, network access, disk space, and whether the runner secret was disabled or rotated. Restore the runner, then start a new run; the old run does not resume. |
| `timed_out` / timed out | The run exceeded the playbook's time limit. The harness is stopped and the failure path is applied. | Split the item or make the playbook more focused. Raise the playbook limit only when the work legitimately needs it, then start a new run. |
| Run stays queued | No online runner in the organization currently advertises the selected harness. | Check **Factory → Runners** and `aictiq runner status`; start a correctly configured runner. |
| Runner exits with code 5 | Its `jrn_` secret was disabled, deleted, or rotated. Retrying cannot repair the credential. | Register or rotate the runner in Aictiq, then run `aictiq runner register` with the newly shown secret. |
| The run cannot open a pull request | The harness can edit locally but the service account cannot push or use `gh`. | Verify the repository remote, Git/SSH or GitHub App permissions, and `gh auth status` as the runner account. Do not put a long-lived personal token in the playbook. |

For the trust boundaries behind these choices, see the [security model](security.md#factory-runners-and-prompts).
