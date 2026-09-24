import { spawn } from 'node:child_process'
import { chmodSync, mkdirSync, realpathSync, rmSync, writeFileSync } from 'node:fs'
import { homedir } from 'node:os'
import { isAbsolute, join, relative, resolve } from 'node:path'
import { RunFailure, type ClaimedRun, type RunAttachment } from './types.js'

export const DefaultAttachmentMaxCount = 25
export const DefaultAttachmentMaxBytes = 25 * 1024 * 1024

/** The runner's small agent-token-only view of item attachments. */
export interface AttachmentProvider {
  list(): Promise<RunAttachment[]>
  download(attachmentId: string): Promise<Uint8Array>
}

/**
 * Where a run's work happens. Everything the runner writes for a run lives in
 * `<root>/<runId>/`; the harness works in `repo/` below it. The prompt, the MCP config and
 * the askpass script sit beside the checkout rather than in it, so nothing the agent
 * commits can carry them.
 */
export interface WorkspaceOptions {
  root: string
  /** project key → local repository path, from runner.json `workspaces` */
  repositories: Record<string, string>
  /**
   * Directories, from runner.json `repoRoots`, under which the project's path hint is trusted
   * when the project has no explicit mapping. Empty means the hint is never used.
   */
  repoRoots?: string[]
  keep: boolean
  mcpServer: { command: string; args: string[] }
  /** runner milestones, streamed as `event` log chunks */
  event: (line: string) => void
  /** fetches a fresh GitHub installation token when the claimed one is missing/expired (POST /runner/runs/{id}/repo-token) */
  refreshCloneToken?: () => Promise<string | null>
  /** Where `github` repositories are cloned from; tests point it at a local bare repository. */
  githubBaseUrl?: string
  env?: NodeJS.ProcessEnv
  signal?: AbortSignal
  /** Optional because old instances do not yet expose the attachment list endpoint. */
  attachments?: AttachmentProvider
  /** Limits for files outside the checkout. Zero disables provisioning. */
  attachmentMaxCount?: number
  attachmentMaxBytes?: number
}

export interface Workspace {
  runDir: string
  checkout: string
  branch: string
  promptFile: string
  mcpConfigFile: string
  attachmentsDir: string
  /** The source prompt plus the runner-generated attachment inventory. */
  prompt: string
  /** Extra environment for the harness process (the push credential for a `github` clone). */
  env: Record<string, string>
  /** removes the worktree (git worktree remove --force + prune in the source repo) or clone, then the run dir; no-op when keep; never throws */
  cleanup(): Promise<void>
}

export function defaultWorkspaceRoot(env: NodeJS.ProcessEnv = process.env): string {
  const base = env.XDG_DATA_HOME ?? join(homedir(), '.local', 'share')
  return join(base, 'aictiq', 'runner')
}

const TOKEN_ENV = 'AICTIQ_GIT_TOKEN'

// Answers only `get`: git also calls helpers with `store` after a successful push, and the
// token must not end up anywhere but this process's environment.
const CREDENTIAL_HELPER = `!f() { test "$1" = get || return 0; echo username=x-access-token; echo "password=$${TOKEN_ENV}"; }; f`

const ASKPASS_SCRIPT = `#!/bin/sh
case "$1" in
  *[Uu]sername*) echo x-access-token ;;
  *) echo "$${TOKEN_ENV}" ;;
esac
`

