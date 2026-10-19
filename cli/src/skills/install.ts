import { createHash } from 'node:crypto'
import { existsSync, mkdirSync, readFileSync, renameSync, statSync, writeFileSync } from 'node:fs'
import { homedir } from 'node:os'
import { dirname, join } from 'node:path'
import type { HarnessName } from '../runner/types.js'

export const SkillName = 'aictiq'

/** The skill as the package ships it: `skills/aictiq/SKILL.md` beside `dist/`. */
export function bundledSkillPath(packageRoot: string): string {
  return join(packageRoot, 'skills', SkillName, 'SKILL.md')
}

export interface SkillTarget {
  harness: HarnessName
  /** The harness's user-level config folder; the harness counts as installed when it exists. */
  home: string
  /** `<home>/skills/aictiq/SKILL.md`. */
  path: string
}

/**
 * Where each harness reads user-level skills, honouring the environment variables that move
 * its config folder. Never a repository: every path is under the user's home or config dir.
 */
export function skillTargets(
  env: NodeJS.ProcessEnv = process.env,
  home: string = homedir(),
): SkillTarget[] {
  const configHome = env.XDG_CONFIG_HOME || join(home, '.config')
  const homes: Record<HarnessName, string> = {
    claude: env.CLAUDE_CONFIG_DIR || join(home, '.claude'),
    codex: env.CODEX_HOME || join(home, '.codex'),
    opencode: join(configHome, 'opencode'),
    cursor: join(home, '.cursor'),
    copilot: env.COPILOT_HOME || join(home, '.copilot'),
  }
  return (Object.entries(homes) as [HarnessName, string][]).map(([harness, dir]) => ({
    harness,
    home: dir,
    path: join(dir, 'skills', SkillName, 'SKILL.md'),
  }))
}

const MarkerPattern = /\n<!-- aictiq-skill version=(\S+) sha256=([0-9a-f]{64}) -->\n?$/

function sha256(text: string): string {
  return createHash('sha256').update(text).digest('hex')
}

/**
 * The file the CLI writes: the bundled skill with a last line naming the CLI version and the
 * hash of everything above it, so a later install can tell its own unchanged file from one a
 * person edited.
 */
export function renderSkill(source: string, version: string): string {
  const body = source.endsWith('\n') ? source : `${source}\n`
  return `${body}\n<!-- aictiq-skill version=${version} sha256=${sha256(body)} -->\n`
}

/**
 * What an installed file is: `ours` when its marker's hash still matches its content, with the
 * version that wrote it; `edited` when the marker is missing or the content no longer matches.
 */
export function inspectSkill(
  content: string,
): { kind: 'ours'; version: string } | { kind: 'edited' } {
  const match = MarkerPattern.exec(content)
  if (!match) return { kind: 'edited' }
  const body = content.slice(0, match.index)
  return sha256(body) === match[2] ? { kind: 'ours', version: match[1]! } : { kind: 'edited' }
}

export type SkillStatus =
  /** The harness's config folder does not exist, so the harness is not installed. */
  | 'not-installed'
  | 'created'
  | 'updated'
  | 'unchanged'
  /** A person edited the file (or wrote it); left alone without `force`. */
  | 'edited'
  | 'failed'

export interface SkillResult {
  harness: HarnessName
  path: string
  status: SkillStatus
  /** Why `failed`. */
  error?: string
}

export interface InstallSkillOptions {
  packageRoot: string
  version: string
  /** Overwrite a file a person edited. */
  force?: boolean
  env?: NodeJS.ProcessEnv
  home?: string
}

/**
 * Writes the bundled skill into the skills folder of every harness whose config folder exists.
 * Never throws for one target: a folder it cannot write is reported as `failed` and the rest
 * still install. Throws only when the bundled skill itself is missing.
 */
