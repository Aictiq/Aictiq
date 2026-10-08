# The AI software factory

Aictiq's factory hands a work item to a coding agent and runs it on a machine your
organization controls. By default, the result is a branch and a pull request; playbooks can
also push directly to the default branch. Deployment stays in your own delivery pipeline.

There are five parts:

| Term | Meaning |
| --- | --- |
| **Agent** | The bot identity that claims the item, comments, commits, and opens the pull request. A person owns every agent. |
| **Runner** | The `aictiq runner` process on a VPS, laptop, or CI host. It starts a supported coding harness. |
| **Run** | One attempt to complete one item as one agent with one playbook. |
| **Playbook** | Reusable project instructions, kept as a versioned page in the wiki's Factory section, plus a harness, time limit, and success/failure states. |
| **Rule** | An optional trigger that starts a playbook when an item enters a workflow state, optionally only when it has a label. |

The examples below use a Linux VPS and a runner-local checkout. Owners and organization
Admins can operate the factory. A Member needs **Can start AI work** enabled; project Admin
access is needed to configure a project's repository and playbooks.

The app walks the same sequence: open **Get started** from the account menu or the command
palette and it tracks each part below - agent, repository, runner, playbook, handoff,
review - against what the API can actually confirm. Use it to see where you are; use this
page for the commands and the reasoning.