export async function provisionWorkspace(
  run: ClaimedRun,
  options: WorkspaceOptions,
): Promise<Workspace> {
  // The run id becomes a directory name; anything path-like in it is refused outright.
  if (!/^[A-Za-z0-9][A-Za-z0-9_-]*$/.test(run.runId)) {
    throw new RunFailure(
      'workspace-failed',
      `Refusing to create a workspace for run id "${run.runId}".`,
    )
  }

  const runDir = join(options.root, run.runId)
  const checkout = join(runDir, 'repo')
  const promptFile = join(runDir, 'prompt.md')
  const mcpConfigFile = join(runDir, 'mcp.json')
  const attachmentsDir = join(runDir, 'attachments')
  const env = options.env ?? process.env

  let source: string | null = null
  const cleanup = async (): Promise<void> => {
    if (options.keep) return
    if (source !== null) {
      await git(['worktree', 'remove', '--force', checkout], { cwd: source, env }).catch(
        () => undefined,
      )
      await git(['worktree', 'prune'], { cwd: source, env }).catch(() => undefined)
    }
    try {
      rmSync(runDir, { recursive: true, force: true })
    } catch {
      // Cleanup is best effort; a leftover directory is not a failed run.
    }
  }

  mkdirSync(options.root, { recursive: true, mode: 0o700 })
  // A leftover directory from an earlier attempt would make `worktree add` or `clone` refuse.
  rmSync(runDir, { recursive: true, force: true })
  mkdirSync(runDir, { mode: 0o700 })
  chmodSync(runDir, 0o700)

  try {
    const mcpConfig = {
      mcpServers: { aictiq: { command: options.mcpServer.command, args: options.mcpServer.args } },
    }
    writePrivate(mcpConfigFile, `${JSON.stringify(mcpConfig, null, 2)}\n`)

    await assertRefName(run.branchName, 'branch', env)
    await assertRefName(run.defaultBranch, 'default branch', env)

    let harnessEnv: Record<string, string> = {}
    if (run.repo.source === 'local') {
      source = await provisionLocal(run, checkout, options, env)
    } else {
      harnessEnv = await provisionGithub(run, runDir, checkout, options, env)
    }

    const prompt = await provisionAttachments(run, attachmentsDir, options)
    writePrivate(promptFile, prompt)

    return {
      runDir,
      checkout,
      branch: run.branchName,
      promptFile,
      mcpConfigFile,
      attachmentsDir,
      prompt,
      env: harnessEnv,
      cleanup,
    }
  } catch (error) {
    await cleanup()
    throw error
  }
}

async function provisionAttachments(run: ClaimedRun, attachmentsDir: string, options: WorkspaceOptions): Promise<string> {
  if (!options.attachments) return run.prompt
  const maxCount = limit(options.attachmentMaxCount, DefaultAttachmentMaxCount)
  const maxBytes = limit(options.attachmentMaxBytes, DefaultAttachmentMaxBytes)
  if (maxCount === 0 || maxBytes === 0) {
    options.event('Attachment provisioning is disabled by this runner configuration')
    return `${run.prompt}\n\n## Attachments\n\nAttachment provisioning is disabled on this runner. Use the attachment download URL from Aictiq if needed.\n`
  }

  let attachments: RunAttachment[]
  try {
    attachments = await options.attachments.list()
  } catch (error) {
    // A missing list endpoint or a transient instance error must not prevent a code run.
    options.event(`Could not list item attachments: ${message(error)}`)
    return run.prompt
  }
  if (attachments.length === 0) return run.prompt

  mkdirSync(attachmentsDir, { recursive: true, mode: 0o700 })
  chmodSync(attachmentsDir, 0o700)
  let total = 0
  const names = new Set<string>()
  const provisioned: Array<RunAttachment & { localName: string }> = []

  for (const attachment of attachments) {
    if (provisioned.length >= maxCount) {
      options.event(`Skipped attachment "${attachment.fileName}" (limit: ${maxCount} files)`)
      continue
    }
    if (!Number.isSafeInteger(attachment.sizeBytes) || attachment.sizeBytes < 0 || total + attachment.sizeBytes > maxBytes) {
      options.event(`Skipped attachment "${attachment.fileName}" (would exceed ${formatBytes(maxBytes)} total)`)
      continue
    }
    try {
      const bytes = await options.attachments.download(attachment.id)
      if (bytes.byteLength > attachment.sizeBytes || total + bytes.byteLength > maxBytes) {
        options.event(`Skipped attachment "${attachment.fileName}" (download exceeds ${formatBytes(maxBytes)} total)`)
        continue
      }
      const name = uniqueFileName(sanitiseFileName(attachment.fileName, attachment.id), names)
      writePrivate(join(attachmentsDir, name), bytes)
      total += bytes.byteLength
      provisioned.push({ ...attachment, localName: name })
    } catch (error) {
      // One unavailable blob is useful information, but never a reason to fail the run.
      options.event(`Failed to download attachment "${attachment.fileName}": ${message(error)}`)
    }
  }

  if (provisioned.length === 0) return run.prompt
  options.event(`Provisioned ${provisioned.length} attachment${provisioned.length === 1 ? '' : 's'}: ${provisioned.map(x => x.localName).join(', ')}`)
  const inventory = provisioned.map((attachment) => {
    const source = attachment.commentId ? `comment ${attachment.commentId}` : 'item description'
    const url = `/api/v1/orgs/${encodeURIComponent(run.organizationSlug)}/attachments/${encodeURIComponent(attachment.id)}/download`
    return `- ${join(attachmentsDir, attachment.localName)} - original: ${attachment.fileName}; ${attachment.contentType}; ${formatBytes(attachment.sizeBytes)}; from ${source}; replaces ${url}`
  }).join('\n')
  const images = provisioned.filter(attachment => attachment.contentType.startsWith('image/'))
  return `${run.prompt}\n\n## Attachments\n\nThese files are outside the git checkout at \`${attachmentsDir}\`; do not add or commit them. ${images.length > 0 ? 'Look at these images before planning.' : ''}\n\n${inventory}\n`
}

