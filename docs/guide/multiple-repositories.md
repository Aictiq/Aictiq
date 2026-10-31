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
  for a monorepo, a **working directory**. Runs that name no repository use the primary, as
  before.
- **Additional repositories**: each has an alias, a source (a GitHub binding of the project,
  or a runner-local path hint), a default branch and an optional working directory.

An alias is lower-case letters, digits, `.`, `_` and `-`, up to 40 characters, unique in the
project. A run checks each repository out at `repo/<alias>/`.

A working directory is a path inside the repository, such as `packages/api`. The agent
starts there and is told to keep its changes there unless the ticket says otherwise; the
rest of the checkout stays available.

## Choose repositories for a run

The ticket decides first, then the playbook. When neither says, the run uses the primary
repository only.

**On the ticket**, add one label per repository:

| Label | Meaning |
| --- | --- |
| `repo:<alias>` | The run works in this repository. |
| `repo:<alias>:after:<dep>` | Both repositories; `<alias>` starts after the `<dep>` step finished. |
| `repo:<alias>:after-merge:<dep>` | Both repositories; `<alias>` starts after the `<dep>` step's pull request merged. |

Ordering labels on the ticket replace the playbook's ordering for that repository.

**On the playbook**, the **Repositories** section (shown only while the project's switch is
on) lists repository steps: a repository, an optional working directory that overrides the
repository's own, the steps it starts after, and whether it waits for their pull requests to
merge or only for their runs to finish.

## Steps and their order

Repositories that wait for nothing run together, in one run with every checkout side by
side. A repository that starts after another runs in a later run, once the earlier step
succeeded, or once its pull request merged when the step waits for the merge. The run page
shows every step with its state (pending, waiting for merge, running, succeeded, failed)
and links each step's run and pull requests.

Steps build on [follow-up playbooks](/factory#follow-up-playbooks): a playbook's follow-up
stages run after its last repository step. Single-repository chains work exactly as before.

## Runners

A multi-repository run goes only to a runner that can serve every repository it selected:

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

A runner that finds no clone of one of the repositories hands the run back with
`no-local-repository` and the repository's alias, and another runner may take it. The
runner needs a CLI recent enough to report multi-repository support. Every checkout is
removed when the run ends.

## Delivery

The agent works on the run's branch in every repository it changes, and opens one pull
request per changed repository. The pull requests link to each other and to the ticket, and
all of them appear on the run and on the ticket.

## What stays on the primary repository

- Project chat and scheduled runs always use the primary repository.
- Refining a ticket and answering a comment use the primary repository.

## Downgrades

When an organization leaves its paid plan, new runs of its multi-repository projects use
the primary repository only. The repositories, aliases and playbook steps are kept, and
apply again after an upgrade.