**Factory → Setup** is the same path for one runner and one project, in more detail. An
animated diagram shows how a run travels from the ticket to your runner and back. Below it,
two checklists (the runner machine and the project) carry the commands for each step and tick
themselves as Aictiq sees them done: the runner's hello and heartbeats confirm the CLI, the
harnesses, that it is online and that a service definition started it; its reported mappings
and repository roots confirm that it can find a runner-local checkout. Only what no server can
see, such as the machine's `gh` sign-in, takes a tick from you, and it is shown as yours. The
guide opens after you register a runner, and after you create a project if you operate the
factory; **Project settings → Factory** links to it too. The Aictiq MCP server and the run's
agent token need no setup: the runner provides both for every run.

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
| Cursor | Run `curl https://cursor.com/install -fsS \| bash` (see [Cursor's CLI guide](https://cursor.com/docs/cli/headless)), then `agent login`, or set `CURSOR_API_KEY` in the runner's environment. Runs use your Cursor plan. |
| GitHub Copilot | Run `npm install -g @github/copilot` (see [GitHub's Copilot CLI guide](https://docs.github.com/en/copilot/how-tos/set-up/install-copilot-cli)), then `copilot login`, or set `COPILOT_GITHUB_TOKEN` (or `GH_TOKEN`) to a fine-grained personal access token with the **Copilot Requests** permission. Needs an active GitHub Copilot plan; every prompt uses premium requests from that user's quota. |

Verify the selected program is on `PATH` for this account:

```bash
claude --version     # or: codex --version / opencode --version / agent --version / copilot --version
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

### One machine, several organizations

A developer who works for several clients can run all of them from one machine. Each
organization registers the machine as its own runner with its own secret. In an organization
with no runner yet, **Use a runner I already have** lists the runners you registered in your
other organizations where you are an Owner or Admin. A machine that runs for several of them
appears once. Pick one, and the command it gives you adds this organization as one more profile
on that machine:

```bash
aictiq runner register --url https://aictiq.example.com --token jrn_…   # adds a profile
aictiq runner root ~/clients/globex --org globex
aictiq runner status                                                     # every organization
```

A running `aictiq runner start` or service picks up the new profile within a few seconds, with
no restart. What the runner can separate, it does:

- **Separate credentials.** Each profile keeps its own `jrn_` secret. The instance still ties
  each secret to one organization, and each run gets an agent token for that run's
  organization only.
- **Separate repositories.** `workspaces` and `repoRoots` belong to each profile. A path hint
  from one organization is never resolved under another organization's roots, and a project
  key used in two organizations maps to two different clones.
- **One organization at a time.** Runs of one organization can run side by side
  (`--parallel`); runs of two organizations never do. This keeps an agent from reading another
  organization's run token from the process table, finding its checkout under the workspace
  root, or pushing with its GitHub token. The machine polls every organization while idle. If
  two runs arrive together, it gives one back to its queue, and it takes that run again as soon
  as the machine is free. After a run, the organization that just ran waits two seconds before
  it claims again, so that one busy queue cannot keep the others waiting.
- **No inherited Aictiq settings.** The harness gets none of the runner's own `AICTIQ_*`
  environment variables, only the run's.

What a profile cannot separate is the operating-system user. Every organization's agent runs
as that user, so it can read that user's files, including `runner.json` with the other
organizations' secrets, their clones, and the harness and `git` sign-ins. Connect only
organizations you trust equally. For clients that must not see each other, use a separate OS
user or VM for each client, each with its own `runner.json` (see
[security](security.md#factory-runners-and-prompts)).

`aictiq runner remove <org>` drops one organization from the machine. Disable or delete its
runner in that organization too, because the secret keeps working until you do.

### `runner.json` reference

The default path is `~/.config/aictiq/runner.json`. `AICTIQ_CONFIG_HOME` or
`XDG_CONFIG_HOME` changes the base directory. Aictiq creates the directory as `0700` and the
file as `0600`:

```json
{
  "machineId": "4d1c7f2e-9b1a-4c3e-8f5d-2a6b7c8d9e0f",
  "name": "factory-vps-1",
  "attachments": {
    "maxCount": 25,
    "maxBytes": 26214400
  },
  "profiles": [
    {
      "organization": "acme",
      "url": "https://aictiq.example.com",
      "token": "jrn_…",
      "workspaces": {
        "ACME": "/home/aictiq/src/aictiq",
        "WEB": "/home/aictiq/src/web"
      },
      "repoRoots": ["/home/aictiq/src"]
    }
  ]
}
```

Each profile is one organization: `url` and `token` are required, and `organization` is the
slug the instance reported on hello. `workspaces` maps uppercase project keys to existing git
clones. `repoRoots` lists directories under which a project's path hint is used when it has no
`workspaces` entry. Both apply only to that profile's runs. `machineId` is random. The runner
reports it to every organization so the web UI can show this machine once; it authorizes
nothing. `name` is a local label, and `attachments` applies to every profile.

Prefer `aictiq runner register`, `aictiq runner map` and `aictiq runner root` (with `--org`
once there are several profiles) over editing the file. Re-registering an organization after a
secret rotation keeps that profile's mappings and roots. A file written before profiles existed,
with `url` and `token` at the top level, is read as one profile and rewritten in this shape on
the next `start`. Never copy this file into a repository.

`attachments` limits how much committed item and comment evidence a run downloads beside its
checkout (25 files / 25 MiB by default; zero disables it). The runner records skipped or failed
files as run-log events and never writes these files into `repo/`.

Test interactively first:

```bash
aictiq runner start
```

Then stop it and install it as a service. `install-service` prints the definition for the
machine it runs on (pass `--platform linux|macos|windows` for another); it does not write or
enable anything. Every variant runs as the user who generated it, with that user's harness
sign-ins and git credentials, and stops for good when the runner secret is revoked.

Every definition sets `AICTIQ_RUNNER_SERVICE=1`, which is how a runner tells **Factory →
Setup** that a service started it. A service installed by an older CLI does not set it;
generate and install the definition again to have that step confirmed.

### Linux (systemd)

```bash
mkdir -p ~/.config/systemd/user
aictiq runner install-service > ~/.config/systemd/user/aictiq-runner.service
systemctl --user daemon-reload
systemctl --user enable --now aictiq-runner
loginctl enable-linger "$USER"
systemctl --user status aictiq-runner
```

Generate the unit from a shell whose `PATH` finds Node.js, `aictiq`, and every harness: that
path is embedded in the unit. The runner also searches the usual per-user install
directories (`~/.local/bin`, `~/.npm-global/bin`, `~/.bun/bin`, `~/.opencode/bin`, pnpm's home)
on every heartbeat, so a harness installed there later is found without regenerating it. Run
`aictiq runner status` as the service user to see the harnesses it did not find, why, and the
`PATH` it searched. Factory → Runners lists only the harnesses a runner offers: a harness you do
not use is not reported as missing. Use `journalctl --user -u aictiq-runner -f` for its local log.
The service finishes runs in flight on its first stop signal; a second signal cancels them.

### macOS (launchd)

```bash
mkdir -p ~/Library/LaunchAgents
aictiq runner install-service > ~/Library/LaunchAgents/com.aictiq.runner.plist
launchctl bootstrap gui/$(id -u) ~/Library/LaunchAgents/com.aictiq.runner.plist
launchctl print gui/$(id -u)/com.aictiq.runner
tail -f ~/Library/Logs/aictiq-runner.log
```

The agent starts at login and runs while you are logged in. launchd restarts it after a
crash, and a stop (`launchctl bootout gui/$(id -u)/com.aictiq.runner`) lets runs in flight
finish for up to 15 minutes. As on Linux, generate it from a shell whose `PATH` finds Node.js
and every harness.

### Windows (Task Scheduler)

In PowerShell:

```powershell
aictiq runner install-service > install-runner.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File install-runner.ps1
Get-Content -Wait "$env:LOCALAPPDATA\aictiq\runner.log"
```

The installer writes `%LOCALAPPDATA%\aictiq\runner-service.ps1` and registers an
**Aictiq runner** task that starts it at logon and runs while you are logged in. That script
restarts the runner 10 seconds after it exits and writes the log. Stopping the task ends the
runner at once, so runs in flight are cancelled rather than finished. Remove it with
`Unregister-ScheduledTask -TaskName 'Aictiq runner' -Confirm:$false`. The runner's git
credential scripts need Git for Windows, which runs them through its bundled `sh`.

The runner upgrades itself from npm between runs and exits with code 75 to be restarted on
the new version; every definition above restarts on it. Turn that off with `"autoUpdate": false`
in `runner.json` and update with `aictiq runner update` instead. See
[the CLI reference](cli.md#aictiq-runner---executing-factory-runs).

For more concurrency, generate the definition with `aictiq runner install-service --parallel 2`.
One process supports 1–16 concurrent runs, but the whole process is still one trust domain.

## 4. Write the first playbook

Open **Factory → Playbooks**, find the project, and choose **Create starter**. Aictiq creates
an **Implement** playbook. The starter covers reading the full
item, branch and commit naming, claim heartbeats, one editable progress comment, tests, the
pull request, and clean release on an incomplete attempt. **New playbook** starts from a blank
page: write the instructions in the playbook dialog.

The **Delivery** setting defaults to **Branch and pull request**. For solo or small projects,
choose **Push directly to default branch** to commit and push without an item branch or PR.
The target is the project's **Repository → Default branch**, which may be `main` or a
dedicated branch such as `develop`. Both the mode and direct target are captured when a run
is queued, so edits apply to future runs. The runner needs permission to push to that branch;
repository branch protection still applies. Direct local runs use an isolated clone so the
developer can keep the target branch checked out with uncommitted work, so that checkout
needs an `origin` remote to push to; without one the run fails with `no-remote`. Concurrent
pushes can be rejected: agents must fetch, reconcile and check again, never force-push.

Tailor existing instructions that explicitly require an item branch or PR before selecting
direct delivery. The starter follows the run's selected delivery mode. A successful direct
run records its summary and applies the same configured success transition without a PR link.
Use an updated runner for direct delivery; older runners only support the branch-and-PR mode.

Aictiq keeps every playbook's instructions as a page in the project wiki's **Factory**
section, named after the playbook, so their revision history is reviewable there. A playbook
cannot use a page outside that section, such as **Home**, and the wiki refuses to move a
playbook's page out of it. A playbook created before this rule keeps working. The next time
its instructions are saved, they move to a new page in the section and the old page is left
as it was.

Edit the instructions for the repository rather than repeating them in tickets. A good
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
its name. The instructions are the prompt text, and their wiki page's permissions are the
prompt's access control: a person who cannot read the page cannot manually start a run from it. Keep secrets
out of playbooks and review their revision history like code.

## 5. Start a run and read its result

Open an unclaimed item and choose **Hand to agent**, then select the playbook and agent. By
default, the first free runner that has the playbook's harness takes the run. When the
organization has more than one runner, the dialog also offers **Runner**: pick one, and only
that machine takes the run. If it is offline, the run waits in the queue until it comes back.
If an Admin disables or deletes that runner while the run waits, any runner may take it. For
Claude Code and Codex, the dialog also shows how much of the harness's 5-hour and weekly
allowance is used. Codex usage is read on the runner every minute, including your own
interactive Codex sessions. Claude usage is updated after runs, or every 5 minutes once you
turn on the opt-in poll with `aictiq runner usage --claude-oauth on` on that machine.
**Factory → Runners** has the detail per runner and a hint on each runner where the Claude
poll is off. See [Harness usage limits](harness-usage-limits.md). The equivalent CLI command is:

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

### Schedule a run for later

To let the agent work later, for example overnight, turn on the **Start later** toggle on the
right below the runner selection (or Agent) in the Hand to agent dialog. The **Start at** field
is prefilled with 6 hours from now. You enter the time in your browser's timezone, which is
shown beside or below the field. Aictiq stores it in UTC and refuses a
time in the past.

Submitting **Schedule run** creates a `queued` run and reserves the item for the agent, so
nobody can start a second run on it. Turning on the toggle alone does not reserve the item.
Work begins at or after the selected time, when a runner is available. The next free
matching runner takes it, or the chosen runner if you picked one. While the run waits, the item
and run pages show **Scheduled for** and the local start time. Cancel the run from the run
page, as you would any queued run. The sweeper does not treat a waiting scheduled run as stuck.

Over the API, send `scheduledFor` with an offset, such as `"2026-10-02T22:00:00+02:00"`, in
the body of `POST /api/v1/orgs/{org}/items/{key}/runs`. The CLI and rules always start runs
immediately.

### Continue a failed run

The runner records each run's harness session (Claude Code's session, Codex's thread,
OpenCode's session, Cursor's chat, Copilot's session). When a run fails or times out after its
harness started, the runner keeps its checkout, so the run can pick up where the agent stopped
instead of starting over:

- **Continue** on the run page queues a new run on the same runner. The harness resumes the
  same session in the kept checkout, with the agent's earlier edits still there, and is told why
  the previous attempt stopped.
- **Retry** starts a fresh run with the same playbook and agent from a new checkout. A run that
  failed before its harness started, such as `workspace-failed`, offers only Retry.
- A rate limit, an overloaded or unreachable model API, or a harness crash
  (`harness-rate-limited`, `harness-transient`, `harness-crashed`) is continued automatically,
  after 1 minute and then 5 minutes, at most twice per failed run. A cancel, a time limit, or any
  other failure is never continued automatically; Continue stays available on the run page.

A continue run is a run: it counts toward plan limits and billing, and it is refused like any
other dispatch while the organization is read-only or the item is claimed. Only the item's
latest run can be continued. The run page links each run in the chain and adds up its cost and
tokens. If the runner is offline or no longer has the workspace, the continue run fails with
`session-unavailable`; use Retry.

### Steer an agent from a comment

Mention an agent in a comment on the item, for example `@builder the redirect should keep the
query string`, and the agent starts working on what the comment asks. The comment's text is the
instruction. When the run finishes or fails, the agent replies in the comment's thread with what
it changed and a link to the pull request, or with the question it needs answered.

- **First run.** If the agent has never implemented the item, or has only refined it, the mention
  starts a normal implement run with the comment quoted in its prompt. Refine runs are never
  continued.
- **Follow-up.** If the agent has an earlier implement run on the item, the mention starts a
  follow-up of it. The follow-up uses the same playbook and branch and goes to the runner that
  ran the earlier run, which holds its session. When that runner is offline the follow-up waits
  in the queue for it. If the runner is disabled or deleted, any runner may take the follow-up
  and starts a fresh session.
- **One run at a time.** A mention made while a run or someone's claim holds the item waits. It
  starts when the item is free, and several mentions run in the order they were written. A
  mention that waits a day on an item no run holds, for example because a person keeps it
  claimed, is dropped and the agent replies to say so.
- **Workflow.** A mention's run moves the item exactly like any implement run: it is claimed and
  moved to the first Active state, then to the playbook's success or failure state.
- **Who can ask.** A mention starts work only when its author could start the run by hand: a
  project Member with **Operate the factory**. A stakeholder's mention, a Guest's, or another
  agent's only notifies, as before, and stakeholders do not see the agent's replies. Mentions of
  people only notify.
- **History.** The item's **Agent runs** list links a mention's run to its comment, and the run
  page links a follow-up to the run it follows up.

If no run can ever start for a mention, the agent replies with the reason. For example, the
project may have no default playbook or be archived, or the author may have lost factory
access. An edit that adds a new agent mention to a comment counts as a new request; editing the
text of a comment that already mentioned the agent does not start another run.

A follow-up uses the workspace the earlier run kept when its runner still has it: the harness
resumes the earlier session and gets the comment as its next message. When the workspace is gone, the follow-up does not fail; it starts
a fresh session from a new checkout, and its prompt quotes the comment. Before the harness
starts, the runner fetches `origin` and picks the branch:

- The earlier pull request is open, or the run opened none: the runner continues on the earlier
  branch, fast-forwarding the checkout when someone pushed to it. Commits the agent never pushed
  are kept.
- The earlier pull request was merged or closed: the runner creates a new branch from the
  default branch, and the agent pushes it and opens a new pull request.
- The earlier pull request or branch no longer exists: the run fails with
  `follow-up-target-missing` without starting the harness, and the reply to the comment says
  what was missing.

The runner asks `gh pr view` for the pull request's state. Without `gh`, or when it is not
signed in, the runner continues on the earlier branch if it still exists.

### Resume a session by hand

In ItemDetail, under **Agent runs**, each finished run with a session shows **Copy resume
command** for 5 days, next to the name of the runner that ran it. Anyone who can see the item's
runs sees it. The copied command opens the harness in that session, inside the run's checkout on
that runner, so run it on that machine as the runner's user:

| Harness | Command |
| --- | --- |
| Claude Code | `cd '<checkout>' && claude --resume '<session>'` |
| Codex | `cd '<checkout>' && codex resume '<session>'` |
| OpenCode | `cd '<checkout>' && opencode --session '<session>'` |
| Cursor | `cd '<checkout>' && agent --resume '<session>'` |
| GitHub Copilot | `cd '<checkout>' && copilot --resume='<session>'` |

The path and session are quoted for bash, zsh and fish. If the runner already removed the
checkout, `cd` fails. Working by hand while Aictiq continues the same run (Continue or an
automatic continue) writes to the same session and checkout; Aictiq warns about this but does
not block it. Runs from before this feature have no stored checkout and show no command.

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
run's prompt, failure reason, or log. Anything that shows how the factory works is hidden from
them as well: comments written by agents, every reply in a thread an agent started, run
outcome comments and the run's own history entries, and the wiki's **Factory** section with
every page in it, as well as any older playbook page that is still outside it. These stay out of their search
results, notifications, the MCP tools and the agent activity feed too. Use the **Team** invitation instead when a colleague
should operate the factory; only a Member's **Can start AI work** flag is optional-Owners and
Admins always can, and Guests never can.

## 8. Refine tickets

A refine run turns a short description into a complete, implementation-ready ticket. It is an
ordinary run on your runner with a different job: it reads the item, its screenshots and
files, and the code, then rewrites the item's title and description, or asks the questions it
cannot answer itself. It never commits, pushes or opens a pull request.

When refinement is off, ticket details show a notice with **View refinement settings**,
which opens this project's **Ticket refinement** section directly.

To turn it on, open **Project settings → Factory → Ticket refinement** and choose
**Create Refine playbook** (or pick an existing playbook). The starter's instructions say how
a Story, Bug, Epic and Task are written up; edit them like any playbook. The same section
holds the project context every refine run receives in front of its prompt:

| Field | Use it for |
| --- | --- |
| Product description | What the product is, who uses it, the parts tickets usually touch. |
| Ticket-writing instructions | Tone, required sections, how acceptance criteria are phrased. |
| Naming conventions | Title prefixes such as `[DS - ...]` or `[IDA - ...]`. |
| Supported platforms and devices | So a bug report names the platform, or the agent asks for it. |
| Refined tickets go to | The workflow state, and so the board column, a confirmed ticket moves to. |

Refine runs execute as the chosen agent, or the project's default agent. A refine run is meant
to be short: Aictiq's own prompt tells the agent to work from the item and its attachments and
to open the checkout only for what the ticket's wording depends on, so the person who asked is
not left waiting through a full reading of the codebase. If you edit the playbook, keep its
instructions about looking at the code just as bounded.

The **Create ticket** dialog offers templates for the selected type. It selects the type's
default and prefills the description, priority and labels. Choosing another template or type
replaces an untouched prefill; text you have edited is preserved. **No template** clears an
untouched prefill and removes the priority and label defaults. Types without templates start
with an empty description.

Project admins manage templates under **Project settings → Templates**. The built-in Bug
report and User story templates are seeded once; renaming or deleting them persists, even
when every template is deleted. Members, guests and archived projects see the list read-only.

Then, from **Create ticket** on the Items, Backlog or Board page (or the command palette):

1. Choose the type, enter a title, describe the ticket in your own words, and paste or drop
   screenshots and files into the description.
2. Choose **Create**. Aictiq files the item exactly as written and opens its details. To
   refine it, choose **Refine ticket** there; a panel above the description follows the run.
3. When the agent needs input, the panel lists its questions. Answer the ones you can and
   choose **Answer and refine**: the answers travel into the next run's prompt.
4. When the ticket is ready, review it, edit anything, and choose **Confirm ticket**. The
   item moves to the configured state. **Ask for changes** sends a note back for another pass.

Next to **Refine ticket**, **Answer and refine**, **Refine again** and **Try again** you choose the harness
and, when the organization has more than one runner, the runner. The harness starts as the
refine playbook's and applies to that run only; the runner list offers **Any free runner**
and the runners that report the chosen harness (a runner that has not reported yet stays
listed). A run sent to an offline runner waits in the queue until it comes back. Your last
choice is remembered in this browser per project, separately from **Hand to agent**'s, and
falls back to the defaults when the runner is gone or cannot run the harness. Through the
API, `POST .../items/{itemKey}/refinement` takes optional `runnerId` and `harness`
(`claude`, `codex`, `opencode`, `cursor` or `copilot`); without them the run goes to any free runner
with the playbook's harness.

Refinement is available only from an existing ticket's details. A refine run claims the item
without moving or assigning it, works in an isolated clone of the default branch (so the
runner checkout needs an `origin` remote, as for direct
delivery), and leaves no item branch behind for the implement run that may follow. When it
ends, Aictiq releases the claim and leaves the item where it is, whatever the playbook's
success and failure states say. A run that ends without calling `submit_refinement` marks the
refinement failed, so it can be tried again. Starting one needs the same **Can start AI work**
permission as **Hand to agent**. Refinement details, confirmation and project refinement
settings require that permission too; stakeholders do not see or change ticket refinement.

## Retention

Run records stay as item history, with outcome summaries, failure reasons, prompt
snapshots, playbook revision references and linked pull requests. Only the raw log goes:
log chunks for finished runs are deleted in a background sweep.

On a **self-hosted** instance the window is 30 days by default; configure
`Retention:RunLogDays` and `Retention:SweepIntervalMinutes` on the Workers service to
change it. Nothing overrides an operator's choice there.

On the **hosted** service the organization's plan supplies the window - 90 days on the
Hosted offer and its evaluation, 30 days on the free tier (`Billing:FreeTier:RunLogDays`).
An organization that drops from its evaluation to Free has logs older than 30 days pruned
on the next sweep. Automation asks `IPlanAllowances` per organization
rather than reading a billing table, and a plan with no opinion falls back to the
configured `Retention:RunLogDays`. **A pruned log cannot be recovered by purchasing a
subscription later**: the chunks are deleted, and buying Hosted afterwards does not bring
them back. Everything else about the run survives.

A run log is also capped at 8 MiB by default (`Automation:MaxLogBytes`), independently of
retention; a capped log remains visibly marked as truncated.

Runner workspaces live under `~/.local/share/aictiq/runner/<run-id>/` by default. A run that
ended with a harness session, whether it succeeded, failed or was cancelled, keeps its workspace
for 5 days so the session can be continued or resumed by hand; other runs remove theirs when
they finish. A runner holds at most 100 kept workspaces (the oldest go first), and a new run on
a Runner-local item removes that item's kept worktree, because the worktree holds the branch the
new run needs. Keeping every session for 5 days uses more disk on busy runners. A kept checkout
can hold uncommitted work and a push token; it stays in the run directory, which only the
runner's user can read.
`aictiq runner start --keep-workspaces` is a debugging option, not a retention policy; it keeps
every workspace and sweeps none, so clean them yourself because they contain repository data.
Attachment files live in the same run directory under `attachments/`, outside the checkout;
they are provisioned with the per-run agent token and are never added to its branch.

## Troubleshooting

| Symptom or failure | Meaning | Fix |
| --- | --- | --- |
| `harness-unavailable` | The run asked for Claude Code, Codex, OpenCode, Cursor, or GitHub Copilot, but that executable did not work on the runner's service `PATH`. | Run `aictiq runner status` as the service user. Install and sign in to the playbook's harness, regenerate the systemd unit from the correct shell, then start a new run. |
| `no-local-repository` | A Runner-local project has no mapping on this runner and its path hint is not inside a repository root, or the path is not a git repository. | Clone the repository under a root (`aictiq runner root /parent/dir`) and set the project's path hint to it, or run `aictiq runner map PROJECT_KEY /absolute/path`. Confirm with `aictiq runner status`. |
| `no-remote` | A direct-delivery run's Runner-local checkout has no `origin` remote. The agent works in an isolated clone that is removed after the run, so without a remote its commits would be lost. | Add the remote (`git remote add origin <url>`) in the mapped checkout, or switch the playbook's **Delivery** to **Branch and pull request**, which works in a local-only repository. |
| `runner-lost` | The assigned runner stopped heartbeating (five minutes by default). Aictiq failed the run, revoked its token, and released the item. | Check `journalctl --user -u aictiq-runner`, network access, disk space, and whether the runner secret was disabled or rotated. Restore the runner, then start a new run; the old run does not resume. |
| `harness-rate-limited`, `harness-transient`, `harness-crashed` | The harness hit a rate limit, the model API was overloaded or unreachable, or the harness died on its own. | Aictiq continues the run automatically up to twice. After that, use **Continue** once the limit resets, or **Retry**. |
| `harness-not-authenticated` | The harness is not signed in as the runner's user (so far reported by Cursor: `Authentication required` or an invalid `CURSOR_API_KEY`; and by GitHub Copilot: not signed in, or the account has no Copilot entitlement). | Cursor: run `agent login` (or `agent status`) as the runner's user, or set a valid `CURSOR_API_KEY` in the service environment. GitHub Copilot: run `copilot login` as the runner's user, or set `COPILOT_GITHUB_TOKEN` / `GH_TOKEN` to a token with the **Copilot Requests** permission, and check that the account has an active Copilot plan. Then **Retry**. |
| `harness-model-unavailable` | The harness rejected the model it was asked to use (GitHub Copilot: `Model "x" from --model flag is not available`). | Check `agent models` for the models your Cursor plan allows, or `/model` in `copilot` for the models your Copilot plan allows. |
| `session-unavailable` | A continue run could not resume: its runner was offline or gone, or it no longer had the kept workspace (older than 5 days, or removed), or the harness no longer had the session (GitHub Copilot: `No session, task, or name matched`). | Use **Retry** to start a fresh run. |
| `follow-up-target-missing` | A follow-up's earlier pull request could not be found, or its branch is neither on `origin` nor in the kept checkout. The harness did not start. | Check that the pull request and branch still exist and that `gh auth status` works as the runner's user, then mention the agent again, or start a new run. |
| `timed_out` / timed out | The run exceeded the playbook's time limit. The harness is stopped and the failure path is applied. | Split the item or make the playbook more focused. Raise the playbook limit only when the work legitimately needs it, then **Continue** the run (each continue gets the full limit again) or start a new one. |
| Run stays queued | No online runner in the organization currently advertises the selected harness, or the run was sent to one runner and that runner is offline. | Check **Factory → Runners** and `aictiq runner status`; start a correctly configured runner, or cancel the run and start it again for any free runner. |
| Runner exits with code 5 | Its `jrn_` secret was disabled, deleted, or rotated. Retrying cannot repair the credential. | Register or rotate the runner in Aictiq, then run `aictiq runner register` with the newly shown secret. |
| The run cannot open a pull request | The harness can edit locally but the service account cannot push or use `gh`. | Verify the repository remote, Git/SSH or GitHub App permissions, and `gh auth status` as the runner account. Do not put a long-lived personal token in the playbook. |

For the trust boundaries behind these choices, see the [security model](security.md#factory-runners-and-prompts).