function limit(value: number | undefined, fallback: number): number {
  return Number.isSafeInteger(value) && value !== undefined && value >= 0 ? value : fallback
}

function sanitiseFileName(value: string, id: string): string {
  const clean = value.replace(/[\\/\p{Cc}]/gu, '_').trim().replace(/^\.+$/, '')
  return (clean || `attachment-${id}`).slice(0, 200)
}

function uniqueFileName(value: string, existing: Set<string>): string {
  const extension = value.lastIndexOf('.') > 0 ? value.slice(value.lastIndexOf('.')) : ''
  const stem = extension ? value.slice(0, -extension.length) : value
  let candidate = value
  let sequence = 2
  while (existing.has(candidate.toLocaleLowerCase())) candidate = `${stem}-${sequence++}${extension}`
  existing.add(candidate.toLocaleLowerCase())
  return candidate
}

function formatBytes(value: number): string {
  return value >= 1024 * 1024 ? `${(value / (1024 * 1024)).toFixed(1)} MiB` : `${value} bytes`
}

/**
 * The clone a runner-local run works from: the explicit `workspaces` mapping, else the
 * project's path hint when it lies inside one of the runner's `repoRoots`. The hint comes
 * from the server, so it is only trusted inside directories this machine's operator named;
 * symlinks and `..` are resolved before that check. `root` is null for an explicit mapping.
 */
function resolveLocalRepository(
  run: ClaimedRun,
  options: WorkspaceOptions,
): { path: string; root: string | null } {
  const mapped = options.repositories[run.projectKey]
  if (mapped !== undefined) return { path: mapped, root: null }

  const hint = run.repo.localPathHint?.trim()
  const roots = options.repoRoots ?? []
  if (hint) {
    const expanded = expandHome(hint)
    if (isAbsolute(expanded)) {
      const real = realpathOrSelf(expanded)
      for (const configured of roots) {
        const expandedRoot = expandHome(configured)
        if (!isAbsolute(expandedRoot)) continue
        const root = realpathOrSelf(expandedRoot)
        if (isWithin(real, root)) return { path: real, root }
      }
    }
  }

  const example = `("${run.projectKey}": "/path/to/repository")`
  const detail = !hint
    ? ` Add it to the \`workspaces\` map in runner.json ${example}, or set the project's path hint and list its parent directory in \`repoRoots\`.`
    : roots.length === 0
      ? ` The project suggests ${hint}. Add its parent directory to \`repoRoots\` in runner.json (\`aictiq runner root <path>\`), or map it in \`workspaces\` ${example}.`
      : ` The project suggests ${hint}, which is not under any of this runner's repoRoots (${roots.join(', ')}). Add a root that contains it, or map it in \`workspaces\` ${example}.`
  throw new RunFailure(
    'no-local-repository',
    `No local repository is mapped for project ${run.projectKey}.${detail}`,
  )
}