export function installSkill(options: InstallSkillOptions): SkillResult[] {
  const content = renderSkill(
    readFileSync(bundledSkillPath(options.packageRoot), 'utf8'),
    options.version,
  )
  return skillTargets(options.env, options.home).map((target): SkillResult => {
    const result = (status: SkillStatus, error?: string): SkillResult => ({
      harness: target.harness,
      path: target.path,
      status,
      ...(error ? { error } : {}),
    })
    try {
      if (!isDirectory(target.home)) return result('not-installed')
      let status: SkillStatus = 'created'
      if (existsSync(target.path)) {
        const existing = readFileSync(target.path, 'utf8')
        if (existing === content) return result('unchanged')
        if (inspectSkill(existing).kind === 'edited' && !options.force) return result('edited')
        status = 'updated'
      }
      mkdirSync(dirname(target.path), { recursive: true })
      // Written aside and renamed, so a harness starting meanwhile never reads half a file.
      const temp = `${target.path}.${process.pid}.tmp`
      writeFileSync(temp, content, { mode: 0o644 })
      renameSync(temp, target.path)
      return result(status)
    } catch (error) {
      return result('failed', error instanceof Error ? error.message : String(error))
    }
  })
}

function isDirectory(path: string): boolean {
  try {
    return statSync(path).isDirectory()
  } catch {
    return false
  }
}

/** One line per result worth telling a person about; `not-installed` harnesses are skipped. */
export function describeSkillResult(result: SkillResult): string | null {
  switch (result.status) {
    case 'created':
      return `Installed the Aictiq skill: ${result.path}`
    case 'updated':
      return `Updated the Aictiq skill: ${result.path}`
    case 'unchanged':
      return `The Aictiq skill is up to date: ${result.path}`
    case 'edited':
      return `Warning: ${result.path} was edited, so the Aictiq skill there was not updated; run \`aictiq skills install --force\` to replace it`
    case 'failed':
      return `Warning: could not install the Aictiq skill to ${result.path}: ${result.error}`
    case 'not-installed':
      return null
  }
}

/** Records the CLI version that last installed the skill, so each version installs it once. */
export function skillStampPath(
  env: NodeJS.ProcessEnv = process.env,
  home: string = homedir(),
): string {
  const base = env.AICTIQ_CONFIG_HOME ?? env.XDG_CONFIG_HOME ?? join(home, '.config')
  return join(base, 'aictiq', 'skill-version')
}

/**
 * Installs the skill once per CLI version, from any command. npm 12 blocks a global
 * install's `postinstall` unless the person allows it, and pnpm blocks it by default, so the
 * first command a new version runs is what reliably brings the skill up to date. Returns the
 * results, or null when this version already installed it. Never throws.
 */
export function syncSkillOnce(options: InstallSkillOptions): SkillResult[] | null {
  try {
    const stamp = skillStampPath(options.env, options.home)
    if (existsSync(stamp) && readFileSync(stamp, 'utf8').trim() === options.version) return null
    const results = installSkill({ ...options, force: false })
    try {
      mkdirSync(dirname(stamp), { recursive: true, mode: 0o700 })
      writeFileSync(stamp, `${options.version}\n`)
    } catch {
      // Without a stamp the next command tries again; the results still get reported.
    }
    return results
  } catch {
    return null
  }
}

/** Opt-out for the automatic installs (postinstall, first run of a version, runner update). */
export function skillAutoInstallDisabled(env: NodeJS.ProcessEnv = process.env): boolean {
  const value = env.AICTIQ_NO_SKILL_INSTALL?.trim().toLowerCase()
  return value !== undefined && value !== '' && value !== '0' && value !== 'false'
}

/**
 * Whether this CLI is an installed package rather than a source checkout: only an installed
 * CLI installs the skill on its own, so building and testing the CLI never touches the
 * developer's harness folders.
 */
export function isInstalledPackage(packageRoot: string): boolean {
  return packageRoot.split(/[\\/]+/).includes('node_modules')
}
