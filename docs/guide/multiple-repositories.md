# Monorepos and multiple repositories

Most projects keep their code in one repository, and the factory works on that repository
unchanged. When one ticket spans several repositories (change an API schema in
`backend-service`, then update the SDK in `frontend-app`), or should stay inside one package
of a monorepo (`packages/api` of a Turborepo or Nx workspace), turn on multiple repositories
for the project.

It is an opt-in, paid feature:

- **Paid hosted plans only.** Free, hosted-free and evaluation organizations see an upgrade
  prompt in place of the switch.
- **Not on self-hosted installs.** The option is hidden there, and the API refuses it.
- **Off by default.** Until a project Admin turns it on, Factory settings show a one-line
  note and nothing changes on tickets, playbooks or runs.

## Turn it on

Open **Settings -> Project -> Factory**, choose **Turn on** next to the note under the
repository settings, and save. The **Advanced: multiple repositories / monorepo** section
then lists:

- **The primary repository**: the one already configured above. Give it an **alias** and,
  for a monorepo, a **working directory**.
- **Additional repositories**: each has an alias, a source (a GitHub binding of the project,
  or a runner-local path hint), a default branch and an optional working directory.

An alias is lower-case letters, digits, `.`, `_` and `-`, up to 40 characters, unique in the
project. A run checks each repository out at `repo/<alias>/`.

A working directory is a path inside the repository, such as `packages/api`. The agent
starts there and is told to keep its changes there unless the ticket says otherwise; the
rest of the checkout stays available.

## Which repositories a run sees

Refining a ticket, answering a comment, project chat and **Hand to agent** each check out
every repository of the project side by side, under `repo/<alias>/`. You don't select
repositories with labels. The agent's prompt lists each checkout with its alias, source and
default branch, and the agent picks the ones the work or the question needs.

Refinement, comment answers and project chat stay read-only: they open no pull requests
and change no code.

Labels on the ticket are optional:

| Label | Meaning |
| --- | --- |
| `repo:<alias>` | A hint: the agent is told to start in this repository. |
| `repo:<alias>:after:<dep>` | `<alias>` starts after the `<dep>` step finished. |
| `repo:<alias>:after-merge:<dep>` | `<alias>` starts after the `<dep>` step's pull request merged. |

Ordering labels apply to **Hand to agent** runs only. On the ticket, they replace the
playbook's ordering for that repository. An ordering label or playbook step that names a
repository the project doesn't have stops the run from starting. A plain `repo:<alias>`
label for an unknown repository is ignored.

**On the playbook**, the **Repositories** section (shown only while the project's switch is
on) lists repository steps: a repository, an optional working directory that overrides the
repository's own, the steps it starts after, and whether it waits for their pull requests to
merge or only for their runs to finish. A repository the steps don't list still gets checked
out, in the first run.

## Steps and their order

Repositories that wait for nothing run together, in one run with every checkout side by
side. Without any ordering, that is one run with all of them. A repository that starts after another runs in a later run, once the earlier step
succeeded, or once its pull request merged when the step waits for the merge. The run page
shows every step with its state (pending, waiting for merge, running, succeeded, failed)
and links each step's run and pull requests.

Steps build on [follow-up playbooks](/factory#follow-up-playbooks): a playbook's follow-up
stages run after its last repository step. Single-repository chains work exactly as before.

## Runners

A multi-repository run goes to a runner that can serve every one of its repositories:

- GitHub repositories are cloned per run, with a fresh installation token for each. A
  missing token for any of them fails the run and names the repository.
- A runner-local repository must lie under one of the runner's roots
  (`aictiq runner root <dir>`), or be mapped on that runner. Map the primary as the project
  key and an additional repository as `KEY/alias`:

  ```sh
  aictiq runner map WEB /srv/repos/backend-service --org acme
  aictiq runner map WEB/frontend /srv/repos/frontend-app --org acme
  ```

`aictiq runner map` accepts `KEY/alias` for any additional runner-local repository, and
`aictiq runner map KEY/alias --remove` drops the mapping again. See the
[CLI reference](/cli).

When no online runner has all of them, a runner that has some of them takes the run, as
does a runner the run was sent to. It starts with the repositories it has. The agent is
told which repositories are missing and how to map them. The outcome comment on the ticket
lists the checked-out repositories, and names each missing one with its fix:

```sh
aictiq runner map WEB/frontend /srv/repos/frontend-app --org acme
```

A runner with none of the repositories never takes the run. Starting a run on such a runner
is refused.

A runner that finds no clone of one of the repositories hands the run back with
`no-local-repository` and the repository's alias, and another runner may take it. The
runner needs a CLI recent enough to report multi-repository support. Every kind of run
refuses an older runner. Every checkout is removed when the run ends.

## Delivery

The agent changes only the repositories the work needs. It works on the run's branch in
each of them and opens one pull request per changed repository. The pull requests link to
each other and to the ticket, and all of them appear on the run and on the ticket. A
repository the agent leaves unchanged gets no pull request.

## What stays as it was

- Scheduled runs use the primary repository.
- A follow-up from a comment, and rework a follow-up playbook asked for, work in the
  repositories their earlier run had.

## Downgrades

When an organization leaves its paid plan, new runs of its multi-repository projects use
the primary repository only. The repositories, aliases and playbook steps are kept, and
apply again after an upgrade.