function expandHome(path: string): string {
  if (path === '~') return homedir()
  return path.startsWith('~/') ? join(homedir(), path.slice(2)) : path
}

/** The canonical path; a path that does not exist yet is still normalised (`..`, `.`). */
function realpathOrSelf(path: string): string {
  const normalised = resolve(path)
  try {
    return realpathSync(normalised)
  } catch {
    return normalised
  }
}

/** True when `path` is `root` itself or below it. Both should already be real paths. */
function isWithin(path: string, root: string): boolean {
  const rel = relative(root, path)
  return rel === '' || (!rel.startsWith('..') && !isAbsolute(rel))
}

function message(error: unknown): string {
  return error instanceof Error ? error.message : String(error)
}

async function provisionLocal(
  run: ClaimedRun,
  checkout: string,
  options: WorkspaceOptions,
  env: NodeJS.ProcessEnv,
): Promise<string> {
  const { path: mapped, root } = resolveLocalRepository(run, options)
  const origin = root === null ? 'mapped for project' : 'the path hint for project'

  const toplevel = await git(['rev-parse', '--show-toplevel'], {
    cwd: mapped,
    env,
    signal: options.signal,
  }).catch((error: unknown) => {
    if (isAbort(error)) throw error
    throw new RunFailure(
      'no-local-repository',
      `${mapped}, ${origin} ${run.projectKey}, is not a git repository.`,
    )
  })
  // A hinted directory that is not a clone itself would otherwise resolve to whatever
  // repository encloses it, e.g. a dotfiles repository in the home directory.
  if (root !== null && !isWithin(realpathOrSelf(toplevel.trim()), root)) {
    throw new RunFailure(
      'no-local-repository',
      `${mapped}, the path hint for project ${run.projectKey}, is not a git repository under ${root}.`,
    )
  }
  const repo = toplevel.trim()
  const call = (args: string[]) => gitStep(args, { cwd: repo, env, signal: options.signal })

  const hasOrigin = await git(['remote', 'get-url', 'origin'], {
    cwd: repo,
    env,
    signal: options.signal,
  }).then(
    () => true,
    (error: unknown) => {
      if (isAbort(error)) throw error
      return false
    },
  )
  let base = run.defaultBranch
  if (hasOrigin) {
    options.event(`Fetching origin in ${repo}`)
    await call(['fetch', 'origin'])
    base = `origin/${run.defaultBranch}`
  }

  const branchExists = await git(
    ['rev-parse', '--verify', '--quiet', `refs/heads/${run.branchName}`],
    {
      cwd: repo,
      env,
      signal: options.signal,
    },
  ).then(
    () => true,
    (error: unknown) => {
      if (isAbort(error)) throw error
      return false
    },
  )

  if (branchExists) {
    // A retried run: its branch may already carry the previous attempt's commits.
    await call(['worktree', 'add', checkout, run.branchName])
    options.event(`Created worktree ${checkout} on existing branch ${run.branchName}`)
  } else {
    // --no-track: the new branch must not have the default branch as its upstream, or a bare
    // `git push` from the agent could land on it.
    await call(['worktree', 'add', '--no-track', '-b', run.branchName, checkout, base])
    options.event(`Created worktree ${checkout} on branch ${run.branchName} from ${base}`)
  }
  return repo
}

async function provisionGithub(
  run: ClaimedRun,
  runDir: string,
  checkout: string,
  options: WorkspaceOptions,
  env: NodeJS.ProcessEnv,
): Promise<Record<string, string>> {
  const full = run.repo.repoFullName
  if (!full || !/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(full)) {
    throw new RunFailure(
      'workspace-failed',
      `The run names no valid GitHub repository (${full ?? 'none'}).`,
    )
  }

  const token = run.repo.cloneToken ?? (await options.refreshCloneToken?.()) ?? null
  if (!token) {
    throw new RunFailure(
      'repository-token-unavailable',
      `No GitHub installation token is available to clone ${full}. Check that the GitHub App is still installed on the repository.`,
    )
  }

  // The token reaches git only through the environment: the askpass script prints it when
  // asked, so it is never in argv (readable through /proc), the remote URL or .git/config.
  const askpass = join(runDir, 'askpass.sh')
  writeFileSync(askpass, ASKPASS_SCRIPT, { mode: 0o700 })
  chmodSync(askpass, 0o700)
  const secretEnv: Record<string, string> = { GIT_ASKPASS: askpass, [TOKEN_ENV]: token }
  const cloneEnv = { ...env, ...secretEnv }

  const base = (options.githubBaseUrl ?? 'https://github.com').replace(/\/+$/, '')
  const url = `${base}/${full}.git`
  await gitStep(
    // An empty credential.helper resets the user's own helpers, so a global `store` helper
    // cannot write the token to ~/.git-credentials after the clone succeeds.
    [
      '-c',
      'credential.helper=',
      'clone',
      '--depth',
      '50',
      '--branch',
      run.defaultBranch,
      url,
      checkout,
    ],
    { cwd: runDir, env: cloneEnv, signal: options.signal },
    token,
  )
  options.event(`Cloned ${full} (depth 50)`)

  const call = (args: string[]) =>
    gitStep(args, { cwd: checkout, env, signal: options.signal }, token)
  await call(['checkout', '-b', run.branchName])
  options.event(`Created branch ${run.branchName} from ${run.defaultBranch}`)

  // So the agent can push: a helper that reads the token from the harness's environment.
  await call(['config', '--local', '--replace-all', 'credential.helper', ''])
  await call(['config', '--local', '--add', 'credential.helper', CREDENTIAL_HELPER])

  return { [TOKEN_ENV]: token }
}

function writePrivate(path: string, content: string | Uint8Array): void {
  writeFileSync(path, content, { mode: 0o600 })
  chmodSync(path, 0o600)
}

async function assertRefName(name: string, what: string, env: NodeJS.ProcessEnv): Promise<void> {
  // Also refuses names starting with `-`, which git would otherwise read as an option.
  const valid = await git(['check-ref-format', '--branch', name], { cwd: process.cwd(), env }).then(
    () => true,
    () => false,
  )
  if (!valid)
    throw new RunFailure(
      'workspace-failed',
      `The run's ${what} "${name}" is not a valid git branch name.`,
    )
}

interface GitOptions {
  cwd: string
  env: NodeJS.ProcessEnv
  signal?: AbortSignal | undefined
}

class GitError extends Error {
  readonly stderr: string
  constructor(args: string[], code: number | null, stderr: string) {
    super(
      `git ${args[0] === '-c' ? (args[2] ?? '') : (args[0] ?? '')} exited with ${code ?? 'a signal'}`,
    )
    this.stderr = stderr
  }
}

/** A step whose failure fails the run, with git's own words in the message. */
async function gitStep(args: string[], options: GitOptions, secret?: string): Promise<string> {
  try {
    return await git(args, options)
  } catch (error) {
    if (error instanceof GitError) {
      const tail = error.stderr.trim().split('\n').slice(-20).join('\n')
      let message = tail ? `${error.message}:\n${tail}` : error.message
      if (secret) message = message.replaceAll(secret, '***')
      throw new RunFailure('workspace-failed', message)
    }
    throw error
  }
}

function git(args: string[], options: GitOptions): Promise<string> {
  return new Promise((resolve, reject) => {
    const child = spawn('git', args, {
      cwd: options.cwd,
      env: { ...options.env, GIT_TERMINAL_PROMPT: '0' },
      stdio: ['ignore', 'pipe', 'pipe'],
      signal: options.signal,
    })
    let stdout = ''
    let stderr = ''
    child.stdout.setEncoding('utf8').on('data', (chunk: string) => (stdout += chunk))
    child.stderr.setEncoding('utf8').on('data', (chunk: string) => {
      // Only the tail is ever reported; a long fetch must not grow this without bound.
      stderr = (stderr + chunk).slice(-16_384)
    })
    child.on('error', reject)
    child.on('close', (code) => {
      if (code === 0) resolve(stdout)
      else reject(new GitError(args, code, stderr))
    })
  })
}

function isAbort(error: unknown): boolean {
  return error instanceof Error && error.name === 'AbortError'
}
